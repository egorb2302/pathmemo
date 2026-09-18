using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace PathMemo.Deletion;

/// <summary>How an item is removed (README section 9.2).</summary>
internal enum DeleteMode
{
    /// <summary>Shell Recycle Bin. Frees nothing now; Explorer can put it back.</summary>
    Recycle,

    /// <summary>Renamed aside on the same volume. Frees nothing until <c>purge</c>.</summary>
    Quarantine,

    /// <summary>Unlinked. Frees space now, and nothing brings it back.</summary>
    Permanent,
}

internal static class DeleteModes
{
    internal static string Name(DeleteMode mode) => mode switch
    {
        DeleteMode.Recycle => "recycle",
        DeleteMode.Permanent => "permanent",
        _ => "quarantine",
    };

    internal static bool TryParse(string text, out DeleteMode mode)
    {
        switch (text.Trim().ToLowerInvariant())
        {
            case "recycle" or "bin" or "trash": mode = DeleteMode.Recycle; return true;
            case "quarantine": mode = DeleteMode.Quarantine; return true;
            case "permanent" or "perm": mode = DeleteMode.Permanent; return true;
            default: mode = DeleteMode.Quarantine; return false;
        }
    }
}

/// <summary>Which part of the tool asked for the deletion. Stored in the journal.</summary>
internal enum DeleteSource
{
    Cli,
    Tree,
    Reclaim,
    Dupes,
    Audit,
}

/// <summary>One thing the plan intends to delete, after the guard has approved it.</summary>
/// <remarks>
/// <see cref="CanonicalPath"/> is the volume-GUID form the guard produced, and it is what
/// every later check compares against - not <see cref="RequestedPath"/>, which is whatever
/// the user typed and may be any of the twelve spellings in README section 9.3.
/// </remarks>
internal sealed record PlanItem
{
    internal required string RequestedPath { get; init; }
    internal required string DisplayPath { get; init; }
    internal required string CanonicalPath { get; init; }
    internal required string VolumeRoot { get; init; }
    internal required bool IsDirectory { get; init; }
    internal required bool IsReparsePoint { get; init; }

    /// <summary>Bytes on disk, the number that comes back when this is gone.</summary>
    internal required long Bytes { get; init; }

    internal long LogicalBytes { get; init; }
    internal int FileCount { get; init; } = 1;
    internal DateTime LastWriteUtc { get; init; }

    /// <summary>Set when measuring a directory hit unreadable subtrees: the size is a floor.</summary>
    internal int MeasureErrors { get; init; }
}

/// <summary>A path the plan will not touch, and the sentence that says why.</summary>
internal sealed record Refusal(string Path, string Reason);

/// <summary>
/// What would happen, computed before anything is changed. A dry run prints exactly this
/// and stops (README section 13.4).
/// </summary>
internal sealed record DeletePlan
{
    internal required IReadOnlyList<PlanItem> Items { get; init; }
    internal required IReadOnlyList<Refusal> Refusals { get; init; }
    internal required DeleteMode Mode { get; init; }

    /// <summary>Set when the mode is not the one asked for, e.g. too big for the bin.</summary>
    internal string? ModeReason { get; init; }

    internal DeleteSource Source { get; init; } = DeleteSource.Cli;
    internal long? ScanId { get; init; }
    internal string? Reason { get; init; }

    internal long TotalBytes => Items.Sum(i => i.Bytes);
    internal int TotalFiles => Items.Sum(i => i.FileCount);
    internal bool IsEmpty => Items.Count == 0;

    /// <summary>Volumes the operation touches, in the order they appear.</summary>
    internal IReadOnlyList<string> Volumes =>
        Items.Select(i => i.VolumeRoot).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>
    /// Bytes this frees the moment it runs. Quarantine and the Recycle Bin free nothing:
    /// both leave the data on the same volume (README sections 9.1, 9.2).
    /// </summary>
    internal long FreesNow => Mode == DeleteMode.Permanent ? TotalBytes : 0;

    internal string Token => ConfirmToken.For(this);
}

internal enum ItemResult
{
    Ok,
    Skipped,
    Failed,
}

internal sealed record ItemOutcome(
    PlanItem Item,
    ItemResult Result,
    int Error = 0,
    string? Message = null,
    string? StoredName = null);

/// <summary>What the operation actually did, as it goes into the journal (README section 9.7).</summary>
internal sealed record OpOutcome
{
    internal required long OpId { get; init; }
    internal required DeleteMode Mode { get; init; }
    internal required IReadOnlyList<ItemOutcome> Items { get; init; }
    internal required long PredictedBytes { get; init; }

    /// <summary>
    /// What this operation actually freed: the measured change for a permanent deletion,
    /// and zero for the modes that by definition free nothing (README sections 9.1, 9.2).
    /// </summary>
    internal long? ActualFreedBytes { get; init; }

    /// <summary>
    /// The raw free-space change across the volumes involved, null when it could not be
    /// read. Not the same number as <see cref="ActualFreedBytes"/>: a busy volume moves by
    /// a few kilobytes on its own, and reporting that as "reclaimed" by a rename would be
    /// a small lie that adds up (README section 9.8).
    /// </summary>
    internal long? MeasuredDelta { get; init; }

    internal TimeSpan Elapsed { get; init; }
    internal string? QuarantinePath { get; init; }
    internal DateTime? PurgeAfter { get; init; }

    internal int Succeeded => Items.Count(i => i.Result == ItemResult.Ok);
    internal int Failed => Items.Count(i => i.Result == ItemResult.Failed);
    internal int Skipped => Items.Count(i => i.Result == ItemResult.Skipped);

    internal long DoneBytes => Items.Where(i => i.Result == ItemResult.Ok).Sum(i => i.Item.Bytes);

    internal string Status => Failed == 0 && Skipped == 0 ? "completed"
        : Succeeded == 0 ? "failed"
        : "partial";
}

/// <summary>
/// The stand-in for a typed confirmation in a script (README section 13.4).
/// </summary>
/// <remarks>
/// <para>
/// The problem it solves: <c>--yes</c> in a shell history is a skeleton key that answers
/// every future question too. A token is derived from the exact list of canonical paths and
/// their sizes, so it answers one question only - if a file grew, or another matched the
/// glob since the dry run, the token no longer fits and the operation stops.
/// </para>
/// <para>
/// Not a secret and not a signature: it is a checksum of an intent. Anyone who can run the
/// dry run can get one, which is the point - the barrier is against stale automation, not
/// against the user.
/// </para>
/// </remarks>
internal static class ConfirmToken
{
    internal static string For(DeletePlan plan)
    {
        var text = new StringBuilder(plan.Items.Count * 80);
        text.Append(DeleteModes.Name(plan.Mode)).Append('\n');

        // Sorted, so the token does not depend on the order paths happened to arrive in.
        foreach (var item in plan.Items.OrderBy(i => i.CanonicalPath, StringComparer.OrdinalIgnoreCase))
        {
            text.Append(item.CanonicalPath.ToUpperInvariant()).Append('|')
                .Append(item.Bytes.ToString(CultureInfo.InvariantCulture)).Append('|')
                .Append(item.LastWriteUtc.Ticks.ToString(CultureInfo.InvariantCulture)).Append('\n');
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()));

        // 10 characters of base32 - about 50 bits, plenty against accident, and short
        // enough to retype from a screenshot without a mistake.
        const string alphabet = "0123456789abcdefghjkmnpqrstvwxyz";
        var token = new char[10];
        for (var i = 0; i < token.Length; i++) token[i] = alphabet[hash[i] & 31];

        return new string(token);
    }
}
