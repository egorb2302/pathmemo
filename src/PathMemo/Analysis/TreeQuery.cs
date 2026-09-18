using PathMemo.Snapshots;

namespace PathMemo.Analysis;

/// <summary>Which of the four sizes to report (README section 3.1).</summary>
internal enum SizeMode
{
    /// <summary>On-disk bytes with hard links counted once. The only total that adds up.</summary>
    Unique,

    /// <summary>On-disk bytes as named, hard links counted per name.</summary>
    Allocated,

    /// <summary>Data-stream bytes, i.e. what Explorer shows in a file's properties.</summary>
    Logical,
}

internal static class TreeQuery
{
    internal static long Size(NodeStore tree, int node, SizeMode mode) => mode switch
    {
        SizeMode.Logical => tree.Logical[node],
        SizeMode.Allocated => tree.Allocated[node],
        _ => (tree.Flags[node] & NodeFlags.HardlinkAlias) != 0 ? 0 : tree.Allocated[node],
    };

    /// <summary>
    /// A node's direct children, largest first.
    /// </summary>
    /// <remarks>
    /// Sorting happens here rather than being baked into the snapshot. Subtree totals are
    /// only known after the bottom-up aggregation pass, so ordering the stored arrays by
    /// size would need a second permutation pass that rewrites every child pointer - a
    /// fragile trade for something that costs milliseconds at display time.
    /// </remarks>
    internal static int[] ChildrenBySize(NodeStore tree, int node, SizeMode mode)
    {
        var count = tree.ChildCount[node];
        if (count == 0) return [];

        var first = tree.FirstChild[node];
        var children = new int[count];
        for (var i = 0; i < count; i++) children[i] = first + i;

        Array.Sort(children, (a, b) => Size(tree, b, mode).CompareTo(Size(tree, a, mode)));
        return children;
    }

    /// <summary>Resolves a filesystem path to a node, or -1.</summary>
    internal static int Find(NodeStore tree, string fullPath)
    {
        foreach (var root in tree.Roots)
        {
            var rootName = tree.Name(root);
            if (!fullPath.StartsWith(rootName, StringComparison.OrdinalIgnoreCase)) continue;

            var node = root;
            var rest = fullPath.AsSpan(rootName.Length);

            foreach (var range in rest.Split(Path.DirectorySeparatorChar))
            {
                var segment = rest[range];
                if (segment.IsEmpty) continue;

                var match = NodeStore.NoNode;
                var children = tree.Children(node);
                for (var i = children.Start.Value; i < children.End.Value; i++)
                {
                    if (!segment.Equals(tree.Name(i), StringComparison.OrdinalIgnoreCase)) continue;
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

    internal sealed record TopFilter
    {
        internal bool Directories { get; init; }
        internal int Limit { get; init; } = 40;
        internal long MinBytes { get; init; }
        internal int? Under { get; init; }
        internal IReadOnlySet<string>? Extensions { get; init; }
        internal DateTime? ModifiedBefore { get; init; }
        internal DateTime? ModifiedAfter { get; init; }
    }

    internal static int[] Top(NodeStore tree, TopFilter filter, SizeMode mode)
    {
        var subtree = filter.Under is { } under ? SubtreeRange(tree, under) : null;
        var matches = new List<int>(Math.Min(4096, tree.Count));

        for (var i = 0; i < tree.Count; i++)
        {
            if (tree.IsDirectory(i) != filter.Directories) continue;

            var size = Size(tree, i, mode);
            if (size < filter.MinBytes || size <= 0) continue;

            if (subtree is not null && !subtree.Contains(i)) continue;

            if (filter.Extensions is { Count: > 0 })
            {
                var name = tree.Name(i);
                var dot = name.LastIndexOf('.');
                if (dot < 0) continue;
                if (!filter.Extensions.Contains(name[(dot + 1)..].ToLowerInvariant())) continue;
            }

            if (filter.ModifiedBefore is { } before && tree.ModifiedUtc(i) >= before) continue;
            if (filter.ModifiedAfter is { } after && tree.ModifiedUtc(i) <= after) continue;

            matches.Add(i);
        }

        matches.Sort((a, b) => Size(tree, b, mode).CompareTo(Size(tree, a, mode)));
        return [.. matches.Take(filter.Limit)];
    }

    /// <summary>Every node under <paramref name="root"/>, including it.</summary>
    internal static HashSet<int> Subtree(NodeStore tree, int root) => SubtreeRange(tree, root);

    private static HashSet<int> SubtreeRange(NodeStore tree, int root)
    {
        var set = new HashSet<int>();
        var stack = new Stack<int>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            var node = stack.Pop();
            set.Add(node);

            var children = tree.Children(node);
            for (var i = children.Start.Value; i < children.End.Value; i++) stack.Push(i);
        }

        return set;
    }
}
