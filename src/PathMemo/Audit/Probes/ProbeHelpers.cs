using System.Globalization;
using Microsoft.Win32;
using PathMemo.Cli.Output;
using PathMemo.Platform.Native;

namespace PathMemo.Audit.Probes;

/// <summary>Bits shared by the probes: sizing, wording, registry and path helpers.</summary>
internal static class ProbeHelpers
{
    internal static string Size(long bytes) => SizeFormat.Bytes(bytes);

    /// <summary>Sizes several directories as one figure, skipping duplicates and absentees.</summary>
    internal static (Measured Total, List<string> Present) MeasureAll(
        IEnumerable<string> directories, CancellationToken ct)
    {
        var total = Measured.Empty;
        var present = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var directory in directories)
        {
            var canonical = LongPath(directory);
            if (!seen.Add(canonical)) continue;
            if (!Directory.Exists(canonical)) continue;

            present.Add(canonical);
            total = total.Add(DirectoryMeasure.Directory_(canonical, ct));
        }

        return (total, present);
    }

    /// <summary>Expands 8.3 path segments; returns the input when Windows cannot.</summary>
    internal static string LongPath(string path)
    {
        if (!path.Contains('~')) return Path.TrimEndingDirectorySeparator(path);

        Span<char> buffer = stackalloc char[1024];
        var length = Kernel32Extra.GetLongPathName(path, buffer, (uint)buffer.Length);

        return length > 0 && length < buffer.Length
            ? Path.TrimEndingDirectorySeparator(new string(buffer[..(int)length]))
            : Path.TrimEndingDirectorySeparator(path);
    }

    /// <summary>The note attached to a directory total some of which could not be read.</summary>
    internal static string? IncompleteNote(Measured measured, bool elevated) =>
        !measured.Incomplete ? null
        : measured.Partial ? "measuring stopped early; the figure is a lower bound"
        : elevated ? $"{Directories(measured.Errors)} could not be read; the figure is a lower bound"
        : $"{Directories(measured.Errors)} could not be read without administrator rights; the figure is a lower bound";

    private static string Directories(int count) =>
        count == 1 ? "1 directory" : count.ToString(CultureInfo.InvariantCulture) + " directories";

    internal static string Files(int count) =>
        count == 1 ? "1 file" : count.ToString("N0", CultureInfo.InvariantCulture) + " files";

    internal static string? RegistryString(RegistryKey hive, string subKey, string value)
    {
        try
        {
            using var key = hive.OpenSubKey(subKey);
            return key?.GetValue(value) as string;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    internal static int? RegistryInt(RegistryKey hive, string subKey, string value)
    {
        try
        {
            using var key = hive.OpenSubKey(subKey);
            return key?.GetValue(value) is int i ? i : null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    internal static IEnumerable<string> RegistrySubKeys(RegistryKey hive, string subKey)
    {
        RegistryKey? key = null;
        try
        {
            key = hive.OpenSubKey(subKey);
            if (key is null) return [];
            return key.GetSubKeyNames();
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return [];
        }
        finally
        {
            key?.Dispose();
        }
    }

    /// <summary>Strips the <c>\\?\</c> prefix the registry sometimes stores paths with.</summary>
    internal static string Unprefixed(string path) =>
        path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;

    internal static Remedy Elevated(string command, string? caveat = null, bool reboot = false) =>
        new(RemedyKind.RunCommand, command, NeedsElevation: true, NeedsReboot: reboot, Caveat: caveat);

    internal static Remedy Command(string command, string? caveat = null) =>
        new(RemedyKind.RunCommand, command, Caveat: caveat);

    internal static Remedy Manual(string text, string? caveat = null) =>
        new(RemedyKind.Manual, text, Caveat: caveat);

    internal static Remedy Settings(string text) => new(RemedyKind.OpenSettings, text);

    internal static Remedy Delete(string what, string? caveat = null) =>
        new(RemedyKind.DeletePaths, what, Caveat: caveat);
}
