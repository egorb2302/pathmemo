using PathMemo.Platform.Native;

namespace PathMemo.Deletion;

/// <summary>
/// Turning a path a user typed into the one name the filesystem agrees on
/// (README section 9.3, step 2).
/// </summary>
/// <remarks>
/// The volume-GUID form - <c>\\?\Volume{...}\Windows\System32</c> - is the only spelling
/// that survives case, trailing slashes, 8.3 names, the <c>\\?\</c> and <c>\\.\</c>
/// prefixes, UNC paths to the local machine, <c>..</c> traversal, the compatibility
/// junctions and mount points. It is produced by the kernel from an open handle, so it
/// describes the object we are holding rather than the string we asked for.
/// </remarks>
internal static class Canonical
{
    /// <summary>Canonical volume-GUID path of an existing file or directory, or null.</summary>
    internal static string? TryOf(string path)
    {
        using var handle = FileApi.TryOpen(path, FileApi.FileReadAttributes | FileApi.Synchronize, out _);
        return handle is null ? null : FileApi.FinalPath(handle);
    }

    /// <summary>
    /// Ordinal, case-insensitive comparison that respects segment boundaries, so
    /// <c>C:\Windows</c> does not contain <c>C:\WindowsApps</c>.
    /// </summary>
    internal static bool IsSameOrUnder(string path, string root)
    {
        if (root.Length == 0) return false;

        var trimmedRoot = TrimSlash(root);
        var trimmedPath = TrimSlash(path);

        if (trimmedPath.Length < trimmedRoot.Length) return false;
        if (!trimmedPath.AsSpan(0, trimmedRoot.Length).Equals(trimmedRoot, StringComparison.OrdinalIgnoreCase))
            return false;

        return trimmedPath.Length == trimmedRoot.Length || trimmedPath[trimmedRoot.Length] == '\\';
    }

    /// <summary>True when <paramref name="root"/> lies inside <paramref name="path"/>.</summary>
    internal static bool Contains(string path, string root) => IsSameOrUnder(root, path);

    /// <summary>Number of segments below the volume. <c>\\?\Volume{..}\</c> is 0.</summary>
    internal static int DepthUnderVolume(string canonical)
    {
        var body = AfterVolume(canonical);
        return body.Length == 0 ? 0 : body.Count(c => c == '\\') + 1;
    }

    /// <summary>The <c>\\?\Volume{GUID}\</c> prefix of a canonical path, or "".</summary>
    internal static string VolumePrefix(string canonical)
    {
        if (!canonical.StartsWith(@"\\?\Volume{", StringComparison.OrdinalIgnoreCase)) return "";

        var end = canonical.IndexOf('}', 11);
        return end < 0 ? "" : canonical[..(end + 2)];   // include the '}' and the '\'
    }

    private static string AfterVolume(string canonical)
    {
        var prefix = VolumePrefix(canonical);
        return prefix.Length == 0 ? TrimSlash(canonical) : TrimSlash(canonical[prefix.Length..]);
    }

    internal static string TrimSlash(string path) =>
        path.Length > 1 && path[^1] == '\\' ? path[..^1] : path;

    /// <summary>
    /// The readable form of a canonical path: the same object named through its drive
    /// letter, for messages and the journal. Falls back to the GUID form on a volume with
    /// no letter, which is honest rather than pretty.
    /// </summary>
    internal static string Display(Microsoft.Win32.SafeHandles.SafeFileHandle handle, string canonical)
    {
        var dos = FileApi.FinalPath(handle, FileApi.VolumeNameDos);
        if (dos is null) return canonical;

        // GetFinalPathNameByHandle always answers with the \\?\ prefix; a path that fits
        // MAX_PATH reads better without it, and nothing downstream needs it.
        return dos.StartsWith(@"\\?\", StringComparison.Ordinal) && dos.Length < 260 && dos[5] == ':'
            ? dos[4..]
            : dos;
    }
}
