using System.Globalization;
using PathMemo.Analysis;
using PathMemo.Cli.Output;
using PathMemo.Snapshots;

namespace PathMemo.Cli.Commands;

/// <summary>
/// One level of the tree, largest first - the non-interactive form of the browse screen.
/// </summary>
internal static class TreeCommand
{
    internal static int Run(TreeOptions options)
    {
        if (!SnapshotLoader.TryLoad(options.ScanId, out var snapshot, out var id)) return ExitCode.NoData;

        var tree = snapshot.Tree;
        var node = ResolveStart(tree, options.Path);
        if (node == NodeStore.NoNode)
        {
            Console.Error.WriteLine(options.Path is null
                ? "pathmemo: snapshot has no roots"
                : $"pathmemo: not in snapshot {id}: {options.Path}");
            return ExitCode.NoData;
        }

        var w = Console.Out;
        var mode = options.SizeMode;
        var total = TreeQuery.Size(tree, node, mode);

        w.WriteLine();
        w.WriteLine($"{tree.GetPath(node)}");
        w.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "{0}  in {1:N0} files   ·   scan {2}   ·   {3:yyyy-MM-dd HH:mm} UTC   ·   {4} sizes",
            SizeFormat.Bytes(total), tree.FileCount[node], id, snapshot.StartedUtc,
            mode.ToString().ToLowerInvariant()));
        w.WriteLine();

        var children = TreeQuery.ChildrenBySize(tree, node, mode);
        if (children.Length == 0)
        {
            w.WriteLine("  (empty)");
            return ExitCode.Ok;
        }

        var width = ConsoleWidth();
        var nameWidth = Math.Max(20, width - 46);

        foreach (var child in children.Take(options.Limit))
        {
            var size = TreeQuery.Size(tree, child, mode);
            var share = total > 0 ? size * 100.0 / total : 0;
            var isDir = tree.IsDirectory(child);

            // TrimEnd because Fit pads to the column width, which would otherwise leave
            // trailing spaces on every row that carries no badges.
            w.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "  {0,9}  {1}  {2,5:F1}%  {3}{4}",
                SizeFormat.Bytes(size),
                Bar(share, 20),
                share,
                Fit(tree.Name(child) + (isDir ? Path.DirectorySeparatorChar.ToString() : ""), nameWidth),
                Badges(tree, child)).TrimEnd());
        }

        if (children.Length > options.Limit)
            w.WriteLine($"  ... {children.Length - options.Limit} more (use --limit)");

        return ExitCode.Ok;
    }

    private static int ResolveStart(NodeStore tree, string? path)
    {
        if (path is null) return tree.Roots.Length > 0 ? LargestRoot(tree) : NodeStore.NoNode;

        return TreeQuery.Find(tree, Path.GetFullPath(path));
    }

    private static int LargestRoot(NodeStore tree)
    {
        var best = tree.Roots[0];
        foreach (var root in tree.Roots)
            if (tree.Allocated[root] > tree.Allocated[best]) best = root;
        return best;
    }

    private static string Bar(double percent, int width)
    {
        var filled = (int)Math.Round(percent / 100 * width);
        filled = Math.Clamp(filled, 0, width);
        return new string('#', filled) + new string('.', width - filled);
    }

    /// <summary>
    /// Badges say why a number may not be what the user expects - a link that frees
    /// nothing, a cloud file that occupies nothing, a directory we could not read.
    /// </summary>
    private static string Badges(NodeStore tree, int node)
    {
        var flags = tree.Flags[node];
        var badges = new List<string>(3);

        if ((flags & NodeFlags.Reparse) != 0) badges.Add("reparse");
        if ((flags & NodeFlags.HardlinkAlias) != 0) badges.Add("link");
        if ((flags & NodeFlags.CloudOnly) != 0) badges.Add("cloud");
        if ((flags & NodeFlags.Sparse) != 0) badges.Add("sparse");
        if ((flags & NodeFlags.SelfData) != 0) badges.Add("self");
        if ((flags & NodeFlags.Incomplete) != 0) badges.Add("unreadable");

        return badges.Count == 0 ? "" : "  [" + string.Join(' ', badges) + "]";
    }

    /// <summary>Pads or truncates to exactly <paramref name="width"/> display columns.</summary>
    private static string Fit(string text, int width)
    {
        var actual = PathDisplay.Width(text);
        if (actual <= width) return text + new string(' ', width - actual);

        // Truncate by display columns, not char count, so a CJK or emoji name cannot
        // overflow the column and shift everything after it (README section 14.4).
        var kept = new System.Text.StringBuilder(width);
        var used = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            var runeWidth = PathDisplay.Width(rune.ToString());
            if (used + runeWidth > width - 1) break;
            kept.Append(rune);
            used += runeWidth;
        }

        kept.Append('~');
        return kept.ToString() + new string(' ', width - used - 1);
    }

    private static int ConsoleWidth()
    {
        try { return Console.IsOutputRedirected ? 120 : Math.Max(80, Console.WindowWidth); }
        catch (IOException) { return 120; }
    }
}

internal sealed record TreeOptions
{
    internal string? Path { get; init; }
    internal long? ScanId { get; init; }
    internal int Limit { get; init; } = 30;
    internal SizeMode SizeMode { get; init; } = SizeMode.Unique;
}

internal static class SnapshotLoader
{
    internal static bool TryLoad(long? requested, out SnapshotContents snapshot, out long id) =>
        TryLoad(requested, SnapshotParts.All, out snapshot, out id);

    /// <summary>
    /// Opens a scan's snapshot, or explains which one is missing.
    /// </summary>
    /// <param name="parts">
    /// What the command will actually read. A summary needs the tree; <c>errors</c> needs a
    /// few hundred strings, and inflating a 74 MB tree to print them is 200 ms and 100 MB
    /// spent on nothing (README sections 5.2, 20).
    /// </param>
    internal static bool TryLoad(long? requested, SnapshotParts parts,
                                 out SnapshotContents snapshot, out long id)
    {
        snapshot = null!;
        id = 0;

        var entry = requested is { } explicitId
            ? SnapshotStore.List().FirstOrDefault(s => s.Id == explicitId)
            : SnapshotStore.Latest();

        if (entry is null)
        {
            Console.Error.WriteLine(requested is null
                ? "pathmemo: no scans yet - run 'pathmemo scan' first"
                : $"pathmemo: scan {requested} not found");
            return false;
        }

        try
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            snapshot = SnapshotFile.Read(entry.Path, parts);
            clock.Stop();

            id = entry.Id;

            Diagnostics.Note("opened scan {0} ({1:N0} nodes) in {2:F0} ms",
                entry.Id, snapshot.Tree.Count, clock.Elapsed.TotalMilliseconds);
            Diagnostics.Memory(Console.Error, "with a snapshot open", snapshot.Tree.Count);
            return true;
        }
        catch (InvalidDataException ex)
        {
            Console.Error.WriteLine($"pathmemo: snapshot {entry.Id} is unreadable: {ex.Message}");
            return false;
        }
    }
}
