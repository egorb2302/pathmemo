using PathMemo.Snapshots;

namespace PathMemo.Tui.Terminal;

/// <summary>
/// The handful of shapes every screen draws: rules, bars, sparklines, badges, the footer.
/// </summary>
internal static class Draw
{
    private const char Filled = '█';      // FULL BLOCK
    private const char Empty = '░';       // LIGHT SHADE
    private const char Horizontal = '─';  // BOX DRAWINGS LIGHT HORIZONTAL

    private static readonly char[] Spark =
        ['▁', '▂', '▃', '▄', '▅', '▆', '▇', '█'];

    internal static void Rule(Screen screen, int y)
    {
        var line = screen.Row(y).Space().Add(Horizontal, screen.Width - 2, Style.Dim);
        screen.Put(y, line);
    }

    /// <summary>A proportion bar, drawn as two runs so the empty part can be dimmed.</summary>
    internal static void Bar(Line line, double share, int width, Style style = Style.Bar)
    {
        var filled = Math.Clamp((int)Math.Round(share / 100 * width), 0, width);

        // A share worth at least a quarter of a cell gets a whole one, so a 1.5% row is
        // visible rather than blank. Below that the bar stays empty: giving 0.02% the same
        // block as 1.5% would make the column a lie, and the percentage is right there.
        if (filled == 0 && share * width >= 25) filled = 1;

        line.Add(Filled, filled, style).Add(Empty, width - filled, Style.BarEmpty);
    }

    /// <summary>
    /// The history of used space as one row of blocks (README section 14.1: history lives
    /// in the CLI, the shape of it lives here).
    /// </summary>
    /// <remarks>
    /// Scaled between the smallest and largest value rather than from zero: a disk that
    /// went from 401 to 409 GB would otherwise draw as a flat line, and the whole point of
    /// the row is to show that it moved.
    /// </remarks>
    internal static string Sparkline(IReadOnlyList<long> values, int width)
    {
        if (values.Count == 0) return "";

        var taken = values.Count <= width ? values : [.. values.Skip(values.Count - width)];
        var min = taken.Min();
        var max = taken.Max();
        var span = max - min;

        var chars = new char[taken.Count];
        for (var i = 0; i < taken.Count; i++)
            chars[i] = span == 0
                ? Spark[3]
                : Spark[Math.Clamp((int)((taken[i] - min) * (Spark.Length - 1) / span), 0, Spark.Length - 1)];

        return new string(chars);
    }

    /// <summary>
    /// The facts about a node that change what deleting it would mean (README section 14.2).
    /// </summary>
    internal static string Badges(NodeStore tree, int node)
    {
        var flags = tree.Flags[node];
        if (flags == NodeFlags.None || flags == NodeFlags.Directory) return "";

        var badges = new List<string>(3);
        if ((flags & NodeFlags.Reparse) != 0) badges.Add("reparse");
        if ((flags & NodeFlags.HardlinkAlias) != 0) badges.Add("link");
        if ((flags & NodeFlags.CloudOnly) != 0) badges.Add("cloud");
        if ((flags & NodeFlags.Sparse) != 0) badges.Add("sparse");
        if ((flags & NodeFlags.SelfData) != 0) badges.Add("self");
        if ((flags & NodeFlags.Incomplete) != 0) badges.Add("partial");

        return badges.Count == 0 ? "" : string.Join(' ', badges);
    }

    /// <summary>One line inside a dialog panel.</summary>
    internal readonly record struct PanelRow(string Text, Style Style = Style.Plain);

    /// <summary>
    /// A centred bordered panel - how every modal is drawn (README section 14.1).
    /// </summary>
    /// <remarks>
    /// Modals are panels over the screen rather than separate screens because the context
    /// underneath is the point: confirming a delete while the row it refers to is still
    /// visible is what makes the confirmation mean anything.
    /// </remarks>
    internal static void Panel(Screen screen, string title, IReadOnlyList<PanelRow> rows, string footer)
    {
        var inner = Math.Max(TextWidth.Of(title) + 4, TextWidth.Of(footer) + 2);
        foreach (var row in rows) inner = Math.Max(inner, TextWidth.Of(row.Text) + 2);

        inner = Math.Min(inner, screen.Width - 4);
        var height = Math.Min(rows.Count + 4, screen.Height - 2);
        var left = Math.Max(1, (screen.Width - inner - 2) / 2);
        var top = Math.Max(0, (screen.Height - height) / 2);

        var head = screen.Row(top).Space(left)
            .Add("┌─ ", Style.Dim).Add(title, Style.Title).Space()
            .Add('─', Math.Max(0, inner - TextWidth.Of(title) - 3), Style.Dim)
            .Add("┐", Style.Dim);
        screen.Put(top, head);

        var body = height - 4;
        for (var i = 0; i < body; i++)
        {
            var y = top + 1 + i;
            var line = screen.Row(y).Space(left).Add("│", Style.Dim).Space();

            if (i < rows.Count) line.Add(rows[i].Text, rows[i].Style);

            line.PadTo(left + inner + 1).Add("│", Style.Dim);
            screen.Put(y, line);
        }

        var footerRow = screen.Row(top + height - 3).Space(left)
            .Add("│", Style.Dim).Space().Add(footer, Style.Dim)
            .PadTo(left + inner + 1).Add("│", Style.Dim);
        screen.Put(top + height - 3, footerRow);

        var tail = screen.Row(top + height - 2).Space(left)
            .Add("└", Style.Dim).Add('─', inner, Style.Dim).Add("┘", Style.Dim);
        screen.Put(top + height - 2, tail);
    }

    /// <summary>
    /// The key hints, in pairs of key and meaning so the keys can be picked out by colour.
    /// Hints that do not fit are dropped from the right, which is where the rarer ones are.
    /// </summary>
    internal static void Hints(Screen screen, int y, params (string Key, string What)[] hints)
    {
        var line = screen.Row(y).Space();

        foreach (var (key, what) in hints)
        {
            if (line.Remaining < key.Length + what.Length + 3) break;

            line.Add(key, Style.Accent).Space().Add(what, Style.Dim).Space(2);
        }

        screen.Put(y, line);
    }
}
