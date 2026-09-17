using PathMemo.Platform;
using PathMemo.Scanning;
using PathMemo.Snapshots;

namespace PathMemo.Tests;

/// <summary>
/// Builds a <see cref="NodeStore"/> from a list of paths, so a test can state the tree it
/// means instead of filling twelve parallel arrays.
/// </summary>
/// <remarks>
/// The emitted layout matches what the scanners produce: breadth-first order with each
/// directory's children in one contiguous range (README section 5.3). Sizes are then
/// aggregated bottom-up by the production code, not by the test.
/// </remarks>
internal sealed class TestTree
{
    private sealed class Node
    {
        internal required string Name { get; init; }
        internal bool IsFile;
        internal long Bytes;
        internal uint Mtime;
        internal NodeFlags ExtraFlags;
        internal readonly List<Node> Children = [];

        internal Node Child(string name)
        {
            foreach (var child in Children)
                if (child.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return child;

            var created = new Node { Name = name };
            Children.Add(created);
            return created;
        }
    }

    private readonly List<Node> _roots = [];

    /// <summary>Adds a file, creating the directories above it.</summary>
    internal TestTree File(string path, long bytes, NodeFlags flags = NodeFlags.None, uint mtime = 0)
    {
        var node = Locate(path);
        node.IsFile = true;
        node.Bytes = bytes;
        node.ExtraFlags = flags;
        node.Mtime = mtime;
        return this;
    }

    /// <summary>
    /// Adds many siblings at once, for the tests that need a directory too large to spell
    /// out. Appends without the name lookup <see cref="File"/> does, which turns building
    /// a 200 thousand entry directory from quadratic into linear.
    /// </summary>
    internal TestTree Fill(string directory, int count, Func<int, long> bytes)
    {
        var parent = Locate(directory);

        for (var i = 0; i < count; i++)
            parent.Children.Add(new Node
            {
                Name = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"f{i:D6}.bin"),
                IsFile = true,
                Bytes = bytes(i),
            });

        return this;
    }

    /// <summary>Adds an empty directory.</summary>
    internal TestTree Directory(string path)
    {
        Locate(path);
        return this;
    }

    private Node Locate(string path)
    {
        var separator = path.IndexOf(Path.DirectorySeparatorChar);
        var rootName = path[..(separator + 1)];        // "C:\"
        var root = _roots.FirstOrDefault(r => r.Name.Equals(rootName, StringComparison.OrdinalIgnoreCase));

        if (root is null)
        {
            root = new Node { Name = rootName };
            _roots.Add(root);
        }

        var node = root;
        foreach (var segment in path[(separator + 1)..].Split(Path.DirectorySeparatorChar))
            if (segment.Length > 0) node = node.Child(segment);

        return node;
    }

    internal NodeStore Build()
    {
        // Children are emitted level by level, so the indexes of one directory's
        // children are consecutive - the invariant the snapshot format relies on.
        var order = new List<Node>();
        var parents = new List<int>();
        var firstChild = new List<int>();
        var childCount = new List<int>();
        var work = new Queue<(Node Node, int Index)>();
        foreach (var root in _roots)
        {
            order.Add(root);
            parents.Add(NodeStore.NoNode);
            firstChild.Add(NodeStore.NoNode);
            childCount.Add(0);
        }

        for (var i = 0; i < _roots.Count; i++) work.Enqueue((_roots[i], i));

        while (work.Count > 0)
        {
            var (node, index) = work.Dequeue();
            if (node.Children.Count == 0) continue;

            firstChild[index] = order.Count;
            childCount[index] = node.Children.Count;

            foreach (var child in node.Children)
            {
                order.Add(child);
                parents.Add(index);
                firstChild.Add(NodeStore.NoNode);
                childCount.Add(0);
            }

            for (var i = 0; i < node.Children.Count; i++)
                work.Enqueue((node.Children[i], firstChild[index] + i));
        }

        var n = order.Count;
        var names = new NameBlobBuilder(16);
        var store = new NodeStore
        {
            Parent = [.. parents],
            NameOffset = new int[n],
            FirstChild = [.. firstChild],
            ChildCount = [.. childCount],
            Allocated = new long[n],
            Logical = new long[n],
            FileCount = new int[n],
            Mtime = new uint[n],
            Attributes = new uint[n],
            Flags = new NodeFlags[n],
            LinkCount = new byte[n],
            VolumeIndex = new byte[n],
            NameBlob = [],
            Roots = [.. Enumerable.Range(0, _roots.Count)],
        };

        for (var i = 0; i < n; i++)
        {
            var node = order[i];
            store.NameOffset[i] = names.Intern(node.Name);
            store.LinkCount[i] = 1;
            store.Mtime[i] = node.Mtime;
            store.Flags[i] = node.ExtraFlags | (node.IsFile ? NodeFlags.None : NodeFlags.Directory);

            if (!node.IsFile) continue;
            store.Allocated[i] = node.Bytes;
            store.Logical[i] = node.Bytes;
        }

        var built = new NodeStore
        {
            Parent = store.Parent, NameOffset = store.NameOffset, FirstChild = store.FirstChild,
            ChildCount = store.ChildCount, Allocated = store.Allocated, Logical = store.Logical,
            FileCount = store.FileCount, Mtime = store.Mtime, Attributes = store.Attributes,
            Flags = store.Flags, LinkCount = store.LinkCount, VolumeIndex = store.VolumeIndex,
            NameBlob = names.ToBlob(), Roots = store.Roots,
        };

        TreeAssembly.Aggregate(built);
        return built;
    }

    /// <summary>Wraps the tree as a snapshot, the way the diff and history code sees one.</summary>
    internal SnapshotContents Snapshot(
        DateTime startedUtc,
        ScannerKind scanner = ScannerKind.Mft,
        ScanFlags flags = ScanFlags.Elevated,
        long freeBytes = 100L << 30,
        long totalBytes = 500L << 30)
    {
        var tree = Build();
        var volumes = new List<VolumeInfo>();

        foreach (var root in tree.Roots)
        {
            var name = tree.Name(root);
            volumes.Add(new VolumeInfo(
                Root: name,
                Letter: name[..2],
                Label: null,
                FileSystem: "NTFS",
                Serial: 0x1234,
                VolumeGuidPath: null,
                ClusterBytes: 4096,
                TotalBytes: (ulong)totalBytes,
                FreeBytes: (ulong)freeBytes,
                DriveType: DriveType.Fixed));
        }

        return new SnapshotContents
        {
            Tree = tree,
            Volumes = volumes,
            Errors = [],
            Scanner = scanner,
            Flags = flags,
            StartedUtc = startedUtc,
            Duration = TimeSpan.FromSeconds(9),
        };
    }

    internal ScanResult Result(
        DateTime startedUtc,
        ScannerKind scanner = ScannerKind.Mft,
        ScanFlags flags = ScanFlags.Elevated)
    {
        var snapshot = Snapshot(startedUtc, scanner, flags);

        return new ScanResult
        {
            Scanner = snapshot.Scanner,
            Flags = snapshot.Flags,
            StartedUtc = snapshot.StartedUtc,
            Duration = snapshot.Duration,
            Volumes = snapshot.Volumes,
            Tree = snapshot.Tree,
            Errors = [],
        };
    }
}
