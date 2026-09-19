using PathMemo.Gui.Render;
using PathMemo.Platform.Native;

namespace PathMemo.Gui.Controls;

/// <summary>
/// A virtualised list of equal-height rows: scrolling, selection and the keyboard
/// (README section 24.3).
/// </summary>
/// <remarks>
/// <para>
/// Only the visible rows are drawn, which is not an optimisation but a requirement:
/// <c>WinSxS\Manifests</c> on this machine has 26,205 children and a synthetic directory of
/// 200,000 is in the budget of README section 20. Drawing them all would be seconds per frame
/// for a screen that holds forty.
/// </para>
/// <para>
/// It holds no rows. The caller says how many there are and draws the one it is asked for, so
/// the same control lists directory children, audit findings, reclaim rules and duplicate
/// groups without any of them being copied into it - the same reason the tree itself is
/// twelve arrays rather than a million objects (README section 17.3).
/// </para>
/// </remarks>
internal sealed class RowList
{
    private int _first;
    private int _selected;

    /// <summary>How many rows the caller has. Set before painting.</summary>
    internal int Count { get; set; }

    internal int Selected => Math.Clamp(_selected, 0, Math.Max(0, Count - 1));

    /// <summary>The first row on screen, for a caller that wants to restore a position.</summary>
    internal int First => _first;

    /// <summary>How many rows fitted last time it was painted, for Page Up and Page Down.</summary>
    internal int Page { get; private set; } = 10;

    internal int RowHeight(IPainter p) => p.LineHeight + p.Scale(9);

    /// <summary>Puts the cursor on a row and scrolls it into view.</summary>
    internal void Select(int index)
    {
        _selected = Math.Clamp(index, 0, Math.Max(0, Count - 1));
        Reveal();
    }

    /// <summary>Back to the top, for when the list becomes a different list.</summary>
    internal void Reset()
    {
        _first = 0;
        _selected = 0;
    }

    internal void Paint(
        IPainter p, Theme theme, HitMap hits, Rect area,
        Action<IPainter, Rect, int, bool, bool> row, Hit hover)
    {
        var height = RowHeight(p);
        var bar = p.Scale(10);

        var body = Count * height > area.Height ? area.DropRight(bar) : area;
        Page = Math.Max(1, body.Height / height);

        Clamp();

        p.Clip(body);

        for (var i = 0; i < Page + 1 && _first + i < Count; i++)
        {
            var index = _first + i;
            var line = new Rect(body.X, body.Y + i * height, body.Width, height);

            if (line.Y >= body.Bottom) break;

            var selected = index == Selected;
            var hovered = hover == new Hit(HitKind.Row, index);

            if (selected) p.Fill(line, theme.Selection);
            else if (hovered) p.Fill(line, theme.Hover);

            row(p, line, index, selected, hovered);
            hits.Add(line.Intersect(body), HitKind.Row, index);
        }

        p.Unclip();

        if (body.Width != area.Width)
            Draw.Scrollbar(p, theme, hits, area.TakeRight(bar), _first, Page, Count);
    }

    /// <summary>
    /// A wheel notch moves three rows, the figure Windows itself uses.
    /// </summary>
    /// <returns>True when something moved and the frame needs redrawing.</returns>
    internal bool Wheel(int notches)
    {
        var before = _first;

        _first -= notches * 3;
        Clamp();

        return _first != before;
    }

    /// <summary>
    /// The navigation keys. Returns false for anything it does not use, so the caller can.
    /// </summary>
    /// <remarks>
    /// The arrows move the cursor, which pulls the view; Page Up and Page Down move by exactly
    /// what fits, so a row cannot be stepped over. Home and End are absolute - on a list of
    /// 1.2 million rows, "scroll to the end" is otherwise a minute of wheel.
    /// </remarks>
    internal bool Key(KeyInput key)
    {
        switch (key.VirtualKey)
        {
            case User32.VkDown: return Move(1);
            case User32.VkUp: return Move(-1);
            case User32.VkNext: return Move(Page);
            case User32.VkPrior: return Move(-Page);
            case User32.VkHome: return MoveTo(0);
            case User32.VkEnd: return MoveTo(Count - 1);
            default: return false;
        }
    }

    private bool Move(int by) => MoveTo(_selected + by);

    private bool MoveTo(int index)
    {
        var target = Math.Clamp(index, 0, Math.Max(0, Count - 1));
        if (target == _selected) return false;

        _selected = target;
        Reveal();

        return true;
    }

    /// <summary>Scrolls the least amount that puts the cursor back on screen.</summary>
    private void Reveal()
    {
        if (_selected < _first) _first = _selected;
        else if (_selected >= _first + Page) _first = _selected - Page + 1;

        Clamp();
    }

    private void Clamp()
    {
        _first = Math.Clamp(_first, 0, Math.Max(0, Count - Page));
        _selected = Math.Clamp(_selected, 0, Math.Max(0, Count - 1));
    }
}
