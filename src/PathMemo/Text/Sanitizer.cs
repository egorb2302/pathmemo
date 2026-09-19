using System.Text;

namespace PathMemo.Text;

/// <summary>
/// Makes a file name safe to draw (README section 14.4).
/// </summary>
/// <remarks>
/// <para>
/// A file name is input from an untrusted source (principle P10, threat T10). Three kinds
/// of character have to go before anything reaches the screen:
/// </para>
/// <list type="number">
/// <item>
/// <b>Bidi overrides.</b> <c>annexe‮txt.exe</c> renders as "annexe.txt" in every
/// terminal that honours them - and we would be showing that to a user with "open file"
/// one key away. This is a real technique, not a hypothetical.
/// </item>
/// <item>
/// <b>Control characters.</b> WSL, Samba and anything writing through <c>\\?\</c> can
/// create names containing them; drawn raw they move the cursor and tear the frame apart.
/// </item>
/// <item>
/// <b>Zero-width characters.</b> Invisible, so two different names look identical -
/// exactly the confusion a deletion decision must not rest on.
/// </item>
/// </list>
/// <para>
/// Escaping rather than dropping the whole name matters: the user still has to recognise
/// their file. The bytes on disk are untouched - this is a display transform, and the
/// path handed to the clipboard or to Explorer is the original.
/// </para>
/// </remarks>
internal static class Sanitizer
{
    /// <summary>
    /// Stands in for a control character: one column wide, visible, unambiguous.
    /// </summary>
    /// <remarks>
    /// MIDDLE DOT by default. It is settable because the stand-in belongs to whoever draws,
    /// not to this function: a console handed a raster font cannot draw U+00B7 and asks for
    /// <c>?</c> instead (<c>Glyphs.Ascii</c>), while the GUI's Segoe UI always can. Sanitising
    /// itself is a rule about untrusted input (README section 14.4) and is the same for both.
    /// </remarks>
    internal static char Replacement { get; set; } = '·';

    internal static string Clean(string raw)
    {
        // The overwhelming majority of names need nothing; walk once to find out and
        // return the same instance when that is so.
        var needsWork = false;
        foreach (var c in raw)
        {
            if (!IsPlain(c)) { needsWork = true; break; }
        }

        if (!needsWork) return raw;

        var clean = new StringBuilder(raw.Length);
        foreach (var c in raw)
        {
            if (IsPlain(c)) { clean.Append(c); continue; }
            if (IsInvisible(c)) continue;                  // dropped outright
            if (IsControl(c)) { clean.Append(Replacement); continue; }
            clean.Append(c);                               // ordinary non-ASCII
        }

        return clean.ToString();
    }

    /// <summary>Whether the text would be altered - used by the details view to say so.</summary>
    internal static bool NeedsCleaning(string raw)
    {
        foreach (var c in raw)
            if (IsControl(c) || IsInvisible(c)) return true;

        return false;
    }

    private static bool IsPlain(char c) => c is >= ' ' and < '';

    private static bool IsControl(char c) => c < ' ' || c is >= '' and <= '';

    private static bool IsInvisible(char c) => c switch
    {
        '‎' or '‏' => true,                      // LRM, RLM
        >= '‪' and <= '‮' => true,               // LRE, RLE, PDF, LRO, RLO
        >= '⁦' and <= '⁩' => true,               // LRI, RLI, FSI, PDI
        >= '​' and <= '‍' => true,               // zero-width space, non-joiner, joiner
        '﻿' => true,                                  // zero-width no-break space
        _ => false,
    };
}
