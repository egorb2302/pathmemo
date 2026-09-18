using System.IO.Enumeration;

namespace PathMemo.Config;

/// <summary>
/// Path patterns for the configuration: <c>%WINDIR%\Temp\**</c>, <c>*.iso</c>,
/// <c>**\node_modules</c> (README section 12.2).
/// </summary>
/// <remarks>
/// <para>
/// One syntax, not two. Per segment this is
/// <see cref="FileSystemName.MatchesSimpleExpression(ReadOnlySpan{char}, ReadOnlySpan{char}, bool)"/>
/// - the matcher the runtime already uses for enumeration, allocation-free - extended with
/// <c>**</c> for "any number of segments". Regular expressions are a separate, opt-in form
/// precisely because one bad pattern would otherwise hang a million-path scan.
/// </para>
/// <para>
/// Everything is compared with invariant case folding: paths on NTFS are case-insensitive,
/// and the project runs under <c>InvariantGlobalization</c>, so there is no locale in which
/// these answers differ.
/// </para>
/// </remarks>
internal sealed class PathGlob
{
    private readonly string[] _segments;
    private readonly bool _nameOnly;

    /// <summary>The pattern as written, for messages.</summary>
    internal string Text { get; }

    /// <summary>
    /// The leading run of literal segments, e.g. <c>C:\Windows\Temp</c> of
    /// <c>C:\Windows\Temp\**</c>. The protection rules canonicalise this part through the
    /// filesystem, so a pattern written with a drive letter still matches a path expressed
    /// as a volume GUID (README section 9.3).
    /// </summary>
    internal string LiteralPrefix { get; }

    /// <summary>Number of leading segments covered by <see cref="LiteralPrefix"/>.</summary>
    internal int LiteralSegments { get; }

    private PathGlob(string text, string[] segments, bool nameOnly)
    {
        Text = text;
        _segments = segments;
        _nameOnly = nameOnly;

        var literal = 0;
        while (literal < segments.Length && !HasWildcard(segments[literal])) literal++;

        LiteralSegments = literal;

        // The root segment already carries its separator ("C:\"), so joining it with the
        // rest naively would produce "C:\\Windows" - which the filesystem accepts and
        // string comparison does not.
        LiteralPrefix = literal switch
        {
            0 => "",
            _ when segments[0].EndsWith('\\') => segments[0] + string.Join('\\', segments[1..literal]),
            _ => string.Join('\\', segments[..literal]),
        };
    }

    internal static PathGlob Parse(string pattern)
    {
        var expanded = Expand(pattern);
        var segments = expanded
            .Replace('/', '\\')
            .Split('\\', StringSplitOptions.RemoveEmptyEntries);

        // "C:\Windows\**" splits to ["C:", "Windows", "**"]; put the colon form back so
        // the first segment compares equal to the first segment of a real path.
        if (segments.Length > 0 && segments[0].EndsWith(':')) segments[0] += '\\';

        // A pattern with no separator in it - "*.kdbx", "node_modules" - is about a name,
        // not a location: matching it against the whole path would make it useless
        // (README section 12.2, "a rule matches on the name rather than the whole path").
        var nameOnly = expanded.AsSpan().IndexOfAny('\\', '/') < 0;

        return new PathGlob(pattern, segments, nameOnly);
    }

    /// <summary>True when the pattern has no wildcard at all: a plain path.</summary>
    internal bool IsLiteral => LiteralSegments == _segments.Length;

    /// <summary>The pattern's segments after the literal prefix, as one pattern again.</summary>
    internal string Remainder => LiteralSegments >= _segments.Length
        ? ""
        : string.Join('\\', _segments[LiteralSegments..]);

    internal bool Matches(string path)
    {
        var parts = path.Replace('/', '\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length > 0 && parts[0].EndsWith(':')) parts[0] += '\\';

        if (_nameOnly)
            return parts.Length > 0
                && FileSystemName.MatchesSimpleExpression(_segments[0], parts[^1], ignoreCase: true);

        return Matches(_segments, 0, parts, 0);
    }

    /// <summary>
    /// Segment matching with <c>**</c>. Recursive rather than a compiled automaton: the
    /// patterns are a handful of segments long and the recursion depth is bounded by them.
    /// </summary>
    private static bool Matches(string[] pattern, int p, string[] path, int i)
    {
        while (true)
        {
            if (p == pattern.Length) return i == path.Length;

            if (pattern[p] == "**")
            {
                // Trailing ** means "this directory and everything under it".
                if (p + 1 == pattern.Length) return true;

                for (var skip = i; skip <= path.Length; skip++)
                    if (Matches(pattern, p + 1, path, skip)) return true;

                return false;
            }

            if (i == path.Length) return false;

            if (!FileSystemName.MatchesSimpleExpression(pattern[p], path[i], ignoreCase: true))
                return false;

            p++;
            i++;
        }
    }

    private static bool HasWildcard(string segment) =>
        segment.AsSpan().IndexOfAny('*', '?') >= 0;

    /// <summary>
    /// Expands the variables a person actually writes. Through the known-folder API rather
    /// than a hard-coded <c>C:\</c>: the system volume need not be C: (README section 9.3).
    /// </summary>
    internal static string Expand(string pattern)
    {
        if (pattern.StartsWith("~\\", StringComparison.Ordinal) || pattern == "~")
            pattern = Folder(Environment.SpecialFolder.UserProfile) + pattern[1..];

        if (pattern.IndexOf('%') < 0) return pattern;

        return pattern
            .Replace("%WINDIR%", Folder(Environment.SpecialFolder.Windows), StringComparison.OrdinalIgnoreCase)
            .Replace("%SYSTEMROOT%", Folder(Environment.SpecialFolder.Windows), StringComparison.OrdinalIgnoreCase)
            .Replace("%TEMP%", Path.TrimEndingDirectorySeparator(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
            .Replace("%TMP%", Path.TrimEndingDirectorySeparator(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
            .Replace("%LOCALAPPDATA%", Folder(Environment.SpecialFolder.LocalApplicationData), StringComparison.OrdinalIgnoreCase)
            .Replace("%APPDATA%", Folder(Environment.SpecialFolder.ApplicationData), StringComparison.OrdinalIgnoreCase)
            .Replace("%USERPROFILE%", Folder(Environment.SpecialFolder.UserProfile), StringComparison.OrdinalIgnoreCase)
            .Replace("%PROGRAMDATA%", Folder(Environment.SpecialFolder.CommonApplicationData), StringComparison.OrdinalIgnoreCase)
            .Replace("%PROGRAMFILES%", Folder(Environment.SpecialFolder.ProgramFiles), StringComparison.OrdinalIgnoreCase)
            .Replace("%PROGRAMFILES(X86)%", Folder(Environment.SpecialFolder.ProgramFilesX86), StringComparison.OrdinalIgnoreCase)
            .Replace("%PUBLIC%", Folder(Environment.SpecialFolder.CommonDocuments), StringComparison.OrdinalIgnoreCase);
    }

    private static string Folder(Environment.SpecialFolder folder) =>
        Environment.GetFolderPath(folder, Environment.SpecialFolderOption.DoNotVerify);

    public override string ToString() => Text;
}
