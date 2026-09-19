using PathMemo.Platform.Native;

namespace PathMemo.Gui.Render;

/// <summary>
/// The five fonts, created once for a given DPI and measured once (README section 24.2).
/// </summary>
/// <remarks>
/// <para>
/// A font is a GDI handle and creating one costs a font-mapper lookup, so they are made when
/// the window learns its DPI and remade only when that changes. Point sizes are converted
/// with the window's own DPI rather than the system's: on two monitors at different scales
/// the same window gets both, and a font built for the wrong one is either blurry or the
/// wrong size, which is the usual way a hand-written Win32 window announces itself.
/// </para>
/// <para>
/// Heights come from <c>GetTextMetrics</c> rather than from the point size, because the row
/// height has to hold ascent, descent and internal leading for the face the font mapper
/// actually chose - which is not necessarily the one asked for.
/// </para>
/// </remarks>
internal sealed class FontSet : IDisposable
{
    private const int Roles = 5;

    private readonly nint[] _fonts = new nint[Roles];
    private readonly int[] _heights = new int[Roles];

    internal FontSet(uint dpi)
    {
        Dpi = dpi;

        _fonts[(int)FontRole.Body] = Create("Segoe UI", 9, Gdi32.FwNormal, dpi);
        _fonts[(int)FontRole.Bold] = Create("Segoe UI", 9, Gdi32.FwSemibold, dpi);
        _fonts[(int)FontRole.Small] = Create("Segoe UI", 8, Gdi32.FwNormal, dpi);
        _fonts[(int)FontRole.Title] = Create("Segoe UI", 13, Gdi32.FwSemibold, dpi);
        _fonts[(int)FontRole.Mono] = Create("Consolas", 9, Gdi32.FwNormal, dpi, fixedPitch: true);

        Measure();
    }

    internal uint Dpi { get; }

    internal nint Handle(FontRole role) => _fonts[(int)role];

    internal int Height(FontRole role) => _heights[(int)role];

    /// <summary>Scales a design measurement given at 96 dpi to this window's dpi.</summary>
    internal int Scale(int at96) => (int)Math.Round(at96 * Dpi / 96.0);

    private static nint Create(string face, int points, int weight, uint dpi, bool fixedPitch = false)
    {
        var log = new Gdi32.LogFont
        {
            // Negative means "character height", i.e. the em size rather than the cell,
            // which is what a point size means everywhere outside GDI.
            Height = -(int)Math.Round(points * dpi / 72.0),
            Weight = weight,
            CharSet = Gdi32.DefaultCharset,
            Quality = Gdi32.ClearTypeQuality,
            PitchAndFamily = fixedPitch ? Gdi32.FixedPitch : Gdi32.VariablePitch,
        };

        log.SetFace(face);
        return Gdi32.CreateFontIndirect(ref log);
    }

    private void Measure()
    {
        var dc = Gdi32.CreateCompatibleDC(nint.Zero);
        if (dc == nint.Zero) return;

        try
        {
            for (var role = 0; role < Roles; role++)
            {
                var previous = Gdi32.SelectObject(dc, _fonts[role]);
                _heights[role] = Gdi32.GetTextMetrics(dc, out var metrics)
                    ? metrics.Height + metrics.ExternalLeading
                    : Scale(18);

                Gdi32.SelectObject(dc, previous);
            }
        }
        finally
        {
            Gdi32.DeleteDC(dc);
        }
    }

    public void Dispose()
    {
        foreach (var font in _fonts)
            if (font != nint.Zero) Gdi32.DeleteObject(font);
    }
}
