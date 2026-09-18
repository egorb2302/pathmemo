using Microsoft.Win32.SafeHandles;
using PathMemo.Analysis;
using PathMemo.Audit;
using PathMemo.Config;
using PathMemo.Platform.Native;
using PathMemo.Snapshots;
using PathMemo.Storage;

namespace PathMemo.Deletion;

/// <summary>What the caller asked for, before the guard has had an opinion about it.</summary>
internal sealed record DeleteRequest
{
    internal required IReadOnlyList<string> Paths { get; init; }

    /// <summary>Null means "whatever the configuration and the size say".</summary>
    internal DeleteMode? Mode { get; init; }

    internal bool DryRun { get; init; }
    internal string? Reason { get; init; }
    internal long? ScanId { get; init; }
    internal DeleteSource Source { get; init; } = DeleteSource.Cli;
}

/// <summary>
/// Free space as the volume reports it, before and after (README section 9.8).
/// </summary>
/// <remarks>
/// The only way to notice that the size model is lying. A prediction that misses by more
/// than a tenth is shown rather than quietly averaged away.
/// </remarks>
internal static class FreeSpace
{
    internal static Dictionary<string, ulong> Read(IEnumerable<string> volumes)
    {
        var map = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase);

        foreach (var volume in volumes)
            if (Kernel32.GetDiskFreeSpaceEx(volume, out _, out _, out var free))
                map[volume] = free;

        return map;
    }

    internal static long? Delta(Dictionary<string, ulong> before, Dictionary<string, ulong> after)
    {
        if (before.Count == 0 || after.Count == 0) return null;

        long total = 0;
        foreach (var (volume, free) in after)
            if (before.TryGetValue(volume, out var was))
                total += (long)free - (long)was;

        return total;
    }
}

/// <summary>
/// Plans a deletion, then carries it out (README section 9).
/// </summary>
/// <remarks>
/// <para>
/// Two phases on purpose. The plan is what the confirmation dialog, <c>--dry-run</c> and
/// the confirmation token all describe, and it is computed without touching anything. The
/// execution then re-opens each item and checks it again: the plan may be seconds or
/// minutes old by the time a human answers, and nothing in it is trusted as a substitute
/// for a fresh handle.
/// </para>
/// <para>
/// Nothing is deleted until three independent checks agree: the guard's protected set, the
/// verification against the scan, and the re-check at execution time.
/// </para>
/// </remarks>
internal sealed class DeleteEngine(AppConfig config, PathGuard guard)
{
    private readonly AppConfig _config = config;
    private readonly PathGuard _guard = guard;

    internal AppConfig Config => _config;

    internal static DeleteEngine Create() => new(AppConfig.Current, new PathGuard(ProtectedSet.Build()));

    internal DeletePlan Plan(DeleteRequest request, CancellationToken ct = default)
    {
        var items = new List<PlanItem>();
        var refusals = new List<Refusal>();
        var snapshot = _config.Delete.VerifyBeforeDelete ? LoadSnapshot(request.ScanId) : null;

        foreach (var path in request.Paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();

            var outcome = _guard.Open(path);
            if (outcome.Target is not { } target)
            {
                refusals.Add(new Refusal(path, outcome.Refusal ?? "refused"));
                continue;
            }

            using (target)
            {
                var measured = Measure(target, ct);

                // Verification against the scan, when there is one to verify against. A
                // path the scan never saw is not a mismatch - it is simply newer than the
                // snapshot, and the guard has already confirmed what it is.
                if (snapshot is not null && Expected(snapshot, target) is { } expectation)
                {
                    var complaint = PathGuard.Verify(target, expectation.Bytes, expectation.Written);
                    if (complaint is not null)
                    {
                        refusals.Add(new Refusal(target.DisplayPath, complaint));
                        continue;
                    }
                }

                items.Add(new PlanItem
                {
                    RequestedPath = path,
                    DisplayPath = target.DisplayPath,
                    CanonicalPath = target.CanonicalPath,
                    VolumeRoot = target.VolumeRoot,
                    IsDirectory = target.IsDirectory,
                    IsReparsePoint = target.IsReparsePoint,
                    Bytes = measured.Allocated,
                    LogicalBytes = measured.Logical,
                    FileCount = measured.Files,
                    LastWriteUtc = target.LastWriteUtc,
                    MeasureErrors = measured.Errors,
                });
            }
        }

        var deduplicated = DropNested(items, refusals);
        var (mode, reason) = ChooseMode(request.Mode, deduplicated);

        return new DeletePlan
        {
            Items = deduplicated,
            Refusals = refusals,
            Mode = mode,
            ModeReason = reason,
            Source = request.Source,
            ScanId = request.ScanId,
            Reason = request.Reason,
        };
    }

