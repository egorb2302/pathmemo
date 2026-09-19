using PathMemo.Gui.Render;

namespace PathMemo.Tests;

/// <summary>
/// An <see cref="IPainter"/> that records instead of drawing (README section 24.6).
/// </summary>
/// <remarks>
/// <para>
/// This is what makes the window testable without a window. A view is a function from state to
/// a list of fills and text runs, so a test can assert what would appear on screen - that a
/// row carries the right size, that a badge is there, that nothing is drawn outside its area -
/// with no message loop, no desktop and no screenshot to compare.
/// </para>
/// <para>
/// It is the same trick the TUI's tests use against <c>Screen.TextAt</c> (README section 22),
/// and the second implementation that justifies <see cref="IPainter"/> being an interface at
/// all under the rule of README section 17.2.
/// </para>
/// <para>
/// Text is measured at six pixels per character and lines are eighteen pixels high: fixed, so
/// a test asserting a layout does not depend on which fonts the machine running it happens to
/// have installed.
/// </para>
/// </remarks>
internal sealed class RecordingPainter : IPainter
{
    internal readonly record struct TextRun(Rect Area, string Text, Colour Colour, FontRole Role, Align Align);

    internal readonly record struct FillRun(Rect Area, Colour Colour);

    internal List<TextRun> Texts { get; } = [];

    internal List<FillRun> Fills { get; } = [];

    /// <summary>The clip rectangles that are open right now, innermost last.</summary>
    internal List<Rect> Clips { get; } = [];

    /// <summary>Every clip that was pushed, for asserting that a view clipped at all.</summary>
    internal List<Rect> ClipHistory { get; } = [];

    public int LineHeight => 18;

    public int Height(FontRole role) => role == FontRole.Small ? 14 : role == FontRole.Title ? 24 : 18;

    public int Scale(int at96) => at96;

    public int Measure(string text, FontRole role = FontRole.Body) => text.Length * 6;

    public void Fill(Rect area, Colour colour) => Fills.Add(new FillRun(area, colour));

    public void Text(Rect area, string text, Colour colour,
        FontRole role = FontRole.Body, Align align = Align.Left, bool middleEllipsis = false)
    {
        if (area.IsEmpty || text.Length == 0) return;

        Texts.Add(new TextRun(area, text, colour, role, align));
    }

    public void Clip(Rect area)
    {
        Clips.Add(area);
        ClipHistory.Add(area);
    }

    public void Unclip()
    {
        if (Clips.Count > 0) Clips.RemoveAt(Clips.Count - 1);
    }

    /// <summary>Whether any text run says exactly this.</summary>
    internal bool Said(string text) => Texts.Exists(t => t.Text == text);

    /// <summary>Whether any text run contains this.</summary>
    internal bool Mentions(string fragment) =>
        Texts.Exists(t => t.Text.Contains(fragment, StringComparison.Ordinal));

    internal IEnumerable<string> AllText => Texts.ConvertAll(t => t.Text);
}
