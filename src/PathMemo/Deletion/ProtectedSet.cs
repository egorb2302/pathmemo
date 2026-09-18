using PathMemo.Config;

namespace PathMemo.Deletion;

/// <summary>What the guard is being asked to do, because the rules differ slightly.</summary>
internal enum GuardOperation
{
    /// <summary>An ordinary deletion. Every rule applies.</summary>
    Delete,

    /// <summary>Emptying our own quarantine: the data directory stops being off limits.</summary>
    Purge,

    /// <summary>Emptying the Recycle Bin, the one operation allowed to touch it.</summary>
    EmptyRecycleBin,
}

internal readonly record struct ProtectionVerdict(bool IsProtected, string Reason)
{
    internal static readonly ProtectionVerdict Allowed = new(false, "");
}

/// <summary>
/// The set of paths nothing may delete (README section 9.3, step 3).
/// </summary>
/// <remarks>
/// <para>
/// Built once, from <see cref="Environment.SpecialFolder"/> - which is
/// <c>SHGetKnownFolderPath</c> underneath - and canonicalised through the filesystem, so
/// the comparison happens between two names the kernel produced rather than between two
/// strings a human typed. A hard-coded <c>C:\Windows</c> would be wrong twice over: the
/// system volume need not be C:, and a literal string is bypassable a dozen ways.
/// </para>
/// <para>
/// <see cref="Check"/> is a pure function over canonical strings, which is what makes the
/// twelve bypasses of README section 9.3 testable without a filesystem (README section 22.1).
/// </para>
/// </remarks>
internal sealed class ProtectedSet
{
    /// <summary>A rule as it is matched: canonical prefix when it resolved, glob always.</summary>
    private sealed record Rule(PathGlob Glob, string? CanonicalPrefix, PathGlob? Remainder)
    {
        internal bool Matches(string canonical, string display)
        {
            if (CanonicalPrefix is { } prefix && Canonical.IsSameOrUnder(canonical, prefix))
            {
                if (Remainder is null) return true;

                var rest = Canonical.TrimSlash(canonical)[Canonical.TrimSlash(prefix).Length..].TrimStart('\\');
                if (Remainder.Matches(rest)) return true;
            }

            return display.Length > 0 && Glob.Matches(display);
        }

        /// <summary>
        /// Whether the path is the very directory the rule is anchored on.
        /// </summary>
        /// <remarks>
        /// A whitelist opens a directory's <b>contents</b>, never the directory:
        /// <c>%WINDIR%\Temp\**</c> must not become permission to delete <c>%WINDIR%\Temp</c>,
        /// which Windows expects to exist. Expressed here rather than in the glob syntax so
        /// that the pattern language keeps one meaning everywhere (README section 12.2).
        /// </remarks>
        internal bool IsAnchor(string canonical, string display)
        {
            if (CanonicalPrefix is { } prefix
                && Canonical.TrimSlash(canonical).Equals(Canonical.TrimSlash(prefix), StringComparison.OrdinalIgnoreCase))
                return true;

            return Glob.LiteralPrefix.Length > 0
                && Canonical.TrimSlash(display).Equals(Canonical.TrimSlash(Glob.LiteralPrefix),
                    StringComparison.OrdinalIgnoreCase);
        }

        internal static Rule Parse(string pattern)
        {
            var glob = PathGlob.Parse(pattern);
            var prefix = glob.LiteralPrefix.Length == 0 ? null : Canonical.TryOf(glob.LiteralPrefix);
            var remainder = glob.IsLiteral || glob.Remainder == "**" ? null : PathGlob.Parse(glob.Remainder);

            return new Rule(glob, prefix, remainder);
        }
    }

    /// <summary>
    /// A protected directory, and how far the protection reaches.
    /// </summary>
    /// <param name="Sealed">
    /// True for the operating system's own directories, where nothing inside may be deleted
    /// and <c>allowInsideProtected</c> is the only way in. False for the structural ones -
    /// the profile, <c>AppData</c>, <c>C:\Users</c> - where the directory itself is
    /// protected and what is inside it is ordinary user data. README section 9.3 lists both
    /// kinds together; treating them alike would mean either that <c>C:\Users</c> can be
    /// deleted or that a 44 GB disk image in <c>AppData\Local</c> cannot, and a disk-space
    /// tool that cannot delete a cache has no reason to exist.
    /// </param>
    private readonly record struct Entry(string Path, string Label, bool Sealed);

    private readonly List<Entry> _folders;
    private readonly List<Rule> _keep;
    private readonly List<Rule> _allowInside;
    private readonly string? _dataDirectory;
    private readonly IReadOnlyList<string> _quarantineRoots;

