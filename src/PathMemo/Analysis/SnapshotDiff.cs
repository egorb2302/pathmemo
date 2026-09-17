using PathMemo.Snapshots;

namespace PathMemo.Analysis;

internal enum DiffRowKind
{
    /// <summary>A directory that exists in both scans, or appeared, or disappeared.</summary>
    Directory,

    /// <summary>A single file whose own size changed by enough to matter.</summary>
    File,

    /// <summary>
    /// What is left of a directory's change after its listed children are accounted for:
    /// many small files, none of them worth a line of its own.
    /// </summary>
    Residual,
}

/// <summary>One line of the diff. <see cref="Before"/> is null when the entry is new.</summary>
internal sealed record DiffRow(string Path, DiffRowKind Kind, long? Before, long? After)
{
    internal long Delta => (After ?? 0) - (Before ?? 0);
}

/// <summary>The appeared or disappeared side of one volume, as a count and a total.</summary>
internal sealed record ChangeSummary(int Paths, long Bytes, string? LargestFile, long LargestFileBytes);

internal sealed record VolumeDiff
{
    internal required string Letter { get; init; }
    internal required long Before { get; init; }
    internal required long After { get; init; }
    internal long FreeBefore { get; init; }
    internal long FreeAfter { get; init; }
    internal IReadOnlyList<DiffRow> Grew { get; init; } = [];
    internal IReadOnlyList<DiffRow> Shrank { get; init; } = [];
    internal required ChangeSummary Appeared { get; init; }
    internal required ChangeSummary Disappeared { get; init; }

    internal long Delta => After - Before;
    internal long GrewBytes => Grew.Sum(r => r.Delta);
    internal long ShrankBytes => Shrank.Sum(r => r.Delta);

    /// <summary>
    /// The part of the volume's change that no row carries: at every level, a remainder
    /// smaller than that level's threshold is dropped, which is exactly what having a
    /// threshold means. Reported rather than hidden, so the columns add up.
    /// </summary>
    internal long Unexplained => Delta - GrewBytes - ShrankBytes;
}

internal sealed record SnapshotDiffResult
{
    internal required long BeforeId { get; init; }
    internal required long AfterId { get; init; }
    internal required DateTime BeforeUtc { get; init; }
    internal required DateTime AfterUtc { get; init; }
    internal IReadOnlyList<VolumeDiff> Volumes { get; init; } = [];

    /// <summary>Things the reader has to know to trust the numbers: volumes present in only one scan, and so on.</summary>
    internal IReadOnlyList<string> Notes { get; init; } = [];
}

/// <summary>
/// Compares two snapshots (README section 10.2).
/// </summary>
/// <remarks>
/// <para>
/// A merge join per directory on the sorted child names, descending only where the change
/// is big enough to be worth a line: the threshold at each level is
/// <c>max(64 MB, 1% of that level's change)</c>. So a drive that grew by 18 GB reports the
/// one <c>node_modules</c> that did it, not the 400 directories above and below it, and a
/// drive that barely moved still reports the 100 MB that did.
/// </para>
/// <para>
/// Every listed row is disjoint from every other, and they sum to the volume's change.
/// What a level cannot attribute to a child becomes one <see cref="DiffRowKind.Residual"/>
/// row for that directory, which is the honest way to say "a thousand small files here".
/// </para>
/// </remarks>
internal static class SnapshotDiff
{
    /// <summary>Floor for the per-level threshold. Below this, nobody cares.</summary>
    internal const long MinimumInteresting = 64L * 1024 * 1024;

    /// <summary>Nodes the "largest new file" search is allowed to visit, per volume.</summary>
    private const int NewFileSearchBudget = 4_000_000;

    internal static SnapshotDiffResult Compare(
        long beforeId, SnapshotContents before,
        long afterId, SnapshotContents after,
        SizeMode mode = SizeMode.Unique,
        long minimumBytes = MinimumInteresting)
    {
        var volumes = new List<VolumeDiff>();
        var notes = new List<string>();

        for (var b = 0; b < before.Volumes.Count && b < before.Tree.Roots.Length; b++)
        {
            var letter = before.Volumes[b].Letter;
            var match = -1;
            for (var a = 0; a < after.Volumes.Count && a < after.Tree.Roots.Length; a++)
            {
                if (!after.Volumes[a].Letter.Equals(letter, StringComparison.OrdinalIgnoreCase)) continue;
                match = a;
                break;
            }

            if (match < 0)
            {
                notes.Add($"{letter} was scanned in {beforeId} but not in {afterId}");
                continue;
            }

            if (before.Volumes[b].Serial != after.Volumes[match].Serial)
                notes.Add($"{letter} has a different volume serial in the two scans: it is not the same filesystem");

            volumes.Add(CompareVolume(
                before, before.Tree.Roots[b], before.Volumes[b],
                after, after.Tree.Roots[match], after.Volumes[match],
                mode, minimumBytes));
        }

        foreach (var volume in after.Volumes)
        {
            if (before.Volumes.Any(v => v.Letter.Equals(volume.Letter, StringComparison.OrdinalIgnoreCase))) continue;
            notes.Add($"{volume.Letter} was scanned in {afterId} but not in {beforeId}");
        }

        return new SnapshotDiffResult
        {
            BeforeId = beforeId,
            AfterId = afterId,
            BeforeUtc = before.StartedUtc,
            AfterUtc = after.StartedUtc,
            Volumes = volumes,
            Notes = notes,
        };
    }

