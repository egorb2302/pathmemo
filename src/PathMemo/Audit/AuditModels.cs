namespace PathMemo.Audit;

/// <summary>What breaks if the remedy is applied (README section 7.1, axis 1).</summary>
internal enum Risk
{
    /// <summary>Nothing. Windows or the application recreates it on demand.</summary>
    Safe,

    /// <summary>A capability is lost: rolling back an update, hibernating, restoring a file.</summary>
    Caution,

    /// <summary>Can break the system or an application. Manual verification required.</summary>
    Danger,
}

/// <summary>What getting it back would cost (README section 7.1, axis 2).</summary>
internal enum Recoverability
{
    Instant,
    Redownload,
    Rebuild,
    Irreversible,
}

internal enum RemedyKind
{
    /// <summary>A command line, shown and copied, never run by the audit itself.</summary>
    RunCommand,

    /// <summary>Paths that could be deleted. Deletion itself arrives with P6.</summary>
    DeletePaths,

    /// <summary>A Settings page or control panel applet to open.</summary>
    OpenSettings,

    /// <summary>Instructions for a human.</summary>
    Manual,
}

/// <summary>Whether a probe managed to measure anything, and if not, why.</summary>
internal enum FindingStatus
{
    /// <summary>Numbers are real.</summary>
    Measured,

    /// <summary>The thing exists, but its size could not be determined. Never reported as 0.</summary>
    Unknown,

    /// <summary>Measuring needs an elevated process (VSS, DISM, some system directories).</summary>
    NeedsElevation,

    /// <summary>The feature is not present on this machine (no WSL, no OneDrive, no Windows.old).</summary>
    NotApplicable,

    /// <summary>The probe needs a stored scan and there is none.</summary>
    NoSnapshot,

    /// <summary>The probe threw or timed out. The message says what happened.</summary>
    Error,
}

internal sealed record Remedy(
    RemedyKind Kind,
    string Display,
    bool NeedsElevation = false,
    bool NeedsReboot = false,
    string? Caveat = null);

/// <summary>
/// One thing the audit found (README section 6.1).
/// </summary>
/// <remarks>
/// <see cref="ReclaimableBytes"/> is what actually comes back after the remedy, which is
/// not the same as <see cref="UsedBytes"/>: a 44 GB WSL disk with 13 GB used inside
/// reclaims 31 GB; a 12 GB page file reclaims nothing unless it is moved. Either may be
/// null when the probe could not tell, and null is displayed as such - never as zero
/// (README section 6.3).
/// </remarks>
internal sealed record AuditFinding
{
    internal required string Id { get; init; }
    internal required string Title { get; init; }
    internal required FindingStatus Status { get; init; }

    /// <summary>"C:" or "" for machine-wide findings.</summary>
    internal string Volume { get; init; } = "";

    internal long? UsedBytes { get; init; }
    internal long? ReclaimableBytes { get; init; }

    internal Risk Risk { get; init; } = Risk.Safe;
    internal Recoverability Recoverability { get; init; } = Recoverability.Instant;

    /// <summary>One to three English sentences a user can act on.</summary>
    internal required string Explanation { get; init; }

    internal IReadOnlyList<Remedy> Remedies { get; init; } = [];

    /// <summary>Concrete paths behind the number, for the details view.</summary>
    internal IReadOnlyList<string> Paths { get; init; } = [];

    /// <summary>Set when a status other than Measured needs explaining.</summary>
    internal string? Note { get; init; }

    internal TimeSpan Elapsed { get; init; }

    internal static AuditFinding NotApplicable(string id, string title, string why) => new()
    {
        Id = id, Title = title, Status = FindingStatus.NotApplicable, Explanation = why,
    };

    internal static AuditFinding NeedsElevation(string id, string title, string what) => new()
    {
        Id = id, Title = title, Status = FindingStatus.NeedsElevation,
        Explanation = what,
        Note = "run as administrator to measure",
    };
}

internal sealed record AuditReport
{
    internal required DateTime StartedUtc { get; init; }
    internal required TimeSpan Duration { get; init; }
    internal required bool Elevated { get; init; }
    internal required long? SnapshotId { get; init; }
    internal required IReadOnlyList<AuditFinding> Findings { get; init; }

    internal long ReclaimableTotal => Findings
        .Where(f => f.Status == FindingStatus.Measured)
        .Sum(f => f.ReclaimableBytes ?? 0);
}
