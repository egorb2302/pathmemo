using Microsoft.Win32;
using PathMemo.Analysis;

namespace PathMemo.Gui.Render;

/// <summary>
/// The palette, and where it comes from (README section 24.2).
/// </summary>
/// <remarks>
/// <para>
/// Two palettes, chosen from the system's own light/dark preference, because an application
/// that is bright white on a machine set to dark is not a style - it is a window that did not
/// look. The registry value is the documented source and is what every other application
/// reads; there is no supported API that answers it for a non-packaged Win32 process.
/// </para>
/// <para>
/// Flat by design: one background, one surface, one border, three text weights and one
/// accent. Everything else - hovered, selected, disabled, the shading that separates one
/// treemap level from the next - is <see cref="Colour.Mix"/> between two of those. A palette
/// with an entry per state drifts, because nobody updates all eleven.
/// </para>
/// </remarks>
internal sealed record Theme
{
    internal required bool Dark { get; init; }

    /// <summary>Behind everything.</summary>
    internal required Colour Background { get; init; }

    /// <summary>Panels, headers and the rows of a table that need to lift off the background.</summary>
    internal required Colour Surface { get; init; }

    /// <summary>Hairlines between regions. One pixel, never a box.</summary>
    internal required Colour Border { get; init; }

    internal required Colour Text { get; init; }

    /// <summary>Secondary text: counts, units, the parts of a row that are context.</summary>
    internal required Colour Dim { get; init; }

    /// <summary>Text on an accent-coloured fill.</summary>
    internal required Colour OnAccent { get; init; }

    internal required Colour Accent { get; init; }

    /// <summary>The bar in a size row, and the default treemap fill.</summary>
    internal required Colour Bar { get; init; }

    /// <summary>A bar's unfilled remainder.</summary>
    internal required Colour BarTrack { get; init; }

    internal required Colour Warning { get; init; }

    internal required Colour Danger { get; init; }

    internal required Colour Good { get; init; }

    internal Colour Hover => Surface.Mix(Text, Dark ? 0.10 : 0.06);

    internal Colour Selection => Accent.Mix(Background, Dark ? 0.62 : 0.80);

    /// <summary>
    /// The fill for a node of the given category, used by the treemap and the bars.
    /// </summary>
    /// <remarks>
    /// Colour carries the one fact a size view cannot: what the space is. A 40 GB block is a
    /// different decision when it is media than when it is cache, and the categories already
    /// exist for the aggregates (README section 6). Hues are spaced rather than chosen for
    /// prettiness, and the same category is the same hue in both palettes so a screenshot
    /// taken in dark mode still reads in light.
    /// </remarks>
    internal Colour Category(FileCategory category) => category switch
    {
        FileCategory.Media => Colour.Rgb(Dark ? 0xB06AB3u : 0x9C5BA8u),
        FileCategory.Archive => Colour.Rgb(Dark ? 0xC98A3Cu : 0xB8762Bu),
        FileCategory.Cache => Colour.Rgb(Dark ? 0x7A8794u : 0x8D99A6u),
        FileCategory.Source => Colour.Rgb(Dark ? 0x4FA96Bu : 0x3E9159u),
        FileCategory.Document => Colour.Rgb(Dark ? 0x4C8FD4u : 0x3C7CC0u),
        FileCategory.App => Colour.Rgb(Dark ? 0xD06A72u : 0xC0565Fu),
        FileCategory.System => Colour.Rgb(Dark ? 0x8A7BC8u : 0x7A6BB8u),
        _ => Colour.Rgb(Dark ? 0x5A6472u : 0xA8B0BAu),
    };

    internal static Theme Dusk { get; } = new()
    {
        Dark = true,
        Background = Colour.Rgb(0x1B1D21),
        Surface = Colour.Rgb(0x23262B),
        Border = Colour.Rgb(0x33373D),
        Text = Colour.Rgb(0xE6E8EA),
        Dim = Colour.Rgb(0x9097A0),
        OnAccent = Colour.Rgb(0xFFFFFF),
        Accent = Colour.Rgb(0x4C9AF0),
        Bar = Colour.Rgb(0x3D7DC4),
        BarTrack = Colour.Rgb(0x2C3035),
        Warning = Colour.Rgb(0xD9A441),
        Danger = Colour.Rgb(0xD95A5A),
        Good = Colour.Rgb(0x5AB877),
    };

    internal static Theme Day { get; } = new()
    {
        Dark = false,
        Background = Colour.Rgb(0xFFFFFF),
        Surface = Colour.Rgb(0xF4F5F7),
        Border = Colour.Rgb(0xDFE2E6),
        Text = Colour.Rgb(0x1A1D21),
        Dim = Colour.Rgb(0x6B7280),
        OnAccent = Colour.Rgb(0xFFFFFF),
        Accent = Colour.Rgb(0x0B6BCB),
        Bar = Colour.Rgb(0x3C86D8),
        BarTrack = Colour.Rgb(0xE8EAED),
        Warning = Colour.Rgb(0xB77B14),
        Danger = Colour.Rgb(0xC0392B),
        Good = Colour.Rgb(0x2E8B57),
    };

    /// <summary>
    /// The palette the system asks for, light when the preference cannot be read.
    /// </summary>
    /// <remarks>
    /// <c>AppsUseLightTheme</c> missing means a Windows old enough not to have the setting,
    /// and those are light. A failure to read the registry is treated the same way rather than
    /// reported: a colour scheme is not worth a message, and the window is perfectly usable in
    /// the other palette.
    /// </remarks>
    internal static Theme FromSystem()
    {
        // PATHMEMO_THEME forces a palette, the way PATHMEMO_ASCII=1 forces the glyph fallback
        // (README section 14.7). Both exist for the same reason: checking how the other mode
        // looks should not require changing a setting on the machine doing the checking.
        switch (Environment.GetEnvironmentVariable("PATHMEMO_THEME"))
        {
            case "light": return Day;
            case "dark": return Dusk;
        }

        try
        {
            var value = Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "AppsUseLightTheme", 1);

            return value is int light && light == 0 ? Dusk : Day;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException
                                      or UnauthorizedAccessException)
        {
            return Day;
        }
    }
}
