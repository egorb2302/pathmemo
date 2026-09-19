using PathMemo.Platform.Native;

namespace PathMemo.Gui.Render;

/// <summary>
/// The off-screen bitmap every frame is composed in, and blitted from (README section 24.2).
/// </summary>
/// <remarks>
/// <para>
/// A window that draws its parts straight onto the screen flickers: the background is
/// visible for the instant between the erase and the text. The whole frame is therefore built
/// in one <c>CreateDIBSection</c> bitmap and copied across in a single <c>BitBlt</c>, which is
/// the same reason the TUI composes a frame buffer before writing a byte (README section 14.7).
/// </para>
/// <para>
/// The bitmap only ever grows. A window being dragged to a larger size would otherwise
/// reallocate several megabytes per <c>WM_SIZE</c>, dozens of times a second; keeping the
/// largest seen size costs the memory of one screen - 8 MB at 1920x1080 - and the budget of
/// README section 20 is 150 MB with a whole snapshot open.
/// </para>
/// </remarks>
internal sealed class Canvas : IDisposable
{
    private nint _dc;
    private nint _bitmap;
    private nint _previous;
    private int _capacityWidth;
    private int _capacityHeight;

    internal int Width { get; private set; }

    internal int Height { get; private set; }

    internal nint Dc => _dc;

    internal Rect Bounds => new(0, 0, Width, Height);

    /// <summary>
    /// Makes the surface at least <paramref name="width"/> by <paramref name="height"/>.
    /// </summary>
    /// <returns>False when the bitmap could not be created, in which case nothing may draw.</returns>
    internal unsafe bool Resize(int width, int height)
    {
        Width = Math.Max(1, width);
        Height = Math.Max(1, height);

        if (_bitmap != nint.Zero && Width <= _capacityWidth && Height <= _capacityHeight)
            return true;

        var w = Math.Max(Width, _capacityWidth);
        var h = Math.Max(Height, _capacityHeight);

        Release();

        _dc = Gdi32.CreateCompatibleDC(nint.Zero);
        if (_dc == nint.Zero) return false;

        var header = new Gdi32.BitmapInfoHeader
        {
            Size = (uint)sizeof(Gdi32.BitmapInfoHeader),
            Width = w,

            // Negative: rows top-down, so the first scan line is the top one. A bottom-up DIB
            // is the GDI default and the source of every upside-down first screenshot.
            Height = -h,
            Planes = 1,
            BitCount = 32,
            Compression = Gdi32.BiRgb,
        };

        _bitmap = Gdi32.CreateDIBSection(_dc, &header, Gdi32.DibRgbColors, out _, nint.Zero, 0);
        if (_bitmap == nint.Zero)
        {
            Gdi32.DeleteDC(_dc);
            _dc = nint.Zero;
            return false;
        }

        _previous = Gdi32.SelectObject(_dc, _bitmap);
        _capacityWidth = w;
        _capacityHeight = h;

        return true;
    }

    /// <summary>Copies the live part of the surface onto the window.</summary>
    internal void Blit(nint target)
    {
        if (_dc == nint.Zero) return;

        Gdi32.BitBlt(target, 0, 0, Width, Height, _dc, 0, 0, Gdi32.SrcCopy);
    }

    private void Release()
    {
        if (_dc != nint.Zero && _previous != nint.Zero) Gdi32.SelectObject(_dc, _previous);
        if (_bitmap != nint.Zero) Gdi32.DeleteObject(_bitmap);
        if (_dc != nint.Zero) Gdi32.DeleteDC(_dc);

        _bitmap = nint.Zero;
        _dc = nint.Zero;
        _previous = nint.Zero;
    }

    public void Dispose() => Release();
}
