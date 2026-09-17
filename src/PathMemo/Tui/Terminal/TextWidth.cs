using System.Globalization;
using System.Text;

namespace PathMemo.Tui.Terminal;

/// <summary>
/// How many terminal columns a string occupies (README section 14.4, rule 3).
/// </summary>
/// <remarks>
/// <para>
/// <c>string.Length</c> is the wrong answer three times over: a CJK ideograph or an emoji
/// takes two cells, a combining mark takes none, and an astral character is two UTF-16
/// chars but one glyph. Any of the three makes every column in the tree drift, and the
/// drift is permanent - the next row starts from a different place.
/// </para>
/// <para>
/// The table below is the East Asian Width W and F ranges plus the emoji blocks that
/// terminals render double-width. It is a subset of the full Unicode table, chosen for
/// what actually appears in file names; a missing range costs one column of alignment,
/// not correctness of the numbers.
/// </para>
/// <para>
/// ZWJ sequences (a family emoji, say) are measured per rune, because whether the
/// terminal ligates them is not knowable from here: Windows Terminal does, conhost does
/// not. Measuring the parts is the choice that keeps conhost aligned.
/// </para>
/// </remarks>
internal static class TextWidth
{
    /// <summary>Marks a name that had to be cut. ASCII, so it needs no font.</summary>
    internal const char Cut = '~';

    internal static int Of(string text)
    {
        var width = 0;
        foreach (var rune in text.EnumerateRunes()) width += Of(rune);
        return width;
    }

    internal static int Of(ReadOnlySpan<char> text)
    {
        var width = 0;
        foreach (var rune in text.EnumerateRunes()) width += Of(rune);
        return width;
    }

    internal static int Of(Rune rune)
    {
        var value = rune.Value;

        // Fast path: ASCII printables, which is most of every path on a Windows disk.
        if (value is >= 0x20 and < 0x7F) return 1;

        // Control characters have no width of their own; the sanitizer replaces them with
        // a visible dot before anything reaches this point (README section 14.4, rule 2).
        if (value < 0x20 || value is >= 0x7F and <= 0x9F) return 0;

        // Combining marks, enclosing marks and format characters (including the emoji
        // variation selector U+FE0F) add nothing to the width of what they follow.
        var category = Rune.GetUnicodeCategory(rune);
        if (category is UnicodeCategory.NonSpacingMark
                     or UnicodeCategory.EnclosingMark
                     or UnicodeCategory.Format) return 0;

        return IsWide(value) ? 2 : 1;
    }

    /// <summary>
    /// Exactly <paramref name="columns"/> cells: truncated with <see cref="Cut"/> if too
    /// long, space-padded if too short. The unit of layout in every table we draw.
    /// </summary>
    internal static string Fit(string text, int columns)
    {
        if (columns <= 0) return "";

        var actual = Of(text);
        if (actual == columns) return text;
        if (actual < columns) return text + new string(' ', columns - actual);

        var cut = Truncate(text, columns - 1);
        return cut + Cut + new string(' ', Math.Max(0, columns - Of(cut) - 1));
    }

    /// <summary>
    /// The longest prefix that fits in <paramref name="columns"/> cells, cutting between
    /// runes rather than between the halves of a surrogate pair.
    /// </summary>
    internal static string Truncate(string text, int columns)
    {
        if (columns <= 0) return "";
        if (Of(text) <= columns) return text;

        var kept = new StringBuilder(text.Length);
        var used = 0;

        foreach (var rune in text.EnumerateRunes())
        {
            var width = Of(rune);
            if (used + width > columns) break;
            kept.Append(rune);
            used += width;
        }

        return kept.ToString();
    }

    /// <summary>Pads on the right, never truncating. For a column that must not lose text.</summary>
    internal static string Pad(string text, int columns)
    {
        var actual = Of(text);
        return actual >= columns ? text : text + new string(' ', columns - actual);
    }

    private static bool IsWide(int value)
    {
        var ranges = Wide;
        var low = 0;
        var high = ranges.Length - 1;

        while (low <= high)
        {
            var mid = (low + high) >> 1;
            var (start, end) = ranges[mid];

            if (value < start) high = mid - 1;
            else if (value > end) low = mid + 1;
            else return true;
        }

        return false;
    }