    internal ProtectedSet(
        IEnumerable<(string Path, string Label, bool Sealed)> folders,
        IEnumerable<string>? keep = null,
        IEnumerable<string>? allowInside = null,
        string? dataDirectory = null,
        IReadOnlyList<string>? quarantineRoots = null)
    {
        // Longest first, so a path inside System32 is refused as the system directory
        // rather than as the Windows directory that happens to contain it. Both answers
        // are correct; only one is useful to read.
        _folders = [.. folders.Where(f => !string.IsNullOrEmpty(f.Path))
                              .OrderByDescending(f => f.Path.Length)
                              .Select(f => new Entry(f.Path, f.Label, f.Sealed))];

        _keep = [.. (keep ?? []).Select(Rule.Parse)];
        _allowInside = [.. (allowInside ?? []).Select(Rule.Parse)];
        _dataDirectory = dataDirectory;
        _quarantineRoots = quarantineRoots ?? [];
    }

    /// <summary>Builds the real set for this machine.</summary>
    internal static ProtectedSet Build(ProtectSettings? settings = null)
    {
        settings ??= AppConfig.Current.Protect;

        var folders = new List<(string, string, bool)>();

        void Add(Environment.SpecialFolder folder, string label, bool sealedFolder)
        {
            var path = Environment.GetFolderPath(folder, Environment.SpecialFolderOption.DoNotVerify);
            if (path.Length == 0) return;

            var canonical = Canonical.TryOf(path);
            if (canonical is not null) folders.Add((canonical, label, sealedFolder));
        }

        void AddPath(string? path, string label, bool sealedFolder)
        {
            if (string.IsNullOrEmpty(path)) return;

            var canonical = Canonical.TryOf(path);
            if (canonical is not null) folders.Add((canonical, label, sealedFolder));
        }

        // Sealed: the operating system's own directories.
        Add(Environment.SpecialFolder.Windows, "the Windows directory", true);
        Add(Environment.SpecialFolder.System, "the system directory", true);
        Add(Environment.SpecialFolder.SystemX86, "the 32-bit system directory", true);
        Add(Environment.SpecialFolder.ProgramFiles, "Program Files", true);
        Add(Environment.SpecialFolder.ProgramFilesX86, "Program Files (x86)", true);
        Add(Environment.SpecialFolder.CommonProgramFiles, "the common Program Files directory", true);
        Add(Environment.SpecialFolder.CommonApplicationData, "ProgramData", true);
        Add(Environment.SpecialFolder.Fonts, "the font directory", true);
        Add(Environment.SpecialFolder.Startup, "the Startup folder", true);
        Add(Environment.SpecialFolder.StartMenu, "the Start menu", true);
        Add(Environment.SpecialFolder.CommonStartMenu, "the common Start menu", true);
        Add(Environment.SpecialFolder.CommonStartup, "the common Startup folder", true);

        // Structural: the directory, not its contents.
        Add(Environment.SpecialFolder.UserProfile, "the user profile", false);
        Add(Environment.SpecialFolder.ApplicationData, "the roaming application data directory", false);
        Add(Environment.SpecialFolder.LocalApplicationData, "the local application data directory", false);

        // FOLDERID_UserProfiles ("C:\Users") has no SpecialFolder member; it is the parent
        // of this user's profile, and deleting it takes every account with it.
        AddPath(Path.GetDirectoryName(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile,
                                      Environment.SpecialFolderOption.DoNotVerify)),
            "the user profiles directory", false);

        AddPath(Environment.GetEnvironmentVariable("PUBLIC"), "the Public profile", false);

        var dataDirectory = Canonical.TryOf(AppPaths.DataDirectory);

