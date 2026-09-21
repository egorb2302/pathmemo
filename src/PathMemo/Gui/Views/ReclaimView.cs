using System.Globalization;
using PathMemo.Analysis;
using PathMemo.Audit;
using PathMemo.Cli.Output;
using PathMemo.Gui.Controls;
using PathMemo.Gui.Render;
using PathMemo.Platform.Native;

namespace PathMemo.Gui.Views;

/// <summary>
/// What the rules recommend, grouped by rule, and the paths behind one rule
/// (README sections 7, 24.3).
/// </summary>
/// <remarks>
/// <para>
/// The same two levels as the terminal's screen 4, for the same reason: a person agrees to
/// "all the node_modules" or picks three of them, and never to something in between that a
/// nested view would invite. The risk ceiling is three segments rather than a key that
/// cycles, because a window can show every choice at once and a key hides two of them; the
/// key still works, on the same letter the TUI uses.
/// </para>
/// <para>
/// <b>It reports and does not delete.</b> What it shows is the same index the terminal
/// screens draw their badges from: the rules over the snapshot, without a handle opened per
/// match to ask the guard (README section 7.5). That is the right trade for a view - a few
/// hundred milliseconds off the render thread instead of seconds of disk work behind a tab
/// the user may never open - and the footer says where the deletion, and the guard's check
/// that precedes it, actually happen.
/// </para>
/// <para>
/// Bars are relative to what is listed - the visible rules, or one rule's paths - not to
/// the volume. A bar against the whole disk is a row of empty tracks that distinguishes
/// nothing, the same reason the tree draws its bars against the current directory.
/// </para>
/// </remarks>
internal sealed class ReclaimView
{
    private readonly RowList _rows = new();

    private ReclaimIndex? _index;
    private IReadOnlyList<ReclaimGroup> _visible = [];
    private IReadOnlyList<ReclaimMatch> _matches = [];
    private long _total;
    private int _ruleCount;

    /// <summary>The scan was stopped early, so everything listed is a floor (README section 4.8).</summary>
    private bool _partial;

    /// <summary>Null at the rule level; a rule id while its paths are listed.</summary>
    private string? _inside;

    /// <summary>The rule row we came out of, so going back puts the cursor on it again.</summary>
    private int _cameFrom = -1;

    /// <summary>The highest risk shown. Caution, as on the terminal screen: a report, not an action.</summary>
    internal Risk Ceiling { get; private set; } = Risk.Caution;

    /// <summary>The scan the view is about, or 0 when there is none.</summary>
    internal long ScanId { get; private set; }

    /// <summary>Whether a snapshot has been handed over, finished or not.</summary>
    internal bool Loaded => ScanId != 0;

    /// <summary>Whether the rules have finished and there is something to list.</summary>
    internal bool HasReport => _index is not null;

    internal string? Inside => _inside;

    /// <summary>Starts a fresh report for a scan; the rows say the rules are running until <see cref="Receive"/>.</summary>
    internal void Begin(long scanId, int ruleCount, bool partial)
    {
        ScanId = scanId;
        _ruleCount = ruleCount;
        _partial = partial;
        _index = null;
        _inside = null;
        _cameFrom = -1;
        _visible = [];
        _matches = [];
        _total = 0;
        _rows.Count = 0;
        _rows.Reset();
    }

    /// <summary>The finished index, on the UI thread.</summary>
    internal void Receive(ReclaimIndex index)
    {
        _index = index;
        Rebuild();
    }

    /// <summary>
    /// Drops everything, so the next visit asks for the newest scan.
    /// </summary>
    /// <remarks>
    /// Called after a scan, together with the tree's own <c>Forget</c>. The index holds node
    /// indices into the snapshot it was built from, and those mean nothing against the tree a
    /// new scan produces (README section 14.7).
    /// </remarks>
    internal void Forget() => Begin(0, 0, partial: false);

    /// <summary>Rebuilds the listing for the current level and ceiling.</summary>
    private void Rebuild()
    {
        if (_index is null) return;

        _visible = [.. _index.Groups.Where(g => g.Rule.Risk <= Ceiling)];

        // A rule that the new ceiling hides cannot be the one whose paths are listed.
        if (_inside is { } id && GroupOf(id) is null) _inside = null;

        _matches = _inside is { } inside ? GroupOf(inside)!.Matches : [];

        _total = _inside is null
            ? _visible.Sum(g => g.Reclaimable)
            : _matches.Sum(m => m.Reclaimable);

        _rows.Count = _inside is null ? _visible.Count : _matches.Count;
        _rows.Reset();

        if (_cameFrom >= 0)
        {
            _rows.Select(_cameFrom);
            _cameFrom = -1;
        }
    }

