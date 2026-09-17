using PathMemo.Scanning;

namespace PathMemo.Storage;

/// <summary>Status of a scan row. Text in the database, for a human reading the file.</summary>
internal static class ScanStatus
{
    internal const string Running = "running";
    internal const string Completed = "completed";
    internal const string Cancelled = "cancelled";
    internal const string Failed = "failed";
}

/// <summary>
/// One row of <c>scans</c> (README section 11).
/// </summary>
/// <remarks>
/// About 200 bytes, and it outlives its snapshot on purpose: the graph of "how full was
/// this disk" has to reach back years, long after retention dropped the trees themselves.
/// A row whose <see cref="SnapshotPath"/> is null is still a real data point.
/// </remarks>
internal sealed record ScanRow
{
    internal required long Id { get; init; }
    internal required DateTime StartedUtc { get; init; }
    internal DateTime? FinishedUtc { get; init; }
    internal required string Status { get; init; }
    internal required ScannerKind Scanner { get; init; }
    internal required ScanFlags Flags { get; init; }
    internal IReadOnlyList<string> Roots { get; init; } = [];
    internal int TotalFiles { get; init; }
    internal int TotalDirectories { get; init; }
    internal long AllocatedBytes { get; init; }
    internal long LogicalBytes { get; init; }
    internal long? DurationMs { get; init; }
    internal int ErrorCount { get; init; }
    internal string ToolVersion { get; init; } = "";
    internal string? SnapshotPath { get; init; }
    internal long? SnapshotBytes { get; init; }
    internal string? Note { get; init; }

    /// <summary>Whether the tree can still be opened, or only the numbers remain.</summary>
    internal bool SnapshotAvailable => SnapshotPath is not null;
}

/// <summary>One row of <c>scan_volumes</c>: the state of a volume at scan time.</summary>
internal sealed record ScanVolumeRow
{
    internal required string Letter { get; init; }
    internal string? Label { get; init; }
    internal string? FileSystem { get; init; }
    internal long VolumeSerial { get; init; }
    internal string? VolumeGuid { get; init; }
    internal long ClusterBytes { get; init; }
    internal long TotalBytes { get; init; }
    internal long FreeBytes { get; init; }
    internal long ScannedBytes { get; init; }
    internal long? MetadataBytes { get; init; }
    internal long? UnaccountedBytes { get; init; }
    internal long? UsnJournalId { get; init; }
    internal long? NextUsn { get; init; }

    internal long UsedBytes => TotalBytes - FreeBytes;
}
