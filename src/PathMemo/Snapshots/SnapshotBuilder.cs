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

        // Hard link owners, keyed by (volume, 128-bit file id). Only entries the lister
        // found to have more than one link are keyed, so this stays small (README
        // section 3.2). Breadth-first emission makes the shallowest name the owner:
        // System32\foo.dll owns, WinSxS\...\foo.dll is the alias.
        var owners = new Dictionary<(byte Volume, ulong Low, ulong High), int>();

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

            var links = result.Links;
            var nextLink = 0;

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
                linkCount[index] = entry.LinkCount == 0 ? (byte)1 : entry.LinkCount;
                volumeIndex[index] = volume;

                if (links is not null && nextLink < links.Length && links[nextLink].EntryIndex == i)
                {
                    var link = links[nextLink++];
                    if (!owners.TryAdd((volume, link.IdLow, link.IdHigh), index))
                        flags[index] |= NodeFlags.HardlinkAlias;
                }

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

        TreeAssembly.Aggregate(store);
        TreeAssembly.MarkSelfData(store, selfDataPrefix);
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
}
