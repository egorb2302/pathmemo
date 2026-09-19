namespace PathMemo.Gui.Render;

internal enum FontRole
{
    /// <summary>Segoe UI 9pt: rows, labels, everything ordinary.</summary>
    Body,

    /// <summary>Semibold body: column headers, a selected tab, a total.</summary>
    Bold,

    /// <summary>8pt: badges, units, secondary counts.</summary>
    Small,

    /// <summary>The one large string on a screen - a volume letter, a heading.</summary>
    Title,

    /// <summary>Consolas: paths and sizes that have to line up character by character.</summary>
    Mono,
}

internal enum Align
{
    Left,
    Right,
    Centre,
}

/// <summary>
/// Everything the views are allowed to draw with (README section 24.2).
/// </summary>
/// <remarks>
/// <para>
/// Four operations, and deliberately no more: a filled rectangle, a run of text, and a clip
/// to push and pop. Every part of this application - bars, tables, the treemap, tabs, the
/// scrollbar - is rectangles and text, so an API with gradients, paths and transforms would
/// be surface nobody calls and a temptation to make the window look like something other
/// than a disk tool.
/// </para>
/// <para>
/// It is an interface for the reason README section 17.2 allows one: there are two
/// implementations. <c>GdiPainter</c> draws, and <c>RecordingPainter</c> collects the calls
/// so a test can assert what a view would have drawn without a window, a message loop or a
/// screenshot - the same trick that made the TUI's frames testable (README section 22).
/// </para>
/// </remarks>
internal interface IPainter
{
    /// <summary>The height of one line of body text, and so of one row.</summary>
    int LineHeight { get; }

    int Height(FontRole role);

    /// <summary>
    /// A measurement written at 96 dpi, in this window's pixels.
    /// </summary>
    /// <remarks>
    /// Every padding, column width and row height in the views is written as the number it
    /// would be on an unscaled display and passed through here. A layout with raw pixels in it
    /// is a layout that is wrong on three quarters of the laptops sold.
    /// </remarks>
    int Scale(int at96);

    /// <summary>The width the text would occupy, in pixels, for sizing a column.</summary>
    int Measure(string text, FontRole role = FontRole.Body);

    void Fill(Rect area, Colour colour);

    /// <summary>
    /// One line of text, vertically centred in <paramref name="area"/> and clipped to it.
    /// </summary>
    /// <param name="middleEllipsis">
    /// Truncate in the middle, keeping both ends, which is what a path needs
    /// (README section 14.4). The end is dropped otherwise.
    /// </param>
    void Text(Rect area, string text, Colour colour,
        FontRole role = FontRole.Body, Align align = Align.Left, bool middleEllipsis = false);

    /// <summary>Restricts drawing to <paramref name="area"/> until <see cref="Unclip"/>.</summary>
    void Clip(Rect area);

    void Unclip();
}
