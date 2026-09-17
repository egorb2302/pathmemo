using PathMemo.Platform.Native;

namespace PathMemo.Platform;

/// <summary>
/// One mounted volume, with everything needed to decide how to scan it
/// and to reconcile scan totals against reality (README sections 3.5, 4.2).
/// </summary>
internal sealed record VolumeInfo(
    string Root,            // "C:\"
    string Letter,          // "C:"
    string? Label,
    string FileSystem,      // "NTFS", "exFAT", "FAT32", "ReFS", ...
    uint Serial,
    string? VolumeGuidPath, // "\?\Volume{...}\"
    int ClusterBytes,
    ulong TotalBytes,
    ulong FreeBytes,
    DriveType DriveType)
{
    internal ulong UsedBytes => TotalBytes - FreeBytes;

    internal double UsedPercent => TotalBytes == 0 ? 0 : UsedBytes * 100.0 / TotalBytes;

    internal bool IsNtfs => FileSystem.Equals("NTFS", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether the fast MFT scanner can run on this volume right now, and why not.
    /// </summary>
    internal (bool Available, string Reason) MftScanAvailability =>
        !IsNtfs                 ? (false, $"{FileSystem} is not NTFS")
        : !Elevation.IsElevated ? (false, "requires administrator rights")
        : DriveType is not (DriveType.Fixed or DriveType.Removable)
                                ? (false, $"{DriveType} volumes are not supported")
        : (true, "ready");

    internal static IReadOnlyList<VolumeInfo> Enumerate(bool fixedOnly = false)
    {
        var result = new List<VolumeInfo>();

        foreach (var drive in DriveInfo.GetDrives())
        {
            if (fixedOnly && drive.DriveType != DriveType.Fixed) continue;

            // Not IsReady: an empty optical drive or a disconnected network share throws
            // on every property. Skipping is correct; we cannot scan what is not mounted.
            if (!drive.IsReady) continue;

            var root = drive.RootDirectory.FullName;   // "C:\"
            var letter = root.TrimEnd('\', '/');       // "C:"

            ReadVolumeStrings(root, out var strings, out var serial);
            var (total, free) = ReadSpace(root);

            result.Add(new VolumeInfo(
                Root: root,
                Letter: letter,
                Label: strings.Label,
                FileSystem: strings.FileSystem ?? "unknown",
                Serial: serial,
                VolumeGuidPath: ReadVolumeGuid(root),
                ClusterBytes: ReadClusterBytes(root),
                TotalBytes: total,
                FreeBytes: free,
                DriveType: drive.DriveType));
        }

        return result;
    }

    private readonly record struct VolumeStrings(string? Label, string? FileSystem);

    private static void ReadVolumeStrings(string root, out VolumeStrings strings, out uint serial)
    {
        Span<char> label = stackalloc char[261];
        Span<char> fs = stackalloc char[261];

        if (Kernel32.GetVolumeInformation(root, label, label.Length, out serial, out _, out _, fs, fs.Length))
        {
            strings = new VolumeStrings(ToStringZ(label), ToStringZ(fs));
            return;
        }

        strings = new VolumeStrings(null, null);
        serial = 0;
    }

    private static string? ReadVolumeGuid(string root)
    {
        Span<char> buf = stackalloc char[64];
        return Kernel32.GetVolumeNameForVolumeMountPoint(root, buf, buf.Length)
            ? ToStringZ(buf)
            : null;
    }

    private static int ReadClusterBytes(string root) =>
        Kernel32.GetDiskFreeSpace(root, out var spc, out var bps, out _, out _)
            ? checked((int)(spc * bps))
            : 0;

    private static (ulong total, ulong free) ReadSpace(string root) =>
        Kernel32.GetDiskFreeSpaceEx(root, out _, out var total, out var free)
            ? (total, free)
            : (0UL, 0UL);

    private static string? ToStringZ(Span<char> buffer)
    {
        var end = buffer.IndexOf('\0');
        var slice = end >= 0 ? buffer[..end] : buffer;
        return slice.IsEmpty ? null : new string(slice);
    }
}