    /// <summary>
    /// Sizes for the plan. A file is one call; a directory is a walk, and a walk that hit
    /// unreadable subtrees produces a floor, marked as such rather than rounded up into a
    /// promise (README section 6.4).
    /// </summary>
    private static Measured Measure(GuardedTarget target, CancellationToken ct)
    {
        if (target.IsReparsePoint) return new Measured(0, 0, 1, 0, false);

        if (!target.IsDirectory)
        {
            var allocated = target.AllocatedBytes > 0 ? target.AllocatedBytes : target.Bytes;
            return new Measured(target.Bytes, allocated, 1, 0, false);
        }

        // Generous budget: this number is what the user is about to make a decision with,
        // and a truncated measurement of a directory tree is worth more time than a probe's.
        return DirectoryMeasure.Directory_(target.DisplayPath, ct, TimeSpan.FromMinutes(2));
    }

    /// <summary>
    /// Removes items that live inside other items. Deleting a tree and then one of its own
    /// children is at best a double count in the total and at worst a confusing failure.
    /// </summary>
    private static List<PlanItem> DropNested(List<PlanItem> items, List<Refusal> refusals)
    {
        var kept = new List<PlanItem>(items.Count);

        foreach (var item in items.OrderBy(i => i.CanonicalPath.Length))
        {
            var parent = kept.FirstOrDefault(k =>
                k.IsDirectory && Canonical.IsSameOrUnder(item.CanonicalPath, k.CanonicalPath));

            if (parent is not null)
            {
                refusals.Add(new Refusal(item.DisplayPath, $"already covered by {parent.DisplayPath}"));
                continue;
            }

            kept.Add(item);
        }

        return kept;
    }

    /// <summary>
    /// Picks the mode (README section 9.2). An explicit choice is honoured except where it
    /// would end in silent destruction, which is the one case the bin must not reach.
    /// </summary>
    private (DeleteMode Mode, string? Reason) ChooseMode(DeleteMode? requested, IReadOnlyList<PlanItem> items)
    {
        var settings = _config.Delete;
        var wanted = requested ?? settings.DefaultMode;

        if (wanted != DeleteMode.Recycle || items.Count == 0) return (wanted, null);

        var unsuitable = RecycleBin.WhyUnsuitable(items, settings.RecycleMaxItems, settings.RecycleMaxBytes);
        return unsuitable is null
            ? (DeleteMode.Recycle, null)
            : (DeleteMode.Quarantine, $"not the Recycle Bin: {unsuitable}");
    }

    /// <summary>
    /// Whether this plan needs a phrase typed out (README section 9.2). <c>--yes</c> does
    /// not answer this question; only the user, or a token derived from this exact plan.
    /// </summary>
    internal bool NeedsTypedConfirmation(DeletePlan plan) =>
        plan.Mode == DeleteMode.Permanent && plan.TotalBytes >= _config.Delete.RequireTypedConfirmationOverBytes;

    internal static string ConfirmationPhrase(DeletePlan plan) => $"delete {plan.Items.Count}";

    private static SnapshotContents? LoadSnapshot(long? scanId)
    {
        try
        {
            var entry = scanId is { } id
                ? SnapshotStore.List().FirstOrDefault(e => e.Id == id)
                : SnapshotStore.List().FirstOrDefault();

            return entry is null ? null : SnapshotFile.Read(entry.Path);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            return null;
        }
    }

    private static (long Bytes, DateTime? Written)? Expected(SnapshotContents snapshot, GuardedTarget target)
    {
        var node = TreeQuery.Find(snapshot.Tree, target.DisplayPath);
        if (node == NodeStore.NoNode) return null;

        var mtime = snapshot.Tree.Mtime[node];

        return (snapshot.Tree.Logical[node],
                mtime == 0 ? null : DateTimeOffset.FromUnixTimeSeconds(mtime).UtcDateTime);
    }