    private static VolumeDiff CompareVolume(
        SnapshotContents before, int rootBefore, Platform.VolumeInfo volumeBefore,
        SnapshotContents after, int rootAfter, Platform.VolumeInfo volumeAfter,
        SizeMode mode, long minimumBytes)
    {
        var state = new Walk(before.Tree, after.Tree, mode, minimumBytes);
        state.Visit(rootBefore, rootAfter);

        var grew = state.Rows.Where(r => r.Delta > 0).OrderByDescending(r => r.Delta).ToList();
        var shrank = state.Rows.Where(r => r.Delta < 0).OrderBy(r => r.Delta).ToList();

        return new VolumeDiff
        {
            Letter = volumeAfter.Letter,
            Before = TreeQuery.Size(before.Tree, rootBefore, mode),
            After = TreeQuery.Size(after.Tree, rootAfter, mode),
            FreeBefore = (long)volumeBefore.FreeBytes,
            FreeAfter = (long)volumeAfter.FreeBytes,
            Grew = grew,
            Shrank = shrank,
            Appeared = state.Appeared.ToSummary(),
            Disappeared = state.Disappeared.ToSummary(),
        };
    }

    /// <summary>Running totals for one side of the appeared/disappeared report.</summary>
    private sealed class SideTally
    {
        internal int Paths;
        internal long Bytes;
        internal string? LargestFile;
        internal long LargestFileBytes;

        internal ChangeSummary ToSummary() => new(Paths, Bytes, LargestFile, LargestFileBytes);
    }

    private sealed class Walk(NodeStore before, NodeStore after, SizeMode mode, long minimumBytes)
    {
        internal readonly List<DiffRow> Rows = [];
        internal readonly SideTally Appeared = new();
        internal readonly SideTally Disappeared = new();
        private int _budget = NewFileSearchBudget;

        /// <summary>
        /// Compares one directory that exists in both scans, emitting rows for the
        /// children that explain its change and descending into those worth descending
        /// into.
        /// </summary>
        internal void Visit(int nodeBefore, int nodeAfter)
        {
            var sizeBefore = TreeQuery.Size(before, nodeBefore, mode);
            var sizeAfter = TreeQuery.Size(after, nodeAfter, mode);
            var delta = sizeAfter - sizeBefore;

            // A child is worth a line if it accounts for at least 1% of the change being
            // explained here, and for at least the floor. Measuring against the change
            // rather than against the directory's size is deliberate: 1% of a 500 GB
            // volume would hide every movement below 5 GB, which is most of them.
            var threshold = Math.Max(minimumBytes, Math.Abs(delta) / 100);
            var rowsBefore = Rows.Count;

            var childrenBefore = SortedChildren(before, nodeBefore);
            var childrenAfter = SortedChildren(after, nodeAfter);

            long explained = 0;
            var i = 0;
            var j = 0;

            while (i < childrenBefore.Length || j < childrenAfter.Length)
            {
                if (i == childrenBefore.Length)
                {
                    explained += OnlyInAfter(childrenAfter[j++], threshold);
                    continue;
                }

                if (j == childrenAfter.Length)
                {
                    explained += OnlyInBefore(childrenBefore[i++], threshold);
                    continue;
                }

                var order = Compare(before.Name(childrenBefore[i]), after.Name(childrenAfter[j]));

                if (order < 0) { explained += OnlyInBefore(childrenBefore[i++], threshold); continue; }
                if (order > 0) { explained += OnlyInAfter(childrenAfter[j++], threshold); continue; }

                explained += InBoth(childrenBefore[i++], childrenAfter[j++], threshold);
            }

            // Whatever the children could not account for belongs to this directory
            // itself: files that changed a little, each below the threshold.
            var residual = delta - explained;
            if (Math.Abs(residual) < threshold) return;

            Rows.Add(Rows.Count == rowsBefore

                // Nothing below was worth naming, so this directory is the answer, and
                // the report stops here rather than listing a thousand small files.
                ? new DiffRow(after.GetPath(nodeAfter), DiffRowKind.Directory, sizeBefore, sizeAfter)

                // Some children were named and there is still change left over.
                : new DiffRow(after.GetPath(nodeAfter), DiffRowKind.Residual, Before: null, After: residual));
        }

