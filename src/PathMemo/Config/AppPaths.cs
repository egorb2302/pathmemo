using PathMemo.Platform;

namespace PathMemo.Config;

internal static class AppPaths
{
    private static string _dataDirectory = DefaultDataDirectory();

    /// <summary>
    /// Root of everything pathmemo stores. Never taken from user configuration while
    /// elevated - that would be an arbitrary-file-write primitive running as admin
    /// (README section 12.1, threat T7).
    /// </summary>
    internal static string DataDirectory => _dataDirectory;

    internal static string SnapshotsDirectory => Path.Combine(DataDirectory, "snapshots");
    internal static string QuarantineDirectory => Path.Combine(DataDirectory, "quarantine");
    internal static string LogsDirectory => Path.Combine(DataDirectory, "logs");
    internal static string ExportsDirectory => Path.Combine(DataDirectory, "exports");
    internal static string DatabasePath => Path.Combine(DataDirectory, "pathmemo.db");
    internal static string ConfigPath => Path.Combine(DataDirectory, "config.json");

    /// <summary>Marker used to detect an unclean shutdown (README threat T14).</summary>
    internal static string CleanShutdownMarker => Path.Combine(DataDirectory, ".clean");

    /// <summary>Whether <c>--data-dir</c> moved the store away from its default place.</summary>
    internal static bool IsRedirected { get; private set; }

    /// <summary>
    /// Implements <c>--data-dir</c>. Refused while elevated: an administrator process
    /// that writes wherever a command line points it is a privilege-escalation gadget,
    /// not a convenience (README section 12.1).
    /// </summary>
    internal static void Redirect(string path)
    {
        if (Elevation.IsElevated)
        {
            Console.Error.WriteLine("pathmemo: --data-dir is ignored while running as administrator (README section 12.1)");
            return;
        }

        var full = Path.GetFullPath(path);
        _dataDirectory = full;
        IsRedirected = true;
    }

    private static string DefaultDataDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData,
                                  Environment.SpecialFolderOption.DoNotVerify),
        "pathmemo");
}