        return new ProtectedSet(
            folders,
            settings.Keep,
            settings.AllowInsideProtected,
            dataDirectory,
            QuarantineRoots(dataDirectory));
    }

    /// <summary>
    /// Where quarantines live, whether or not any exist yet.
    /// </summary>
    /// <remarks>
    /// Composed from the canonical name of the volume rather than resolved through the
    /// filesystem: the first quarantine is created <b>during</b> the operation that needs
    /// this list, so a set built by asking which directories exist would not contain it -
    /// and purging it would then be refused for being inside the data directory.
    /// </remarks>
    private static List<string> QuarantineRoots(string? dataDirectory)
    {
        var roots = new List<string>();

        void AddBoth(string? canonicalParent, string name, string realPath)
        {
            if (canonicalParent is not null)
                roots.Add(Canonical.TrimSlash(canonicalParent) + '\\' + name);

            // Also the resolved form, for the case where a reparse point sits in between
            // and the composed name is not what the kernel would answer.
            var resolved = Canonical.TryOf(realPath);
            if (resolved is not null && !roots.Contains(resolved, StringComparer.OrdinalIgnoreCase))
                roots.Add(resolved);
        }

        AddBoth(dataDirectory, Path.GetFileName(AppPaths.QuarantineDirectory), AppPaths.QuarantineDirectory);

        foreach (var volume in Platform.VolumeInfo.Enumerate(fixedOnly: true))
        {
            AddBoth(Canonical.TryOf(volume.Root), Quarantine.VolumeStoreName,
                Path.Combine(volume.Root, Quarantine.VolumeStoreName));
        }

        return roots;
    }

    /// <summary>
    /// The whole of README section 9.3, step 3, as one decision.
    /// </summary>
    /// <param name="canonical">Volume-GUID path from <see cref="Canonical"/>.</param>
    /// <param name="display">The same object as a drive-letter path, for the glob rules.</param>
    /// <param name="operation">Purge and "empty the bin" relax exactly one rule each.</param>
    internal ProtectionVerdict Check(string canonical, string display, GuardOperation operation = GuardOperation.Delete)
    {
        var depth = Canonical.DepthUnderVolume(canonical);
        if (depth == 0) return new ProtectionVerdict(true, "a volume root");

        // Purging our own quarantine is the one operation allowed inside the data
        // directory, and it is allowed before any other rule gets to object: the store may
        // sit under %LOCALAPPDATA%, which is protected for other reasons.
        if (operation == GuardOperation.Purge && IsInQuarantine(canonical))
            return ProtectionVerdict.Allowed;

        foreach (var segment in Segments(canonical))
        {
            if (segment.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase))
                return new ProtectionVerdict(true, "inside System Volume Information");

            if (segment.Equals("$Recycle.Bin", StringComparison.OrdinalIgnoreCase)
                && operation != GuardOperation.EmptyRecycleBin)
                return new ProtectionVerdict(true, "inside the Recycle Bin - use 'pathmemo purge --bin'");
        }

        // Our own store: nothing deletes the database out from under a running scan.
        if (_dataDirectory is { } data && Canonical.IsSameOrUnder(canonical, data))
            return new ProtectionVerdict(true, "pathmemo's own data directory");

        if (IsInQuarantine(canonical))
            return new ProtectionVerdict(true, "a pathmemo quarantine - use 'pathmemo purge'");

        foreach (var rule in _keep)
            if (rule.Matches(canonical, display))
                return new ProtectionVerdict(true, $"matched the keep rule '{rule.Glob.Text}'");

        foreach (var entry in _folders)
        {
            // Being one of these directories is refused whichever kind it is.
            if (Canonical.TrimSlash(canonical).Equals(Canonical.TrimSlash(entry.Path), StringComparison.OrdinalIgnoreCase))
                return new ProtectionVerdict(true, entry.Label);

            if (!entry.Sealed || !Canonical.IsSameOrUnder(canonical, entry.Path)) continue;

            // Inside a sealed folder: refused, unless a rule opens that door on purpose.
            if (IsAllowedInside(canonical, display)) continue;

            return new ProtectionVerdict(true, entry.Label);
        }

        // Two passes, because "this is the Windows directory" and "this contains the system
        // directory" are both true of C:\Windows and only the first one tells the user
        // anything. Being the thing wins over containing it.
        foreach (var entry in _folders)
        {
            if (Canonical.Contains(canonical, entry.Path))
                return new ProtectionVerdict(true, $"it contains {entry.Label}");
        }

        // Last, so that the named folders answer with their own names: everything directly
        // under a volume root. A tool that can be talked into deleting C:\Windows has
        // already lost; one that refuses D:\anything never gets the chance. What is inside
        // such a directory stays deletable.
        if (depth <= 1)
            return new ProtectionVerdict(true,
                "a top-level directory on the volume - delete what is inside it instead");

        return ProtectionVerdict.Allowed;
    }

    internal bool IsAllowedInside(string canonical, string display)
    {
        foreach (var rule in _allowInside)
            if (rule.Matches(canonical, display) && !rule.IsAnchor(canonical, display))
                return true;

        return false;
    }

    private bool IsInQuarantine(string canonical) =>
        _quarantineRoots.Any(root => Canonical.IsSameOrUnder(canonical, root));

    private static IEnumerable<string> Segments(string canonical)
    {
        var prefix = Canonical.VolumePrefix(canonical);
        var body = prefix.Length == 0 ? canonical : canonical[prefix.Length..];

        return body.Split('\\', StringSplitOptions.RemoveEmptyEntries);
    }
}
