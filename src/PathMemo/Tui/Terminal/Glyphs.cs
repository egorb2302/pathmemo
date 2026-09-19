namespace PathMemo.Tui.Terminal;

/// <summary>
/// Every character the interface is drawn with, in two sets.
/// </summary>
/// <remarks>
/// <para>
/// The frames are made of box-drawing and block characters, and those need a TrueType
/// console font. A console still using the legacy raster font - which is what an old
/// user profile hands to a double-clicked exe - would draw the whole screen as garbage.
/// So the glyphs go through here, and <see cref="ConsoleWindow"/> switches the set once
/// at startup after looking at the font actually in use.
/// </para>
/// <para>
/// The ASCII set is not a lesser mode: bars of <c>#</c> and <c>.</c> are how every disk
/// tool looked for twenty years, and it is readable in a terminal that can show nothing
/// else. <c>PATHMEMO_ASCII=1</c> forces it, which is also how it gets tested.
/// </para>
/// </remarks>
internal static class Glyphs
{
    private static bool _ascii;

    /// <summary>
    /// Whether the console font can only draw ASCII. Setting it also hands the control-character
    /// stand-in to <see cref="Text.Sanitizer"/>, which has no business knowing about consoles.
    /// </summary>
    internal static bool Ascii
    {
        get => _ascii;
        set
        {
            _ascii = value;
            Text.Sanitizer.Replacement = ControlChar;
        }
    }

    internal static char BarFilled => Ascii ? '#' : '█';       // FULL BLOCK
    internal static char BarEmpty => Ascii ? '.' : '░';        // LIGHT SHADE
    internal static char Rule => Ascii ? '-' : '─';            // BOX DRAWINGS LIGHT HORIZONTAL
    internal static char Cursor => Ascii ? '>' : '▸';          // BLACK RIGHT-POINTING SMALL TRIANGLE
    internal static char Mark => Ascii ? '*' : '×';            // MULTIPLICATION SIGN
    internal static char Caret => Ascii ? '_' : '█';

    /// <summary>The separator between facts on one line. Latin-1, but a raster font in a
    /// UTF-8 code page is unreliable even there.</summary>
    internal static string Dot => Ascii ? "  -  " : "  ·  ";

    /// <summary>Leads an explanatory line in the details panel.</summary>
    internal static string Bullet => Ascii ? "- " : "· ";

    /// <summary>Stands in for a control character in a file name (README section 14.4).</summary>
    internal static char ControlChar => Ascii ? '?' : '·';

    internal static string PanelTopLeft => Ascii ? "+-" : "┌─";
    internal static string PanelTopRight => Ascii ? "+" : "┐";
    internal static string PanelBottomLeft => Ascii ? "+" : "└";
    internal static string PanelBottomRight => Ascii ? "+" : "┘";
    internal static string PanelSide => Ascii ? "|" : "│";
    internal static string CurrentSort => Ascii ? "<- current" : "← current";

    /// <summary>Eight levels, lowest first, for the history row on Overview.</summary>
    internal static char[] Spark => Ascii ? AsciiSpark : BlockSpark;

    private static readonly char[] BlockSpark =
        ['▁', '▂', '▃', '▄', '▅', '▆', '▇', '█'];

    private static readonly char[] AsciiSpark =
        ['_', '.', ',', '-', '=', '+', '*', '#'];
}