        private long InBoth(int nodeBefore, int nodeAfter, long threshold)
        {
            var isDirectoryBefore = before.IsDirectory(nodeBefore);
            var isDirectoryAfter = after.IsDirectory(nodeAfter);

            // A name that was a file and is now a directory (or the reverse) is two
            // separate events, not a resize.
            if (isDirectoryBefore != isDirectoryAfter)
                return OnlyInBefore(nodeBefore, threshold) + OnlyInAfter(nodeAfter, threshold);

            var sizeBefore = TreeQuery.Size(before, nodeBefore, mode);
            var sizeAfter = TreeQuery.Size(after, nodeAfter, mode);
            var delta = sizeAfter - sizeBefore;

            if (Math.Abs(delta) < threshold) return 0;

            if (!isDirectoryAfter)
            {
                Rows.Add(new DiffRow(after.GetPath(nodeAfter), DiffRowKind.File, sizeBefore, sizeAfter));
                return delta;
            }

            Visit(nodeBefore, nodeAfter);
            return delta;
        }

        private long OnlyInAfter(int node, long threshold)
        {
            var size = TreeQuery.Size(after, node, mode);

            Appeared.Paths++;
            Appeared.Bytes += size;
            TrackLargestFile(after, node, Appeared);

            if (size < threshold) return 0;

            Rows.Add(new DiffRow(after.GetPath(node),
                after.IsDirectory(node) ? DiffRowKind.Directory : DiffRowKind.File,
                Before: null, After: size));
            return size;
        }

        private long OnlyInBefore(int node, long threshold)
        {
            var size = TreeQuery.Size(before, node, mode);

            Disappeared.Paths++;
            Disappeared.Bytes += size;
            TrackLargestFile(before, node, Disappeared);

            if (size < threshold) return 0;

            Rows.Add(new DiffRow(before.GetPath(node),
                before.IsDirectory(node) ? DiffRowKind.Directory : DiffRowKind.File,
                Before: size, After: null));
            return -size;
        }

        /// <summary>
        /// Finds the biggest single file inside an appeared or disappeared subtree. The
        /// headline of a weekly diff is usually one downloaded ISO, and a total of
        /// "6.2 GB in 118 paths" does not name it.
        /// </summary>
        private void TrackLargestFile(NodeStore tree, int node, SideTally tally)
        {
            if (!tree.IsDirectory(node))
            {
                Consider(tree, node, tally);
                return;
            }

            var stack = new Stack<int>();
            stack.Push(node);

            while (stack.Count > 0)
            {
                if (_budget-- <= 0) return;

                var current = stack.Pop();
                if (!tree.IsDirectory(current))
                {
                    Consider(tree, current, tally);
                    continue;
                }

                var children = tree.Children(current);
                for (var i = children.Start.Value; i < children.End.Value; i++) stack.Push(i);
            }
        }

        private void Consider(NodeStore tree, int node, SideTally tally)
        {
            var size = TreeQuery.Size(tree, node, mode);
            if (size <= tally.LargestFileBytes) return;

            tally.LargestFileBytes = size;
            tally.LargestFile = tree.GetPath(node);
        }
    }

    /// <summary>
    /// Child indexes ordered by name, so two directories can be joined in one pass.
    /// Names are materialised once per directory visited, and only directories that
    /// carry a real change are ever visited.
    /// </summary>
    private static int[] SortedChildren(NodeStore tree, int node)
    {
        var count = tree.ChildCount[node];
        if (count == 0) return [];

        var first = tree.FirstChild[node];
        var indexes = new int[count];
        var names = new string[count];
        for (var i = 0; i < count; i++)
        {
            indexes[i] = first + i;
            names[i] = tree.Name(first + i);
        }

        Array.Sort(names, indexes, NameOrder.Instance);
        return indexes;
    }

    private static int Compare(string a, string b) => NameOrder.Instance.Compare(a, b);

    /// <summary>
    /// Case-insensitive ordinal order. NTFS is case-insensitive in practice, and an
    /// invariant comparison keeps the join independent of culture (README section 12.2).
    /// </summary>
    private sealed class NameOrder : IComparer<string>
    {
        internal static readonly NameOrder Instance = new();

        public int Compare(string? x, string? y) =>
            string.Compare(x, y, StringComparison.OrdinalIgnoreCase);
    }
}