    /// <summary>
    /// Carries out an approved plan.
    /// </summary>
    /// <remarks>
    /// The journal row is opened before the first item and closed after the last, so an
    /// interrupted run leaves a record rather than a silence (README section 9.7).
    /// </remarks>
    internal OpOutcome Execute(
        DeletePlan plan,
        DeleteRepository journal,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var startedUtc = DateTime.UtcNow;
        var clock = System.Diagnostics.Stopwatch.StartNew();

        var purgeAfter = plan.Mode == DeleteMode.Quarantine
            ? startedUtc.AddDays(_config.Delete.QuarantineRetentionDays)
            : (DateTime?)null;

        var opId = journal.Begin(plan, startedUtc, purgeAfter);
        var before = FreeSpace.Read(plan.Volumes);

        var outcomes = plan.Mode switch
        {
            DeleteMode.Recycle => RecycleBin.Send(plan.Items, ct),
            DeleteMode.Quarantine => Quarantine(plan, opId, startedUtc, purgeAfter, progress, ct),
            _ => Unlink(plan, progress, ct),
        };

        // A short settle before reading free space again: NTFS finishes some of the work
        // after the last handle closes, and reading immediately reports less than happened.
        Thread.Sleep(500);
        var after = FreeSpace.Read(plan.Volumes);
        var delta = FreeSpace.Delta(before, after);

        var result = new OpOutcome
        {
            OpId = opId,
            Mode = plan.Mode,
            Items = outcomes,
            PredictedBytes = plan.TotalBytes,
            ActualFreedBytes = plan.Mode == DeleteMode.Permanent ? delta : 0,
            MeasuredDelta = delta,
            Elapsed = clock.Elapsed,
            QuarantinePath = plan.Mode == DeleteMode.Quarantine
                ? Path.Combine(AppPaths.QuarantineDirectory, Deletion.Quarantine.FolderName(opId))
                : null,
            PurgeAfter = purgeAfter,
        };

        journal.Complete(result, DateTime.UtcNow);
        return result;
    }

    /// <summary>Permanent deletion: unlink through the handle, recursively, by handle only.</summary>
    private List<ItemOutcome> Unlink(DeletePlan plan, IProgress<string>? progress, CancellationToken ct)
    {
        var outcomes = new List<ItemOutcome>(plan.Items.Count);

        foreach (var item in plan.Items)
        {
            if (ct.IsCancellationRequested)
            {
                outcomes.Add(new ItemOutcome(item, ItemResult.Skipped, Message: "cancelled"));
                continue;
            }

            progress?.Report(item.DisplayPath);

            var reopened = Reopen(item);
            if (reopened.Target is not { } target)
            {
                outcomes.Add(new ItemOutcome(item, ItemResult.Failed, Message: reopened.Refusal));
                continue;
            }

            using (target)
            {
                var report = HandleTreeDeleter.Delete(target, ct);

                outcomes.Add(report.Complete
                    ? new ItemOutcome(item, ItemResult.Ok)
                    : new ItemOutcome(item, ItemResult.Failed,
                        Message: Summarise(report)));
            }
        }

        return outcomes;
    }

    private static string Summarise(TreeDeleteReport report)
    {
        var first = report.Errors[0];
        return report.Errors.Count == 1
            ? $"{first.Path}: {first.Message}"
            : $"{report.Errors.Count} entries could not be removed, first: {first.Path}: {first.Message}";
    }

    /// <summary>Quarantine: one rename per item, into a store on the item's own volume.</summary>
    private List<ItemOutcome> Quarantine(
        DeletePlan plan, long opId, DateTime startedUtc, DateTime? purgeAfter,
        IProgress<string>? progress, CancellationToken ct)
    {
        var outcomes = new List<ItemOutcome>(plan.Items.Count);
        var entries = new List<QuarantineEntry>();
        var stores = new Dictionary<string, SafeFileHandle>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var seq = 0;

            foreach (var item in plan.Items)
            {
                seq++;

                if (ct.IsCancellationRequested)
                {
                    outcomes.Add(new ItemOutcome(item, ItemResult.Skipped, Message: "cancelled"));
                    continue;
                }

                progress?.Report(item.DisplayPath);

                var storeRoot = Deletion.Quarantine.StoreRootFor(item.VolumeRoot, opId);

                if (!stores.TryGetValue(storeRoot, out var data))
                {
                    var handle = Deletion.Quarantine.OpenData(storeRoot, out var storeError);
                    if (handle is null)
                    {
                        outcomes.Add(new ItemOutcome(item, ItemResult.Failed, Message: storeError));
                        continue;
                    }

                    stores[storeRoot] = data = handle;
                }

                var reopened = Reopen(item);
                if (reopened.Target is not { } target)
                {
                    outcomes.Add(new ItemOutcome(item, ItemResult.Failed, Message: reopened.Refusal));
                    continue;
                }

                using (target)
                {
                    var storedName = Deletion.Quarantine.StoredName(seq);
                    var failure = Deletion.Quarantine.Move(target, data, storedName);

                    if (failure is not null)
                    {
                        outcomes.Add(new ItemOutcome(item, ItemResult.Failed, Message: failure));
                        continue;
                    }

                    entries.Add(new QuarantineEntry
                    {
                        Seq = seq,
                        OriginalPath = item.DisplayPath,
                        StoreRoot = storeRoot,
                        StoredName = storedName,
                        Bytes = item.Bytes,
                        IsDirectory = item.IsDirectory,
                        LastWriteUtc = item.LastWriteUtc,
                    });

                    outcomes.Add(new ItemOutcome(item, ItemResult.Ok, StoredName: storedName));
                }
            }
        }
        finally
        {
            foreach (var handle in stores.Values) handle.Dispose();
        }

