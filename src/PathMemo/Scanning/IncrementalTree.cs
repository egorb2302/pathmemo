using PathMemo.Snapshots;

namespace PathMemo.Scanning;

/// <summary>One entry of a directory that had to be read again, as the live disk reports it.</summary>
/// <remarks>
/// A <c>string</c> name here, unlike <see cref="RawEntry"/>, on purpose: a full scan holds
/// a million of these at once and a rescan holds the contents of a few hundred changed
/// directories, so the allocation the full scan cannot afford is free here - and it makes
/// the rebuild testable without a name blob (README section 17.3).
/// </remarks>
internal readonly record struct LiveEntry(
    string Name, long Logical, long Allocated, uint Mtime, uint Attributes, NodeFlags Flags, byte LinkCount);

/// <summary>
/// Reads one directory level. False means the directory could not be enumerated at all,
/// which the rebuild records as an incomplete subtree rather than as an empty one.
/// </summary>
internal delegate bool LiveDirectoryReader(string path, List<LiveEntry> into, out ScanError? error);

/// <summary>
/// Builds a new tree from a previous one plus the set of directories that changed
/// (README section 4.5).
/// </summary>
/// <remarks>
/// <para>
/// The walk is breadth-first, like both scanners', because that is what makes a node's
/// children a contiguous index range - the property the whole snapshot format rests on
/// (README section 5.3). A directory the journal did not mention is copied across node by
/// node with its name offsets untouched; only the ones it did mention are read from the
/// disk again.
/// </para>
/// <para>
/// A directory that appears in a changed parent with no counterpart in the old tree is
/// read in full, recursively. That is not an optimisation to skip: a subtree <em>moved</em>
/// into place on the same volume emits one rename record and nothing at all for its
/// contents, so "new to the tree" is the only signal that its thousand descendants have
/// never been seen.
/// </para>
/// </remarks>
internal static class IncrementalTree
{
    /// <summary>
    /// Above this many changed directories a full scan is the cheaper answer: each one
    /// costs an enumeration, and forty thousand of those are no longer a delta.
    /// </summary>
    internal const int MaxChangedDirectories = 20_000;

    private readonly record struct Task(int NewNode, int OldNode, string Path, bool Dirty);

