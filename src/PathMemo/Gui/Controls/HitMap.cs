using PathMemo.Gui.Render;

namespace PathMemo.Gui.Controls;

internal enum HitKind
{
    None,

    /// <summary>One of the view tabs. <c>Index</c> is the <c>GuiView</c>.</summary>
    Tab,

    /// <summary>A toolbar or dialog button. <c>Index</c> is a <c>GuiAction</c>.</summary>
    Button,

    /// <summary>A row of the main list. <c>Index</c> is the row, not the node.</summary>
    Row,

    /// <summary>A segment of the path above the list. <c>Index</c> counts from the root.</summary>
    Crumb,

    /// <summary>A column header, for sorting. <c>Index</c> is the column.</summary>
    Column,

    /// <summary>A block of the treemap. <c>Index</c> is into the laid-out block list.</summary>
    Block,

    /// <summary>The scrollbar thumb or trough.</summary>
    Scroll,

    /// <summary>A volume card on the overview.</summary>
    Volume,
}

internal readonly record struct Hit(HitKind Kind, int Index)
{
    internal static readonly Hit None = new(HitKind.None, 0);

    internal bool Is(HitKind kind) => Kind == kind;
}

/// <summary>
/// What is under the pointer, recorded while painting (README section 24.3).
/// </summary>
/// <remarks>
/// <para>
/// The views draw and register their clickable areas in the same pass, and a click is
/// resolved by looking through what the last frame registered. There is no control tree and
/// no second layout pass.
/// </para>
/// <para>
/// That is not laziness, it is the property worth having: a retained hierarchy can disagree
/// with the picture - a row that moved when the window was resized but whose bounds were not
/// updated is a click that deletes the wrong thing. Here the only bounds that exist are the
/// ones something was actually drawn into, so "what you clicked" and "what you saw" cannot
/// come apart. The cost is that a click needs a frame to have been painted, which is always
/// true by the time a pointer is over the window.
/// </para>
/// <para>
/// Regions are searched newest first, so something drawn over something else - a dialog over
/// the list beneath it - wins, which is the order the user sees.
/// </para>
/// </remarks>
internal sealed class HitMap
{
    private readonly List<(Rect Area, Hit What)> _regions = [];

    /// <summary>
    /// Regions added after this point shadow everything before it, and nothing before it can
    /// be hit. Used by a modal: the list behind must not answer a click aimed at the dialog.
    /// </summary>
    private int _floor;

    internal void Clear()
    {
        _regions.Clear();
        _floor = 0;
    }

    internal void Add(Rect area, HitKind kind, int index = 0)
    {
        if (area.IsEmpty) return;

        _regions.Add((area, new Hit(kind, index)));
    }

    /// <summary>Makes everything registered so far unclickable, for a modal layer.</summary>
    internal void Seal() => _floor = _regions.Count;

    internal Hit At(int x, int y)
    {
        for (var i = _regions.Count - 1; i >= _floor; i--)
            if (_regions[i].Area.Contains(x, y)) return _regions[i].What;

        return Hit.None;
    }

    /// <summary>The area a region was drawn into, for a caller that needs to scroll it into view.</summary>
    internal Rect AreaOf(Hit what)
    {
        for (var i = _regions.Count - 1; i >= _floor; i--)
            if (_regions[i].What == what) return _regions[i].Area;

        return Rect.Empty;
    }
}