        // The manifest is written even when every item failed: an empty quarantine with a
        // manifest is a fact, and 'restore' reading nothing is better than 'restore'
        // finding files it has no record of.
        Deletion.Quarantine.WriteManifest(new QuarantineManifest
        {
            OpId = opId,
            CreatedUtc = startedUtc,
            PurgeAfterUtc = purgeAfter,
            Reason = plan.Reason,
            Items = entries,
        });

        return outcomes;
    }

    /// <summary>
    /// Opens an item again, at execution time, and confirms it is the same object the plan
    /// approved. The second half of README section 9.3: a plan is a description, and a
    /// description is not a handle.
    /// </summary>
    private GuardOutcome Reopen(PlanItem item)
    {
        var outcome = _guard.Open(item.CanonicalPath, PathGuard.DeleteAccess);
        if (outcome.Target is not { } target) return outcome;

        if (!Canonical.TrimSlash(target.CanonicalPath)
                .Equals(Canonical.TrimSlash(item.CanonicalPath), StringComparison.OrdinalIgnoreCase))
        {
            target.Dispose();
            return GuardOutcome.No("it is not the same object any more; rescan first");
        }

        // Against the logical size, not PlanItem.Bytes: that one is the on-disk figure,
        // rounded up to the cluster, and comparing it with the stream length would call
        // every file over a cluster "changed".
        if (!item.IsDirectory && target.Bytes != item.LogicalBytes)
        {
            target.Dispose();
            return GuardOutcome.No("its size changed between the plan and the deletion");
        }

        return outcome;
    }

    /// <summary>
    /// Puts a quarantined operation back (README section 9.4).
    /// </summary>
    internal static (int Restored, List<(string Path, string Why)> Failed) Restore(QuarantineManifest manifest)
    {
        var failures = new List<(string, string)>();
        var restored = 0;

        foreach (var entry in manifest.Items)
        {
            var failure = Deletion.Quarantine.RestoreEntry(entry);
            if (failure is null) restored++;
            else failures.Add((entry.OriginalPath, failure));
        }

        return (restored, failures);
    }

    /// <summary>
    /// Deletes a quarantine for real. This is the operation that actually frees the space
    /// the quarantine promised (README section 9.4).
    /// </summary>
    internal (long Bytes, List<(string Path, string Why)> Failed) Purge(
        QuarantineManifest manifest, CancellationToken ct = default)
    {
        var failures = new List<(string, string)>();
        var volumes = Deletion.Quarantine.StoresOf(manifest)
            .Select(store => PathGuard.VolumeRootOf(store) ?? "")
            .Where(v => v.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var before = FreeSpace.Read(volumes);

        foreach (var store in Deletion.Quarantine.StoresOf(manifest))
        {
            if (!Directory.Exists(store)) continue;

            var outcome = _guard.Open(store, PathGuard.DeleteAccess, GuardOperation.Purge);
            if (outcome.Target is not { } target)
            {
                failures.Add((store, outcome.Refusal ?? "refused"));
                continue;
            }

            using (target)
            {
                var report = HandleTreeDeleter.Delete(target, ct);
                foreach (var error in report.Errors) failures.Add(error);
            }
        }

        Thread.Sleep(500);
        var freed = FreeSpace.Delta(before, FreeSpace.Read(volumes)) ?? 0;

        return (freed, failures);
    }
}
