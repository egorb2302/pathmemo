using PathMemo.Platform;
using PathMemo.Snapshots;

namespace PathMemo.Scanning;

internal enum ScannerKind
{
    /// <summary>Directory walk. Works anywhere, no elevation, ~40x slower.</summary>
    Walk,

    /// <summary>Raw $MFT read. NTFS + administrator only (README section 4.3).</summary>
    Mft,

    /// <summary>USN journal delta applied to a previous snapshot (README section 4.5).</summary>
    Incremental,
}

[Flags]
internal enum ScanFlags
{
    None = 0,

    /// <summary>Cancelled or aborted: totals are incomplete, not valid for diff or reclaim.</summary>
    Partial = 1 << 0,

    /// <summary>Ran without the capabilities the fast path would have given.</summary>
    Degraded = 1 << 1,

    /// <summary>
    /// Hard links were only resolved for files above a size threshold, because obtaining a
    /// file id without the MFT costs a handle per file (README section 3.2).
    /// </summary>
    PartialHardlinkResolution = 1 << 2,

    /// <summary>Process was elevated.</summary>
    Elevated = 1 << 3,

    /// <summary>Built by applying a USN delta rather than a full traversal.</summary>
    Incremental = 1 << 4,

    /// <summary>Alternate data streams were not counted (walk scanner limitation).</summary>
    NoAdsAccounting = 1 << 5,
}

internal enum ScanErrorKind
{
    Unknown,
    AccessDenied,
    SharingViolation,
    PathTooLong,
    NameInvalid,
    NotFound,
    IoError,
}

internal sealed record ScanError(string Path, ScanErrorKind Kind, int Win32Code, string Message);

internal sealed record ScanRequest
{
    /// <summary>Roots to traverse. Empty means every ready fixed volume.</summary>
    internal IReadOnlyList<string> Roots { get; init; } = [];

    internal ScannerKind? ForceScanner { get; init; }

    /// <summary>0 or negative means auto-detect from the storage medium.</summary>
    internal int Parallelism { get; init; }

    /// <summary>Extra globs excluded for this scan only.</summary>
    internal IReadOnlyList<string> Exclude { get; init; } = [];

    internal string? Note { get; init; }
}

internal sealed record ScanProgress
{
    internal long Entries { get; init; }
    internal long Bytes { get; init; }
    internal long Errors { get; init; }

    /// <summary>Directory currently being enumerated, for the walk scanner's status line.</summary>
    internal string? CurrentPath { get; init; }

    internal TimeSpan Elapsed { get; init; }

    /// <summary>Known only for the MFT scanner, which learns the record count up front.</summary>
    internal double? Fraction { get; init; }
}

internal sealed record ScanResult
{
    internal required ScannerKind Scanner { get; init; }
    internal required ScanFlags Flags { get; init; }
    internal required DateTime StartedUtc { get; init; }
    internal required TimeSpan Duration { get; init; }
    internal required IReadOnlyList<VolumeInfo> Volumes { get; init; }
    internal required NodeStore Tree { get; init; }
    internal required IReadOnlyList<ScanError> Errors { get; init; }
    internal string? Note { get; init; }

    internal int TotalNodes => Tree.Count;

    internal long AllocatedBytes => Tree.Roots.Sum(r => Tree.Allocated[r]);
    internal long LogicalBytes => Tree.Roots.Sum(r => Tree.Logical[r]);
    internal int FileCount => Tree.Roots.Sum(r => Tree.FileCount[r]);
    internal int DirectoryCount => Tree.Count - FileCount;
}

internal interface IScanner
{
    ScannerKind Kind { get; }

    /// <summary>Whether this scanner can handle the volume under the current privileges.</summary>
    (bool Can, string Reason) CanScan(VolumeInfo volume);

    Task<ScanResult> ScanAsync(
        ScanRequest request,
        IReadOnlyList<VolumeInfo> volumes,
        IProgress<ScanProgress>? progress,
        CancellationToken ct);
}
