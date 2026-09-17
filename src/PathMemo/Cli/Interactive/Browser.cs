using System.Globalization;
using PathMemo.Analysis;
using PathMemo.Cli.Output;
using PathMemo.Snapshots;

namespace PathMemo.Cli.Interactive;

/// <summary>
/// Walk down the tree by typing a row number, back up with <c>u</c>.
/// </summary>
/// <remarks>
/// Line-oriented rather than a real terminal UI: no raw input mode, no cursor control, so
/// it behaves the same everywhere and cannot leave the terminal in a broken state. The
/// key bindings it accepts (<c>u</c>, <c>r</c>, <c>q</c>, <c>m</c>) are the ones the P5
/// tree screen will keep, so the muscle memory carries over (README section 14.3).
/// </remarks>
internal static class Browser
{
    private const int PageSize = 20;

    internal static void Run(long scanId)
    {
        SnapshotContents snapshot;
        try
        {
            snapshot = SnapshotStore.Load(scanId);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            Console.WriteLine($"  cannot open scan {scanId}: {ex.Message}");
            Launcher.Pause();
            return;
        }

        var tree = snapshot.Tree;
        var mode = SizeMode.Unique;
        var node = LargestRoot(tree);
        var offset = 0;

        while (true)
        {
            var children = TreeQuery.ChildrenBySize(tree, node, mode);
            offset = Math.Clamp(offset, 0, Math.Max(0, children.Length - 1));

            Launcher.ClearScreen();
            PrintHeader(tree, node, mode, scanId);
            var shown = PrintRows(tree, node, children, offset, mode);

            Console.WriteLine();
            Console.WriteLine(Hint(children.Length, offset, shown));
            Console.Write("  > ");

            var input = (Console.ReadLine() ?? "q").Trim().ToLowerInvariant();

            if (input is "q" or "") return;

            if (input is "u")
            {
                if (tree.Parent[node] != NodeStore.NoNode) { node = tree.Parent[node]; offset = 0; }
                continue;
            }

            if (input is "r") { node = LargestRoot(tree); offset = 0; continue; }

            if (input is "m")
            {
                mode = mode switch
                {
                    SizeMode.Unique => SizeMode.Allocated,
                    SizeMode.Allocated => SizeMode.Logical,
                    _ => SizeMode.Unique,
                };
                continue;
            }

            if (input is "n") { offset += PageSize; continue; }
            if (input is "p") { offset -= PageSize; continue; }

            if (int.TryParse(input, CultureInfo.InvariantCulture, out var row)
                && row >= 1 && row <= children.Length)
            {
                var target = children[row - 1];

                if (tree.IsDirectory(target) && tree.ChildCount[target] > 0)
                {
                    node = target;
                    offset = 0;
                }
                else
                {
                    PrintDetails(tree, target, mode);
                    Launcher.Pause();
                }
                continue;
            }

            Console.WriteLine("  ?");
            Launcher.Pause();
        }
    }

    private static void PrintHeader(NodeStore tree, int node, SizeMode mode, long scanId)
    {
        var total = TreeQuery.Size(tree, node, mode);

        Console.WriteLine();
        Console.WriteLine("  " + PathDisplay.Shorten(tree.GetPath(node), Width() - 4));
        Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "  {0} in {1:N0} files   ·   scan {2}   ·   {3} sizes",
            SizeFormat.Bytes(total), tree.FileCount[node], scanId,
            mode.ToString().ToLowerInvariant()));
        Console.WriteLine();
    }

    private static int PrintRows(
        NodeStore tree, int parent, int[] children, int offset, SizeMode mode)
    {
        if (children.Length == 0)
        {
            Console.WriteLine("  (empty)");
            return 0;
        }

        var total = TreeQuery.Size(tree, parent, mode);
        var nameWidth = Math.Max(16, Width() - 48);
        var shown = 0;

        for (var i = offset; i < children.Length && shown < PageSize; i++, shown++)
        {
            var child = children[i];
            var size = TreeQuery.Size(tree, child, mode);
            var share = total > 0 ? size * 100.0 / total : 0;
            var name = tree.Name(child) + (tree.IsDirectory(child) ? Path.DirectorySeparatorChar.ToString() : "");

            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "  {0,3}  {1,9}  {2}  {3,5:F1}%  {4}{5}",
                i + 1,
                SizeFormat.Bytes(size),
                Bar(share, 14),
                share,
                Fit(name, nameWidth),
                Badges(tree, child)).TrimEnd());
        }

        return shown;
    }

    private static void PrintDetails(NodeStore tree, int node, SizeMode mode)
    {
        Console.WriteLine();
        Console.WriteLine("  " + tree.GetPath(node));
        Console.WriteLine();
        Console.WriteLine($"  on disk     {SizeFormat.Bytes(tree.Allocated[node])}");
        Console.WriteLine($"  logical     {SizeFormat.Bytes(tree.Logical[node])}");

        if (tree.IsDirectory(node))
            Console.WriteLine($"  files       {tree.FileCount[node]:N0}");

        Console.WriteLine($"  modified    {tree.ModifiedUtc(node):yyyy-MM-dd HH:mm} UTC");
        Console.WriteLine($"  attributes  {(FileAttributes)tree.Attributes[node]}");

        var badges = Badges(tree, node);
        if (badges.Length > 0) Console.WriteLine($"  flags      {badges}");

        if ((tree.Flags[node] & NodeFlags.Sparse) != 0)
            Console.WriteLine("  · sparse or compressed: it occupies less than its logical size");
        if ((tree.Flags[node] & NodeFlags.CloudOnly) != 0)
            Console.WriteLine("  · stored in the cloud: deleting it frees nothing locally");
        if ((tree.Flags[node] & NodeFlags.Reparse) != 0)
            Console.WriteLine("  · a link, not a directory: its target was not counted here");
    }

    private static string Hint(int count, int offset, int shown)
    {
        var range = count == 0 ? "" : $"{offset + 1}-{offset + shown} of {count}   ·   ";
        var pager = count > PageSize ? "n/p = page   ·   " : "";
        return $"  {range}{pager}number = open   ·   u = up   ·   r = root   ·   m = size mode   ·   q = back";
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
        var filled = Math.Clamp((int)Math.Round(percent / 100 * width), 0, width);
        return new string('#', filled) + new string('.', width - filled);
    }

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

    private static int Width()
    {
        try { return Math.Max(80, Console.WindowWidth); }
        catch (IOException) { return 100; }
    }
}
