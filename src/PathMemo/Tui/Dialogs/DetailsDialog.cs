using System.Globalization;
using PathMemo.Analysis;
using PathMemo.Cli.Output;
using PathMemo.Platform;
using PathMemo.Snapshots;
using PathMemo.Tui.Terminal;

namespace PathMemo.Tui.Dialogs;

/// <summary>
/// Everything known about one entry, including the parts the row cannot show.
/// </summary>
/// <remarks>
/// The four sizes appear together here on purpose (README section 3.1): a row shows one
/// of them, and the only way to understand why a 40 GB <c>WinSxS</c> is not 40 GB of
/// reclaimable space is to see unique, allocated and logical side by side.
/// </remarks>
internal sealed class DetailsDialog(int node) : ITuiView
{
    internal int Node => node;

    public void Render(Screen screen, TuiSession session)
    {
        var rows = new List<Draw.PanelRow>();
        foreach (var line in Describe(session, node))
            rows.Add(new Draw.PanelRow(line, line.StartsWith(Glyphs.Bullet, StringComparison.Ordinal) ? Style.Dim : Style.Plain));

        Draw.Panel(screen, Sanitizer.Clean(session.Tree.Name(node)), rows, "y copy path   Y copy details   any other key closes");
    }

    public bool HandleKey(in TuiKey key, TuiSession session)
    {
        if (key.Is('y'))
        {
            Copy(session, session.Tree.GetPath(node), "path");
            return true;
        }

        if (key.Is('Y'))
        {
            Copy(session, string.Join(Environment.NewLine, Describe(session, node)), "details");
            return true;
        }

        session.Modal = null;
        return true;
    }

    private static void Copy(TuiSession session, string text, string what)
    {
        if (Clipboard.TrySetText(text, out var error)) session.Say($"{what} copied");
        else session.Warn($"clipboard: {error}");
    }

    /// <summary>The same text the dialog shows and <c>Y</c> puts on the clipboard.</summary>
    internal static IReadOnlyList<string> Describe(TuiSession session, int node)
    {
        var tree = session.Tree;
        var lines = new List<string>(16);
        var isDirectory = tree.IsDirectory(node);

        var raw = tree.Name(node);
        lines.Add(Sanitizer.Clean(tree.GetPath(node)));
        lines.Add("");

        lines.Add(Field("on disk (unique)", SizeFormat.Bytes(TreeQuery.Size(tree, node, SizeMode.Unique))));
        lines.Add(Field("allocated", SizeFormat.Bytes(tree.Allocated[node])));
        lines.Add(Field("logical", SizeFormat.Bytes(tree.Logical[node])));

        if (isDirectory)
        {
            lines.Add(Field("files below", tree.FileCount[node].ToString("N0", CultureInfo.InvariantCulture)));
            lines.Add(Field("direct entries", tree.ChildCount[node].ToString("N0", CultureInfo.InvariantCulture)));
        }

        var parent = tree.Parent[node];
        if (parent != NodeStore.NoNode)
        {
            var parentSize = TreeQuery.Size(tree, parent, session.Mode);
            var share = parentSize > 0 ? TreeQuery.Size(tree, node, session.Mode) * 100.0 / parentSize : 0;
            lines.Add(Field("share of parent", string.Format(CultureInfo.InvariantCulture, "{0:F1}%", share)));
        }

        lines.Add(Field("modified", tree.ModifiedUtc(node).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC"));
        lines.Add(Field("attributes", ((FileAttributes)tree.Attributes[node]).ToString()));

        if (tree.LinkCount[node] > 1)
            lines.Add(Field("hard links", tree.LinkCount[node] == 255 ? "255 or more" : tree.LinkCount[node].ToString(CultureInfo.InvariantCulture)));

        lines.Add(Field("category", CategoryName(tree, node, isDirectory)));

        var badges = Draw.Badges(tree, node);
        if (badges.Length > 0) lines.Add(Field("flags", badges));

        // What the rules make of it, in the place where a person is deciding about one
        // thing rather than reading a table of thirty (README section 7.1).
        if (session.Reclaim.For(node) is { } match)
        {
            lines.Add(Field("reclaim rule", match.Rule.Id));
            lines.Add(Field("", $"{ReclaimNames.Of(match.Rule.Risk)} · "
                              + $"{ReclaimNames.Of(match.Rule.Recoverability)} · {match.Rule.What}"));

            if (match.Rule.Command is { } command)
                lines.Add(Field("", (match.Rule.Action == ReclaimAction.Command ? "use " : "or ") + command));

            if (match.SharedBytes > 0)
                lines.Add(Field("", $"{SizeFormat.Bytes(match.SharedBytes)} of it is shared via hard links"));
        }

        var flags = tree.Flags[node];
        if ((flags & NodeFlags.Sparse) != 0)
            lines.Add(Glyphs.Bullet + "sparse or compressed: it occupies less than its logical size");
        if ((flags & NodeFlags.HardlinkAlias) != 0)
            lines.Add(Glyphs.Bullet + "another name for a file counted elsewhere; deleting it frees nothing");
        if ((flags & NodeFlags.CloudOnly) != 0)
            lines.Add(Glyphs.Bullet + "stored in the cloud; deleting it frees nothing locally");
        if ((flags & NodeFlags.Reparse) != 0)
            lines.Add(Glyphs.Bullet + "a link, not a directory: its target was not counted here");
        if ((flags & NodeFlags.SelfData) != 0)
            lines.Add(Glyphs.Bullet + "pathmemo's own data directory");
        if ((flags & NodeFlags.Incomplete) != 0)
            lines.Add(Glyphs.Bullet + "could not be read in full, so this total is a lower bound");

        if (Sanitizer.NeedsCleaning(raw))
            lines.Add(Glyphs.Bullet + "the real name contains hidden or control characters, shown as dots");

        if (!isDirectory && FileLaunch.FromInternet(tree.GetPath(node)))
            lines.Add(Glyphs.Bullet + "downloaded from the internet (mark of the web)");

        return lines;
    }

    private static string CategoryName(NodeStore tree, int node, bool isDirectory)
    {
        if (isDirectory)
        {
            var inherited = FileCategories.Inherited(tree.Name(node), tree.Depth(node));
            return inherited is { } category ? FileCategories.Name(category) : "(from its contents)";
        }

        var name = tree.Name(node);
        var dot = name.LastIndexOf('.');
        return FileCategories.Name(FileCategories.ByExtension(dot >= 0 ? name.AsSpan(dot + 1) : []));
    }

    private static string Field(string label, string value) => TextWidth.Pad(label, 18) + value;
}
