namespace PathMemo.Config;

internal static class AppPaths
{
    /// <summary>
    /// Root of everything pathmemo stores. Never taken from user configuration while
    /// elevated - that would be an arbitrary-file-write primitive running as admin
    /// (README section 12.1, threat T7).
    /// </summary>
    internal static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData,
                                  Environment.SpecialFolderOption.DoNotVerify),
        "pathmemo");

    internal static string SnapshotsDirectory => Path.Combine(DataDirectory, "snapshots");
    internal static string QuarantineDirectory => Path.Combine(DataDirectory, "quarantine");
    internal static string LogsDirectory => Path.Combine(DataDirectory, "logs");
    internal static string ExportsDirectory => Path.Combine(DataDirectory, "exports");
    internal static string DatabasePath => Path.Combine(DataDirectory, "pathmemo.db");
    internal static string ConfigPath => Path.Combine(DataDirectory, "config.json");

    /// <summary>Marker used to detect an unclean shutdown (README threat T14).</summary>
    internal static string CleanShutdownMarker => Path.Combine(DataDirectory, ".clean");
}