    private ReclaimGroup? GroupOf(string id) =>
        _visible.FirstOrDefault(g => g.Rule.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    /// <summary>The group under the cursor at the rule level, or null.</summary>
    private ReclaimGroup? CurrentGroup =>
        _inside is null && _visible.Count > 0 ? _visible[_rows.Selected] : null;

    private ReclaimMatch? CurrentMatch =>
        _inside is not null && _matches.Count > 0 ? _matches[_rows.Selected] : null;

    // ------------------------------------------------------------------ painting

    internal void Paint(IPainter p, Theme theme, HitMap hits, Rect area, Hit hover)
    {
        if (!Loaded)
        {
            Centred(p, theme, area, "No snapshot to look at. Run a scan first.");
            return;
        }

        var pad = p.Scale(16);
        var body = area.Deflate(pad, p.Scale(10));

        var head = body.TakeTop(p.LineHeight + p.Scale(8));
        Heading(p, theme, hits, head, hover);

        var controls = body.DropTop(head.Height + p.Scale(4)).TakeTop(p.LineHeight + p.Scale(8));
        Controls(p, theme, hits, controls, hover);

        var below = body.DropTop(head.Height + p.Scale(4) + controls.Height + p.Scale(8));

        // A scan that was stopped produced real numbers for part of a disk, and they look
        // exactly like a complete scan's. A partial snapshot is "fit for browsing, not for
        // reclaim advice" (README section 4.8): a cache the scan never reached is not listed,
        // and one it was half way through is listed at half its size. The rows are still
        // worth seeing; what they are not is the whole answer, and that goes above them.
        if (_partial)
        {
            var warning = below.TakeTop(p.LineHeight);

            p.Text(warning, string.Create(CultureInfo.InvariantCulture,
                    $"Scan {ScanId} was stopped early. What it did not read is not listed, so every number here is a floor."),
                theme.Warning);

            below = below.DropTop(warning.Height + p.Scale(6));
        }

        var foot = below.TakeBottom(p.Height(FontRole.Small) + p.Scale(6));
        Footer(p, theme, foot);
        below = below.DropBottom(foot.Height + p.Scale(6));

        if (_index is null)
        {
            Centred(p, theme, below, string.Create(CultureInfo.InvariantCulture,
                $"Applying {_ruleCount} rules to scan {ScanId}…"));
            return;
        }

        if (_rows.Count == 0)
        {
            Centred(p, theme, below, _index.MatchCount == 0
                ? "No rule matched anything in this scan."
                : "Nothing at this risk level. Allow more above.");
            return;
        }

        var header = below.TakeTop(p.Height(FontRole.Small) + p.Scale(6));
        Header(p, theme, header);

        var list = below.DropTop(header.Height);

        _rows.Paint(p, theme, hits, list, (painter, line, index, selected, _) =>
        {
            if (_inside is null) RuleRow(painter, theme, line, index, selected);
            else PathRow(painter, theme, line, index, selected);
        }, hover);
    }

    private static void Centred(IPainter p, Theme theme, Rect area, string text)
    {
        var middle = new Rect(area.X, area.Y + area.Height / 2 - p.LineHeight / 2, area.Width, p.LineHeight);
        p.Text(middle, text, theme.Dim, FontRole.Body, Align.Centre);
    }

    /// <summary>
    /// "Reclaim", or "Reclaim › rule" with the first word clickable: the way back is in
    /// plain sight, the same as the tree's breadcrumb.
    /// </summary>
    private void Heading(IPainter p, Theme theme, HitMap hits, Rect area, Hit hover)
    {
        var left = area;

        var inside = _inside is not null;
        var rootWidth = p.Measure("Reclaim", inside ? FontRole.Body : FontRole.Bold) + p.Scale(8);
        var root = left.TakeLeft(rootWidth);

        if (inside)
        {
            if (hover == new Hit(HitKind.Crumb, 0)) p.Fill(root, theme.Hover);

            p.Text(root, "Reclaim", theme.Accent, FontRole.Body, Align.Centre);
            hits.Add(root, HitKind.Crumb, 0);

            left = left.DropLeft(rootWidth);

            var separator = left.TakeLeft(p.Scale(10));
            p.Text(separator, "›", theme.Dim, FontRole.Body, Align.Centre);
            left = left.DropLeft(separator.Width);

            var id = _inside!;
            p.Text(left.TakeLeft(p.Measure(id, FontRole.Bold) + p.Scale(8)), id, theme.Text, FontRole.Bold, Align.Centre);
        }
        else
        {
            p.Text(root, "Reclaim", theme.Text, FontRole.Bold, Align.Centre);
        }

        if (_index is null) return;

        p.Text(area, Summary(), theme.Dim, FontRole.Small, Align.Right);
    }

    /// <summary>
    /// The one number the view exists for, and what it is made of.
    /// </summary>
    /// <remarks>
    /// "Reclaimable" counts only what pathmemo can delete itself. A disk image that needs a
    /// person, or a repository that needs <c>git gc</c>, is listed with its size but kept out
    /// of that number and named beside it: a total that includes bytes no command of this
    /// tool will ever free is the number that never arrives (README section 7.3), and the
    /// terminal screen draws the same line in the same place.
    /// </remarks>
    private string Summary()
    {
        if (_inside is { } id && GroupOf(id) is { } group)
        {
            var label = group.Rule.Action == ReclaimAction.Delete ? "reclaimable" : "not pathmemo's to delete";

            return string.Create(CultureInfo.InvariantCulture,
                $"{SizeFormat.Bytes(_total)} {label}  ·  {_matches.Count:N0} places  ·  scan {ScanId}");
        }

        var offered = _visible.Where(g => g.Rule.Action == ReclaimAction.Delete).Sum(g => g.Reclaimable);
        var left = _total - offered;

        var aside = left > 0 ? $"  ·  {SizeFormat.Bytes(left)} for another tool or a decision" : "";

        // The scan, by id, because these are stored numbers (README section 14.7) - and
        // because the status line describes the last completed scan while the views open the
        // newest snapshot. After a scan that was stopped, those are two different scans.
        return string.Create(CultureInfo.InvariantCulture,
            $"{SizeFormat.Bytes(offered)} reclaimable{aside}  ·  {_visible.Sum(g => g.Count):N0} places  ·  {_visible.Count} rules  ·  scan {ScanId}");
    }

    /// <summary>The risk ceiling as three segments, and the rule's own line when inside one.</summary>
    private void Controls(IPainter p, Theme theme, HitMap hits, Rect area, Hit hover)
    {
        var label = area.TakeLeft(p.Measure("up to", FontRole.Small) + p.Scale(10));
        p.Text(label, "up to", theme.Dim, FontRole.Small);

        var left = area.DropLeft(label.Width);

        foreach (var risk in Enum.GetValues<Risk>())
        {
            var text = ReclaimNames.Of(risk);
            var width = p.Measure(text, FontRole.Body) + p.Scale(20);
            var segment = left.TakeLeft(width).Deflate(0, p.Scale(3));

            var active = risk == Ceiling;
            var hovered = hover == new Hit(HitKind.Risk, (int)risk);

            p.Fill(segment, active ? theme.Accent : hovered ? theme.Hover : theme.Surface);
            p.Text(segment, text, active ? theme.OnAccent : theme.Text, FontRole.Body, Align.Centre);
            hits.Add(segment, HitKind.Risk, (int)risk);

            left = left.DropLeft(width + 1);
        }

        if (_inside is not { } id || GroupOf(id) is not { } group) return;

        // What the rule is, and who may act on it - the two facts a row of paths cannot carry.
        var rule = group.Rule;
        var note = rule.Action switch
        {
            ReclaimAction.Command => $"pathmemo will not delete this - run: {rule.Command}",
            ReclaimAction.Manual => "reported only; deleting this is a decision, not a rule",
            _ => rule.What,
        };

        p.Text(left.DropLeft(p.Scale(16)), note, theme.Dim, FontRole.Body);
    }

    private void Footer(IPainter p, Theme theme, Rect area)
    {
        p.Fill(area.TakeTop(1), theme.Border);

        p.Text(area.DropTop(1),
            "Reporting only. Deleting is `pathmemo reclaim --apply` or the terminal's Reclaim screen, "
            + "where the guard checks every path first.",
            theme.Dim, FontRole.Small);
    }

    private void Header(IPainter p, Theme theme, Rect area)
    {
        var columns = Columns(p, area);

        p.Text(columns.Size, "reclaim", theme.Dim, FontRole.Small, Align.Right);
        p.Text(columns.Share, "share", theme.Dim, FontRole.Small, Align.Right);
        p.Text(columns.Name, _inside is null ? "rule" : "path", theme.Dim, FontRole.Small);
        p.Text(columns.Right, _inside is null ? "places" : "modified", theme.Dim, FontRole.Small, Align.Right);

        p.Fill(area.TakeBottom(1), theme.Border);
    }

    /// <summary>
    /// Where the columns sit, for the header and for every row - one function, so the two
    /// cannot disagree (the tree view's rule).
    /// </summary>
    private (Rect Size, Rect Bar, Rect Share, Rect Name, Rect Right) Columns(IPainter p, Rect line)
    {
        var size = line.TakeLeft(p.Scale(74));
        var bar = line.DropLeft(size.Width + p.Scale(10)).TakeLeft(p.Scale(120));
        var share = line.DropLeft(size.Width + bar.Width + p.Scale(18)).TakeLeft(p.Scale(48));

        var used = size.Width + bar.Width + share.Width + p.Scale(28);

        var wide = line.Width - used > p.Scale(320);
        var right = wide ? line.TakeRight(p.Scale(_inside is null ? 70 : 84)) : Rect.Empty;

        var name = line.DropLeft(used).DropRight(wide ? right.Width + p.Scale(10) : 0);

        return (size, bar, share, name, right);
    }

    private void RuleRow(IPainter p, Theme theme, Rect line, int index, bool selected)
    {
        var group = _visible[index];
        var rule = group.Rule;
        var offered = rule.Action == ReclaimAction.Delete;

        var inner = line.Deflate(p.Scale(4), 0);
        var columns = Columns(p, inner);

        // A rule pathmemo cannot act on is dimmed the way the terminal dims it: the bytes are
        // real, but they are not this tool's to give back.
        p.Text(columns.Size, SizeFormat.Bytes(group.Reclaimable), offered ? theme.Text : theme.Dim,
            FontRole.Body, Align.Right);

        var share = _total > 0 ? group.Reclaimable / (double)_total : 0;
        Draw.Bar(p, theme, columns.Bar.Deflate(0, (columns.Bar.Height - p.Scale(8)) / 2), share, Tint(theme, rule.Risk));

        p.Text(columns.Share, Percent(share), theme.Dim, FontRole.Body, Align.Right);

        var room = columns.Name;

        var idWidth = p.Measure(rule.Id, FontRole.Bold) + p.Scale(12);
        p.Text(room.TakeLeft(idWidth), rule.Id, theme.Text, FontRole.Bold);
        room = room.DropLeft(idWidth);

        // Both axes, because the two together are the point (README section 7.1).
        var badge = $"{ReclaimNames.Of(rule.Risk)} {ReclaimNames.Of(rule.Recoverability)}";
        var badgeWidth = p.Measure(badge, FontRole.Small) + 10;

        if (room.Width > badgeWidth)
        {
            Draw.Badge(p, theme, line, room.X + badgeWidth, badge, Tint(theme, rule.Risk));
            room = room.DropLeft(badgeWidth + p.Scale(12));
        }

        if (room.Width > p.Scale(80)) p.Text(room, Note(group), theme.Dim, FontRole.Body);

        if (!columns.Right.IsEmpty)
            p.Text(columns.Right, group.Count.ToString("N0", CultureInfo.InvariantCulture),
                theme.Dim, FontRole.Body, Align.Right);

        if (selected) p.Fill(line.TakeLeft(p.Scale(3)), theme.Accent);
    }

    private void PathRow(IPainter p, Theme theme, Rect line, int index, bool selected)
    {
        var match = _matches[index];

        var inner = line.Deflate(p.Scale(4), 0);
        var columns = Columns(p, inner);

        p.Text(columns.Size, SizeFormat.Bytes(match.Reclaimable), theme.Text, FontRole.Body, Align.Right);

        var share = _total > 0 ? match.Reclaimable / (double)_total : 0;
        Draw.Bar(p, theme, columns.Bar.Deflate(0, (columns.Bar.Height - p.Scale(8)) / 2), share, theme.Bar);

        p.Text(columns.Share, Percent(share), theme.Dim, FontRole.Body, Align.Right);

        var room = columns.Name;

        // Bytes held by other hard links look like space and are not (README section 3.2).
        if (match.SharedBytes > 0 && room.Width > p.Scale(200))
        {
            var width = Draw.Badge(p, theme, line, room.Right,
                SizeFormat.Bytes(match.SharedBytes) + " shared", theme.Warning);
            room = room.DropRight(width + p.Scale(8));
        }

        p.Text(room, match.Path, theme.Text, FontRole.Body, Align.Left, middleEllipsis: true);

        if (!columns.Right.IsEmpty)
            p.Text(columns.Right, match.ModifiedUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                theme.Dim, FontRole.Body, Align.Right);

        if (selected) p.Fill(line.TakeLeft(p.Scale(3)), theme.Accent);
    }

    private static string Percent(double share) =>
        (share * 100).ToString("0.0", CultureInfo.InvariantCulture) + "%";

    private static Colour Tint(Theme theme, Risk risk) => risk switch
    {
        Risk.Safe => theme.Good,
        Risk.Caution => theme.Warning,
        _ => theme.Danger,
    };

    /// <summary>The one thing about a rule the size columns cannot say - the table's note column.</summary>
    private static string Note(ReclaimGroup group)
    {
        var rule = group.Rule;

        if (rule.Action == ReclaimAction.Command) return "use " + rule.Command;
        if (rule.Action == ReclaimAction.Manual) return "decide by hand";
        if (group.SharedBytes > 0) return $"{SizeFormat.Bytes(group.SharedBytes)} shared via hard links";
        if (rule.ContentsOnly) return "contents only; the directory stays";
        if (rule.Custom) return "from config.json";

        return rule.What;
    }

    // ------------------------------------------------------------------ interaction

    /// <summary>Lists the paths behind the rule under the cursor.</summary>
    internal bool Enter()
    {
        if (CurrentGroup is not { } group) return false;

        _inside = group.Rule.Id;
        Rebuild();

        return true;
    }

    /// <summary>Back to the rules, with the cursor on the one we came out of.</summary>
    internal bool Up()
    {
        if (_inside is not { } id) return false;

        _cameFrom = _visible.ToList().FindIndex(g => g.Rule.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        _inside = null;
        Rebuild();

        return true;
    }

    internal bool Click(int row, bool doubleClick)
    {
        if (row < 0 || row >= _rows.Count) return false;

        _rows.Select(row);
        return !doubleClick || Enter();
    }

    internal bool Wheel(int notches) => _rows.Wheel(notches);

    internal bool Key(KeyInput key)
    {
        if (_rows.Key(key)) return true;

        switch (key.VirtualKey)
        {
            case User32.VkReturn:
            case User32.VkRight:
                return Enter();

            case User32.VkLeft:
            case User32.VkBack:
                return Up();
        }

        return false;
    }

    /// <summary>Sets the ceiling. Returns false when it was that already.</summary>
    internal bool SetCeiling(Risk ceiling)
    {
        if (ceiling == Ceiling) return false;

        Ceiling = ceiling;
        Rebuild();

        return true;
    }

    /// <summary>The next ceiling up, wrapping to safe, on the terminal's key.</summary>
    internal void CycleCeiling() => SetCeiling(Ceiling switch
    {
        Risk.Safe => Risk.Caution,
        Risk.Caution => Risk.Danger,
        _ => Risk.Safe,
    });

    /// <summary>What the ceiling means, for the status line.</summary>
    internal string CeilingNote => Ceiling switch
    {
        Risk.Safe => "Safe only: the application recreates it, nothing is lost.",
        Risk.Caution => "Up to caution: a capability or a way back may be lost.",
        _ => "Everything, including what may break an application.",
    };

    /// <summary>
    /// The paths the cursor stands for: one path, or a whole rule's worth. For the clipboard
    /// - the window has nothing else to do with them.
    /// </summary>
    internal IReadOnlyList<string> SelectedPaths
    {
        get
        {
            if (CurrentMatch is { } match) return [match.Path];
            if (CurrentGroup is { } group) return [.. group.Matches.Select(m => m.Path)];

            return [];
        }
    }
}
