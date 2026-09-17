using PathMemo.Platform;
using PathMemo.Snapshots;

namespace PathMemo.Audit;

/// <summary>
/// Everything a probe may look at. Probes read; they never change the system
/// (README section 6.3).
/// </summary>
internal sealed class AuditContext
{
    internal required IReadOnlyList<VolumeInfo> Volumes { get; init; }
    internal required bool Elevated { get; init; }

    /// <summary>The newest stored scan, if any. Some probes can only read the tree.</summary>
    internal SnapshotContents? Snapshot { get; init; }
    internal long? SnapshotId { get; init; }

    internal required CancellationToken Cancellation { get; init; }

    internal VolumeInfo? SystemVolume => Volumes.FirstOrDefault(v =>
        WindowsDirectory.StartsWith(v.Root, StringComparison.OrdinalIgnoreCase));

    internal string WindowsDirectory { get; } =
        Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    internal string LocalAppData { get; } =
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    internal string ProgramData { get; } =
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

    /// <summary>
    /// Another fixed volume with the most free space, for "move it there" advice. A page
    /// file or a WSL disk that cannot shrink can still leave a full system drive.
    /// </summary>
    internal VolumeInfo? RoomiestOtherVolume(string volumeLetter) => Volumes
        .Where(v => v.DriveType == DriveType.Fixed
                 && !v.Letter.Equals(volumeLetter, StringComparison.OrdinalIgnoreCase)
                 && v.FreeBytes > 20UL * 1024 * 1024 * 1024)
        .OrderByDescending(v => v.FreeBytes)
        .FirstOrDefault();

    internal static string Letter(string path) =>
        path.Length >= 2 && path[1] == ':' ? path[..2].ToUpperInvariant() : "";
}

/// <summary>
/// One measurement. The only interface in the audit: there are twenty implementations
/// and the runner treats them uniformly (README section 17.2).
/// </summary>
internal interface IAuditProbe
{
    string Id { get; }
    string Title { get; }

    /// <summary>
    /// Returns one finding per thing found - usually one, one per volume for the Recycle
    /// Bin, one per distribution for WSL. Returning nothing means "nothing to report";
    /// a probe that cannot measure returns a finding with a non-Measured status instead.
    /// </summary>
    IEnumerable<AuditFinding> Run(AuditContext context);
}
