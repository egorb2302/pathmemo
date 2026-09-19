namespace PathMemo.Gui.Render;

/// <summary>
/// A GDI <c>COLORREF</c>, constructed from the hex triple everybody actually writes.
/// </summary>
/// <remarks>
/// <c>COLORREF</c> is <c>0x00BBGGRR</c> - the bytes are the other way round from the
/// <c>#RRGGBB</c> every palette, screenshot and design note is written in. Passing a raw
/// <c>uint</c> to GDI therefore produces a colour that is wrong in a way that looks
/// deliberate: blue where red was meant, and the same hue family throughout, so it reads as
/// a styling choice rather than a bug. Wrapping it means the swap happens exactly once.
/// </remarks>
internal readonly record struct Colour(uint Ref)
{
    /// <summary>From <c>0xRRGGBB</c>, the way the palette below is written.</summary>
    internal static Colour Rgb(uint rgb) =>
        new(((rgb & 0x0000FF) << 16) | (rgb & 0x00FF00) | ((rgb & 0xFF0000) >> 16));

    internal byte R => (byte)(Ref & 0xFF);

    internal byte G => (byte)((Ref >> 8) & 0xFF);

    internal byte B => (byte)((Ref >> 16) & 0xFF);

    /// <summary>
    /// <paramref name="t"/> of the way from this colour to <paramref name="other"/>, 0 to 1.
    /// </summary>
    /// <remarks>
    /// Channel-wise in whatever order the channels happen to sit, which is safe because both
    /// operands use the same order. Blending towards the background is how a hovered row, a
    /// selected row and the depth shading of a treemap are all expressed - one function
    /// instead of three more palette entries that would have to be kept in step.
    /// </remarks>
    internal Colour Mix(Colour other, double t)
    {
        var f = Math.Clamp(t, 0, 1);

        static uint Channel(uint a, uint b, double f) => (uint)(a + (b - (double)a) * f);

        var r = Channel(Ref & 0xFF, other.Ref & 0xFF, f);
        var g = Channel((Ref >> 8) & 0xFF, (other.Ref >> 8) & 0xFF, f);
        var b = Channel((Ref >> 16) & 0xFF, (other.Ref >> 16) & 0xFF, f);

        return new Colour(r | (g << 8) | (b << 16));
    }
}
