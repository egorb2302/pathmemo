using PathMemo.Platform.Native;
using PathMemo.Text;

namespace PathMemo.Gui.Render;

/// <summary>
/// <see cref="IPainter"/> over a GDI device context (README section 24.2).
/// </summary>
/// <remarks>
/// <para>
/// Solid rectangles go through the stock <c>DC_BRUSH</c> and <c>PatBlt</c> rather than a
/// brush per colour: a frame fills several hundred rectangles in a dozen colours, and creating
/// and deleting a brush for each is a GDI object churn that shows up as a stutter long before
/// the drawing does. One brush, its colour changed, is the same picture with no allocation.
/// </para>
/// <para>
/// <b>Every string is sanitised here</b>, not in the views (README section 14.4). File names
/// are untrusted input and the rule is only as good as its least careful caller, so the one
/// place all text passes through is the one place worth enforcing it. The cost is a scan per
/// string, and <c>Sanitizer.Clean</c> returns the original instance when nothing needs
/// changing, which is almost always. The raw name still reaches the clipboard and Explorer,
/// because those do not come through here.
/// </para>
/// </remarks>
internal sealed class GdiPainter : IPainter
{
    private readonly nint _dc;
    private readonly FontSet _fonts;
    private readonly Stack<int> _clips = new();

    private FontRole _font = FontRole.Body;
    private uint _colour = uint.MaxValue;
    private bool _stateValid;

    internal GdiPainter(nint dc, FontSet fonts)
    {
        _dc = dc;
        _fonts = fonts;
    }

    public int LineHeight => _fonts.Height(FontRole.Body);

    public int Height(FontRole role) => _fonts.Height(role);

    public int Scale(int at96) => _fonts.Scale(at96);

    public unsafe int Measure(string text, FontRole role = FontRole.Body)
    {
        if (text.Length == 0) return 0;

        Apply();
        SelectFont(role);

        fixed (char* p = text)
        {
            return Gdi32.GetTextExtentPoint32(_dc, p, text.Length, out var size) ? size.X : 0;
        }
    }

    public void Fill(Rect area, Colour colour)
    {
        if (area.IsEmpty) return;

        Apply();
        Gdi32.SetDCBrushColor(_dc, colour.Ref);
        Gdi32.PatBlt(_dc, area.X, area.Y, area.Width, area.Height, Gdi32.PatCopy);
    }

    public unsafe void Text(Rect area, string text, Colour colour,
        FontRole role = FontRole.Body, Align align = Align.Left, bool middleEllipsis = false)
    {
        if (area.IsEmpty || text.Length == 0) return;

        Apply();
        SelectFont(role);

        if (_colour != colour.Ref)
        {
            Gdi32.SetTextColor(_dc, colour.Ref);
            _colour = colour.Ref;
        }

        var clean = Sanitizer.Clean(text);

        var format = User32.DtSingleLine | User32.DtVCenter | User32.DtNoPrefix
                     | (middleEllipsis ? User32.DtPathEllipsis : User32.DtEndEllipsis)
                     | align switch
                     {
                         Align.Right => User32.DtRight,
                         Align.Centre => User32.DtCenter,
                         _ => User32.DtLeft,
                     };

        var rect = new User32.Rect
        {
            Left = area.X,
            Top = area.Y,
            Right = area.Right,
            Bottom = area.Bottom,
        };

        fixed (char* p = clean)
        {
            User32.DrawText(_dc, p, clean.Length, ref rect, format);
        }
    }

    public void Clip(Rect area)
    {
        _clips.Push(Gdi32.SaveDC(_dc));
        Gdi32.IntersectClipRect(_dc, area.X, area.Y, area.Right, area.Bottom);

        // SaveDC captured the selected objects, so whatever RestoreDC puts back has to be
        // re-applied rather than assumed.
        _stateValid = false;
    }

    public void Unclip()
    {
        if (_clips.Count == 0) return;

        Gdi32.RestoreDC(_dc, _clips.Pop());
        _stateValid = false;
    }

    /// <summary>
    /// Re-establishes the device context state this painter assumes.
    /// </summary>
    /// <remarks>
    /// Called before every operation and does nothing on all but the first after a clip
    /// change. Cheaper than reasoning about whether the last <see cref="Unclip"/> undid the
    /// brush selection - which is the sort of question that gets answered wrongly once and
    /// then draws in black for a release.
    /// </remarks>
    private void Apply()
    {
        if (_stateValid) return;

        Gdi32.SelectObject(_dc, Gdi32.GetStockObject(Gdi32.DcBrush));
        Gdi32.SetBkMode(_dc, Gdi32.TransparentBk);
        Gdi32.SelectObject(_dc, _fonts.Handle(_font));
        Gdi32.SetTextColor(_dc, _colour == uint.MaxValue ? 0 : _colour);

        _stateValid = true;
    }

    private void SelectFont(FontRole role)
    {
        if (_font == role) return;

        Gdi32.SelectObject(_dc, _fonts.Handle(role));
        _font = role;
    }
}