    /// <summary>
    /// East Asian Width W and F, plus the emoji ranges with default emoji presentation.
    /// Sorted; searched by bisection.
    /// </summary>
    private static readonly (int Start, int End)[] Wide =
    [
        (0x1100, 0x115F), (0x231A, 0x231B), (0x2329, 0x232A), (0x23E9, 0x23EC),
        (0x23F0, 0x23F0), (0x23F3, 0x23F3), (0x25FD, 0x25FE), (0x2614, 0x2615),
        (0x2648, 0x2653), (0x267F, 0x267F), (0x2693, 0x2693), (0x26A1, 0x26A1),
        (0x26AA, 0x26AB), (0x26BD, 0x26BE), (0x26C4, 0x26C5), (0x26CE, 0x26CE),
        (0x26D4, 0x26D4), (0x26EA, 0x26EA), (0x26F2, 0x26F3), (0x26F5, 0x26F5),
        (0x26FA, 0x26FA), (0x26FD, 0x26FD), (0x2705, 0x2705), (0x270A, 0x270B),
        (0x2728, 0x2728), (0x274C, 0x274C), (0x274E, 0x274E), (0x2753, 0x2755),
        (0x2757, 0x2757), (0x2795, 0x2797), (0x27B0, 0x27B0), (0x27BF, 0x27BF),
        (0x2B1B, 0x2B1C), (0x2B50, 0x2B50), (0x2B55, 0x2B55),
        (0x2E80, 0x2E99), (0x2E9B, 0x2EF3), (0x2F00, 0x2FD5), (0x2FF0, 0x2FFB),
        (0x3000, 0x303E), (0x3041, 0x3096), (0x3099, 0x30FF), (0x3105, 0x312F),
        (0x3131, 0x318E), (0x3190, 0x31E3), (0x31F0, 0x321E), (0x3220, 0x3247),
        (0x3250, 0x4DBF), (0x4E00, 0xA48C), (0xA490, 0xA4C6), (0xA960, 0xA97C),
        (0xAC00, 0xD7A3), (0xF900, 0xFAFF), (0xFE10, 0xFE19), (0xFE30, 0xFE52),
        (0xFE54, 0xFE66), (0xFE68, 0xFE6B), (0xFF01, 0xFF60), (0xFFE0, 0xFFE6),
        (0x16FE0, 0x16FE4), (0x17000, 0x187F7), (0x18800, 0x18CD5),
        (0x1B000, 0x1B152), (0x1B164, 0x1B167), (0x1B170, 0x1B2FB),
        (0x1F004, 0x1F004), (0x1F0CF, 0x1F0CF), (0x1F18E, 0x1F18E),
        (0x1F191, 0x1F19A), (0x1F200, 0x1F320), (0x1F32D, 0x1F335),
        (0x1F337, 0x1F37C), (0x1F37E, 0x1F393), (0x1F3A0, 0x1F3CA),
        (0x1F3CF, 0x1F3D3), (0x1F3E0, 0x1F3F0), (0x1F3F4, 0x1F3F4),
        (0x1F3F8, 0x1F43E), (0x1F440, 0x1F440), (0x1F442, 0x1F4FC),
        (0x1F4FF, 0x1F53D), (0x1F54B, 0x1F54E), (0x1F550, 0x1F567),
        (0x1F57A, 0x1F57A), (0x1F595, 0x1F596), (0x1F5A4, 0x1F5A4),
        (0x1F5FB, 0x1F64F), (0x1F680, 0x1F6C5), (0x1F6CC, 0x1F6CC),
        (0x1F6D0, 0x1F6D2), (0x1F6D5, 0x1F6D7), (0x1F6EB, 0x1F6EC),
        (0x1F6F4, 0x1F6FC), (0x1F7E0, 0x1F7EB), (0x1F90C, 0x1F93A),
        (0x1F93C, 0x1F945), (0x1F947, 0x1F978), (0x1F97A, 0x1F9CB),
        (0x1F9CD, 0x1F9FF), (0x1FA70, 0x1FA74), (0x1FA78, 0x1FA7A),
        (0x1FA80, 0x1FA86), (0x1FA90, 0x1FAA8), (0x1FAB0, 0x1FAB6),
        (0x1FAC0, 0x1FAC2), (0x1FAD0, 0x1FAD6),
        (0x20000, 0x2FFFD), (0x30000, 0x3FFFD),
    ];
}
