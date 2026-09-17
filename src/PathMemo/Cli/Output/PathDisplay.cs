using System.Text;

namespace PathMemo.Cli.Output;

internal static class PathDisplay
{
    private const string Ellipsis = "...";

    /// <summary>
    /// Fits a path into <paramref name="width"/> columns by removing whole middle
    /// segments.
    /// </summary>
    /// <remarks>
    /// Truncating the tail - the obvious approach - throws away the only part that
    /// identifies the item: <c>C:\Users\me\projects\bigap...</c> says nothing, while
    /// <c>C:\Users\me\...\node_modules\.bin</c> says everything.
    /// </remarks>
    internal static string Shorten(string path, int width)
    {
        if (width <= 0) return "";
        if (Width(path) <= width) return path;

        var segments = path.Split(Path.DirectorySeparatorChar);
        if (segments.Length <= 2) return HardTruncate(path, width);

        // Keep the volume root and grow the tail back from the end for as long as it fits.
        var head = segments[0];
        var kept = new List<string>();
        var budget = width - Width(head) - 1 - Ellipsis.Length - 1;

        for (var i = segments.Length - 1; i >= 1 && budget > 0; i--)
        {
            var cost = Width(segments[i]) + 1;
            if (cost > budget) break;
            kept.Insert(0, segments[i]);
            budget -= cost;
        }

        if (kept.Count == 0) return HardTruncate(path, width);

        var sb = new StringBuilder(width);
        sb.Append(head).Append(Path.DirectorySeparatorChar).Append(Ellipsis);
        foreach (var segment in kept) sb.Append(Path.DirectorySeparatorChar).Append(segment);

        var result = sb.ToString();
        return Width(result) <= width ? result : HardTruncate(path, width);
    }

    private static string HardTruncate(string text, int width) =>
        width <= Ellipsis.Length
            ? text[..Math.Min(text.Length, width)]
            : Ellipsis + text[^Math.Min(text.Length, width - Ellipsis.Length)..];

    /// <summary>
    /// Display columns, not <c>string.Length</c>: CJK and emoji occupy two cells, and
    /// counting chars would make every table containing them drift out of alignment
    /// (README section 14.4). Replaced by the full East Asian Width table in P5.
    /// </summary>
    internal static int Width(string text)
    {
        var width = 0;
        foreach (var rune in text.EnumerateRunes())
            width += IsWide(rune.Value) ? 2 : 1;
        return width;
    }

    private static bool IsWide(int cp) =>
        (cp >= 0x1100 && cp <= 0x115F) ||    // Hangul Jamo
        (cp >= 0x2E80 && cp <= 0xA4CF) ||    // CJK radicals .. Yi
        (cp >= 0xAC00 && cp <= 0xD7A3) ||    // Hangul syllables
        (cp >= 0xF900 && cp <= 0xFAFF) ||    // CJK compatibility ideographs
        (cp >= 0xFE30 && cp <= 0xFE6F) ||    // CJK compatibility forms
        (cp >= 0xFF00 && cp <= 0xFF60) ||    // Fullwidth forms
        (cp >= 0xFFE0 && cp <= 0xFFE6) ||
        (cp >= 0x1F300 && cp <= 0x1F64F) ||  // Emoji
        (cp >= 0x1F900 && cp <= 0x1F9FF) ||
        (cp >= 0x20000 && cp <= 0x3FFFD);    // CJK extensions B..
}
