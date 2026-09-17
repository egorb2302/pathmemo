using PathMemo.Config;
using PathMemo.Platform;
using PathMemo.Scanning;

namespace PathMemo.Snapshots;

internal static class SnapshotBuilder
{
    /// <summary>
    /// Assembles the parallel scan's per-directory results into the final
    /// Struct-of-Arrays tree.
    /// </summary>
    /// <remarks>
    /// Single-threaded on purpose. Emission is breadth-first, which is what makes a
    /// node's children a contiguous index range - the property that turns "descend into
    /// a directory with 200k entries" into a slice rather than a search. A million array
    /// writes cost a few milliseconds, so there is nothing to gain from parallelising it.
    /// </remarks>
    internal static NodeStore Build(
        ChunkedList<string> dirs,
        ChunkedList<WalkDirResult?> results,
        int[] rootDirIds,
        IReadOnlyList<VolumeInfo> volumes,
        DirectoryLister[] listers)
    {
        var total = CountNodes(results, rootDirIds);

        var parent = new int[total];
        var nameOffset = new int[total];
        var firstChild = new int[total];
        var childCount = new int[total];
        var allocated = new long[total];
        var logical = new long[total];
        var fileCount = new int[total];
        var mtime = new uint[total];
        var attributes = new uint[total];
        var flags = new NodeFlags[total];
        var linkCount = new byte[total];
        var volumeIndex = new byte[total];

        var names = new NameBlobBuilder(Math.Max(1 << 12, total / 8));
        var selfDataPrefix = AppPaths.DataDirectory;

        // dirId -> node index, filled as directories are emitted. A flat array rather
        // than a Dictionary: at 360k directories that is 1.4 MB instead of ~15 MB.
        var nodeOfDir = new int[dirs.Count];
        Array.Fill(nodeOfDir, NodeStore.NoNode);
        var bfs = new Queue<(int DirId, byte Volume)>();
        var next = 0;
        var roots = new int[rootDirIds.Length];

        for (var v = 0; v < rootDirIds.Length; v++)
        {
            var dirId = rootDirIds[v];
            var index = next++;

            roots[v] = index;
            nodeOfDir[dirId] = index;

            parent[index] = NodeStore.NoNode;
            nameOffset[index] = names.Intern(dirs[dirId]);
            flags[index] = NodeFlags.Directory;
            volumeIndex[index] = (byte)v;
            linkCount[index] = 1;

            bfs.Enqueue((dirId, (byte)v));
        }

        while (bfs.Count > 0)
        {
            var (dirId, volume) = bfs.Dequeue();
            var node = nodeOfDir[dirId];
            var result = results[dirId];

            if (result is null || result.Failed)
            {
                flags[node] |= NodeFlags.Incomplete;
                continue;
            }

            var workerBlob = listers[result.BlobId].Blob;

            firstChild[node] = next;
            childCount[node] = result.Entries.Length;

            for (var i = 0; i < result.Entries.Length; i++)
            {
                var entry = result.Entries[i];
                var index = next++;

                parent[index] = node;

                // Re-intern from the worker's blob into the snapshot's single blob. The
                // bytes are already UTF-8, so this is a byte-for-byte copy, not a decode.
                nameOffset[index] = names.Intern(workerBlob.Read(entry.NameOffset));
                allocated[index] = entry.Allocated;
                logical[index] = entry.Logical;
                mtime[index] = entry.Mtime;
                attributes[index] = entry.Attributes;
                flags[index] = entry.Flags;
                linkCount[index] = 1;
                volumeIndex[index] = volume;

                var subdirId = result.SubdirIds[i];
                if (subdirId >= 0)
                {
                    nodeOfDir[subdirId] = index;
                    bfs.Enqueue((subdirId, volume));
                }
            }

            // Consumed: drop the scan-phase entries and the directory path so the peak is
            // not "everything the scan produced" plus "everything the snapshot needs".
            results[dirId] = null;
            dirs[dirId] = null!;
        }

        // Breadth-first emission guarantees child index > parent index, so one reverse
        // pass rolls every subtree total up to its parent.
        for (var i = total - 1; i >= 1; i--)
        {
            var p = parent[i];
            if (p == NodeStore.NoNode) continue;

            allocated[p] += allocated[i];
            logical[p] += logical[i];
            fileCount[p] += (flags[i] & NodeFlags.Directory) != 0 ? fileCount[i] : 1;
        }

        var store = new NodeStore
        {
            Parent = parent,
            NameOffset = nameOffset,
            FirstChild = firstChild,
            ChildCount = childCount,
            Allocated = allocated,
            Logical = logical,
            FileCount = fileCount,
            Mtime = mtime,
            Attributes = attributes,
            Flags = flags,
            LinkCount = linkCount,
            VolumeIndex = volumeIndex,
            NameBlob = names.ToBlob(),
            Roots = roots,
        };

        MarkSelfData(store, selfDataPrefix);
        return store;
    }

    private static int CountNodes(ChunkedList<WalkDirResult?> results, int[] rootDirIds)
    {
        var total = rootDirIds.Length;
        for (var i = 0; i < results.Count; i++)
        {
            var result = results[i];
            if (result is { Failed: false }) total += result.Entries.Length;
        }
        return total;
    }

    /// <summary>
    /// Flags pathmemo's own data directory. It is shown in the tree like anything else -
    /// the tool accounts for itself - but is never offered as a deletion target
    /// (README section 4.6, threat T16).
    /// </summary>
    /// <remarks>
    /// Descends segment by segment from the matching volume root rather than calling
    /// GetPath on every directory, which would be O(directories * depth).
    /// </remarks>
    private static void MarkSelfData(NodeStore store, string dataDirectory)
    {
        var node = FindNode(store, dataDirectory);
        if (node != NodeStore.NoNode) MarkSubtree(store, node);
    }

    private static int FindNode(NodeStore store, string fullPath)
    {
        foreach (var root in store.Roots)
        {
            var rootName = store.Name(root);
            if (!fullPath.StartsWith(rootName, StringComparison.OrdinalIgnoreCase)) continue;

            var node = root;
            var rest = fullPath.AsSpan(rootName.Length);

            foreach (var segmentRange in rest.Split(Path.DirectorySeparatorChar))
            {
                var segment = rest[segmentRange];
                if (segment.IsEmpty) continue;

                var match = NodeStore.NoNode;
                var children = store.Children(node);
                for (var i = children.Start.Value; i < children.End.Value; i++)
                {
                    if (!segment.Equals(store.Name(i), StringComparison.OrdinalIgnoreCase)) continue;
                    match = i;
                    break;
                }

                if (match == NodeStore.NoNode) return NodeStore.NoNode;
                node = match;
            }

            return node;
        }

        return NodeStore.NoNode;
    }

    private static void MarkSubtree(NodeStore store, int node)
    {
        var stack = new Stack<int>();
        stack.Push(node);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            store.Flags[current] |= NodeFlags.SelfData;

            var range = store.Children(current);
            for (var i = range.Start.Value; i < range.End.Value; i++) stack.Push(i);
        }
    }
}
