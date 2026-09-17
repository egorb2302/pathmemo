namespace PathMemo.Snapshots;

/// <summary>
/// Steps shared by every way of producing a <see cref="NodeStore"/>: the bottom-up
/// totals, the self-data marker, and joining per-volume trees into one.
/// </summary>
internal static class TreeAssembly
{
    /// <summary>
    /// Rolls every subtree total up to its parent. Requires breadth-first node order
    /// (child index greater than parent index), which both scanners guarantee.
    /// </summary>
    /// <remarks>
    /// A hard link alias adds nothing: its bytes were counted where the owner sits
    /// (README section 3.2). The alias node keeps its own size so the details view can
    /// still say "1.2 GB (link)".
    /// </remarks>
    internal static void Aggregate(NodeStore store)
    {
        var parent = store.Parent;
        var allocated = store.Allocated;
        var logical = store.Logical;
        var fileCount = store.FileCount;
        var flags = store.Flags;

        for (var i = store.Count - 1; i >= 0; i--)
        {
            var p = parent[i];
            if (p == NodeStore.NoNode) continue;

            var f = flags[i];
            if ((f & NodeFlags.Directory) != 0)
            {
                allocated[p] += allocated[i];
                logical[p] += logical[i];
                fileCount[p] += fileCount[i];
                continue;
            }

            fileCount[p]++;
            if ((f & NodeFlags.HardlinkAlias) != 0) continue;

            allocated[p] += allocated[i];
            logical[p] += logical[i];
        }
    }

    /// <summary>
    /// Flags pathmemo's own data directory. It is shown in the tree like anything else -
    /// the tool accounts for itself - but is never offered as a deletion target
    /// (README section 4.6, threat T16).
    /// </summary>
    internal static void MarkSelfData(NodeStore store, string dataDirectory)
    {
        var node = Analysis.TreeQuery.Find(store, dataDirectory);
        if (node == NodeStore.NoNode) return;

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

    /// <summary>
    /// Joins trees built separately - one per volume, or by different scanners - into a
    /// single store. Indexes and name offsets are shifted; nothing is re-interned, so
    /// a name used on two volumes is stored twice, which costs kilobytes.
    /// </summary>
    internal static NodeStore Concat(IReadOnlyList<NodeStore> parts)
    {
        if (parts.Count == 1) return parts[0];
        if (parts.Count == 0) return Empty();

        var total = 0;
        var blobLength = 0;
        var rootCount = 0;
        foreach (var part in parts)
        {
            total += part.Count;
            blobLength += part.NameBlob.Length;
            rootCount += part.Roots.Length;
        }

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
        var blob = new byte[blobLength];
        var roots = new int[rootCount];

        var nodeBase = 0;
        var blobBase = 0;
        var rootBase = 0;
        byte volumeBase = 0;

        foreach (var part in parts)
        {
            var n = part.Count;

            for (var i = 0; i < n; i++)
            {
                var at = nodeBase + i;
                parent[at] = part.Parent[i] == NodeStore.NoNode ? NodeStore.NoNode : part.Parent[i] + nodeBase;
                nameOffset[at] = part.NameOffset[i] + blobBase;
                firstChild[at] = part.ChildCount[i] == 0 ? 0 : part.FirstChild[i] + nodeBase;
                volumeIndex[at] = (byte)(part.VolumeIndex[i] + volumeBase);
            }

            part.ChildCount.AsSpan().CopyTo(childCount.AsSpan(nodeBase));
            part.Allocated.AsSpan().CopyTo(allocated.AsSpan(nodeBase));
            part.Logical.AsSpan().CopyTo(logical.AsSpan(nodeBase));
            part.FileCount.AsSpan().CopyTo(fileCount.AsSpan(nodeBase));
            part.Mtime.AsSpan().CopyTo(mtime.AsSpan(nodeBase));
            part.Attributes.AsSpan().CopyTo(attributes.AsSpan(nodeBase));
            part.Flags.AsSpan().CopyTo(flags.AsSpan(nodeBase));
            part.LinkCount.AsSpan().CopyTo(linkCount.AsSpan(nodeBase));
            part.NameBlob.AsSpan().CopyTo(blob.AsSpan(blobBase));

            for (var r = 0; r < part.Roots.Length; r++) roots[rootBase + r] = part.Roots[r] + nodeBase;

            nodeBase += n;
            blobBase += part.NameBlob.Length;
            rootBase += part.Roots.Length;
            volumeBase += (byte)part.Roots.Length;
        }

        return new NodeStore
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
            NameBlob = blob,
            Roots = roots,
        };
    }

    private static NodeStore Empty() => new()
    {
        Parent = [], NameOffset = [], FirstChild = [], ChildCount = [], Allocated = [], Logical = [],
        FileCount = [], Mtime = [], Attributes = [], Flags = [], LinkCount = [], VolumeIndex = [],
        NameBlob = new NameBlobBuilder(1).ToBlob(), Roots = [],
    };
}