    internal static NodeStore Rebuild(
        NodeStore old,
        IReadOnlySet<string> changed,
        LiveDirectoryReader read,
        ICollection<ScanError> errors,
        CancellationToken ct)
    {
        var capacity = old.Count + 4096;

        var parent = new List<int>(capacity);
        var nameOffset = new List<int>(capacity);
        var firstChild = new List<int>(capacity);
        var childCount = new List<int>(capacity);
        var allocated = new List<long>(capacity);
        var logical = new List<long>(capacity);
        var mtime = new List<uint>(capacity);
        var attributes = new List<uint>(capacity);
        var flags = new List<NodeFlags>(capacity);
        var linkCount = new List<byte>(capacity);
        var volumeIndex = new List<byte>(capacity);

        // The old blob is adopted whole, so every copied node keeps the offset it already
        // had and only the changed directories' names are interned.
        var names = new NameBlobBuilder(old.NameBlob);

        var queue = new Queue<Task>();
        var roots = new int[old.Roots.Length];
        var buffer = new List<LiveEntry>(256);

        int Emit(int parentNode, int offset, long alloc, long log, uint time, uint attrs,
                 NodeFlags nodeFlags, byte links, byte volume)
        {
            parent.Add(parentNode);
            nameOffset.Add(offset);
            firstChild.Add(0);
            childCount.Add(0);
            allocated.Add(alloc);
            logical.Add(log);
            mtime.Add(time);
            attributes.Add(attrs);
            flags.Add(nodeFlags);
            linkCount.Add(links);
            volumeIndex.Add(volume);
            return parent.Count - 1;
        }

        for (var v = 0; v < old.Roots.Length; v++)
        {
            var oldRoot = old.Roots[v];
            var path = old.GetPath(oldRoot);

            roots[v] = Emit(NodeStore.NoNode, old.NameOffset[oldRoot], 0, 0,
                old.Mtime[oldRoot], old.Attributes[oldRoot], old.Flags[oldRoot],
                old.LinkCount[oldRoot], old.VolumeIndex[oldRoot]);

            queue.Enqueue(new Task(roots[v], oldRoot, path, changed.Contains(Key(path))));
        }

        while (queue.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var task = queue.Dequeue();

            // A directory nobody touched: its children, and everything under them, are
            // still true. Copy the level and walk on.
            if (task.OldNode != NodeStore.NoNode && !task.Dirty)
            {
                var range = old.Children(task.OldNode);
                firstChild[task.NewNode] = parent.Count;
                childCount[task.NewNode] = range.End.Value - range.Start.Value;

                for (var c = range.Start.Value; c < range.End.Value; c++)
                {
                    // A directory's sizes in the old tree are its subtree's totals, and
                    // those are about to be recomputed from the leaves; carrying them over
                    // would count every untouched file twice.
                    var directory = (old.Flags[c] & NodeFlags.Directory) != 0;

                    var index = Emit(task.NewNode, old.NameOffset[c],
                        directory ? 0 : old.Allocated[c], directory ? 0 : old.Logical[c],
                        old.Mtime[c], old.Attributes[c], old.Flags[c], old.LinkCount[c], old.VolumeIndex[c]);

                    if (!Enterable(old.Flags[c])) continue;

                    var childPath = Join(task.Path, old.Name(c));
                    queue.Enqueue(new Task(index, c, childPath, changed.Contains(Key(childPath))));
                }

                continue;
            }

            if (!read(task.Path, buffer, out var error))
            {
                // Unreadable now, whatever it was before. Saying "incomplete" is the whole
                // difference between a directory of unknown size and one of zero bytes
                // (README section 4.9).
                flags[task.NewNode] |= NodeFlags.Incomplete;
                if (error is not null) errors.Add(error);
                continue;
            }

            var previous = task.OldNode == NodeStore.NoNode ? null : ChildrenByName(old, task.OldNode);
            var volume = volumeIndex[task.NewNode];

            // Read successfully, so whatever the old snapshot thought of this directory,
            // its contents are known now.
            flags[task.NewNode] &= ~NodeFlags.Incomplete;

            firstChild[task.NewNode] = parent.Count;
            childCount[task.NewNode] = buffer.Count;

            foreach (var entry in buffer)
            {
                var index = Emit(task.NewNode, names.Intern(entry.Name), entry.Allocated, entry.Logical,
                    entry.Mtime, entry.Attributes, entry.Flags, entry.LinkCount == 0 ? (byte)1 : entry.LinkCount,
                    volume);

                if (!Enterable(entry.Flags)) continue;

                var childPath = Join(task.Path, entry.Name);
                var oldChild = NodeStore.NoNode;

                if (previous is not null && previous.TryGetValue(entry.Name, out var candidate)
                    && Enterable(old.Flags[candidate]))
                    oldChild = candidate;

                // No counterpart in the old tree means nothing under it has ever been
                // seen, whether it was just created or moved in from elsewhere.
                queue.Enqueue(new Task(index, oldChild, childPath,
                    oldChild == NodeStore.NoNode || changed.Contains(Key(childPath))));
            }
        }

        var store = new NodeStore
        {
            Parent = [.. parent],
            NameOffset = [.. nameOffset],
            FirstChild = [.. firstChild],
            ChildCount = [.. childCount],
            Allocated = [.. allocated],
            Logical = [.. logical],
            FileCount = new int[parent.Count],
            Mtime = [.. mtime],
            Attributes = [.. attributes],
            Flags = [.. flags],
            LinkCount = [.. linkCount],
            VolumeIndex = [.. volumeIndex],
            NameBlob = names.ToBlob(),
            Roots = roots,
        };

        // Directory totals are wholly recomputed rather than adjusted by a delta: one pass
        // over an array is milliseconds, and an adjustment that drifts is a number nobody
        // can check (README section 4.5).
        TreeAssembly.Aggregate(store);
        TreeAssembly.MarkSelfData(store, Config.AppPaths.DataDirectory);
        return store;
    }

    /// <summary>A directory that is not a reparse point: the only thing worth descending into.</summary>
    private static bool Enterable(NodeFlags flags) =>
        (flags & NodeFlags.Directory) != 0 && (flags & NodeFlags.Reparse) == 0;

    /// <summary>
    /// The old children of one directory, by name. Built per changed directory and thrown
    /// away, so the cost is proportional to what changed rather than to the tree.
    /// </summary>
    private static Dictionary<string, int> ChildrenByName(NodeStore old, int node)
    {
        var range = old.Children(node);
        var map = new Dictionary<string, int>(
            range.End.Value - range.Start.Value, StringComparer.OrdinalIgnoreCase);

        for (var c = range.Start.Value; c < range.End.Value; c++) map[old.Name(c)] = c;
        return map;
    }

    /// <summary>
    /// The one spelling both sides of the changed set are compared in.
    /// </summary>
    /// <remarks>
    /// A scan root carries a trailing separator - <c>C:\Users\me\projects\</c> - while the
    /// kernel answers a file id with <c>C:\Users\me\projects</c>. Without one rule for both,
    /// a file created directly in the scan root would never mark it as changed. A volume
    /// root is left alone, because <c>C:</c> and <c>C:\</c> are not the same thing.
    /// </remarks>
    internal static string Key(string path) =>
        path.Length > 3 && path.EndsWith(Path.DirectorySeparatorChar) ? path[..^1] : path;

    /// <summary>Appends a segment, respecting a volume root's own trailing separator.</summary>
    internal static string Join(string directory, string name) =>
        directory.EndsWith(Path.DirectorySeparatorChar)
            ? directory + name
            : directory + Path.DirectorySeparatorChar + name;
}
