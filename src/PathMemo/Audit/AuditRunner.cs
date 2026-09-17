using System.Diagnostics;
using PathMemo.Audit.Probes;
using PathMemo.Platform;
using PathMemo.Snapshots;

namespace PathMemo.Audit;

/// <summary>
/// Runs every probe, in parallel, with each one isolated: a probe that throws or hangs
/// becomes an <see cref="FindingStatus.Error"/> finding, never a missing report.
/// </summary>
internal static class AuditRunner
{
    /// <summary>
    /// The DISM analysis alone takes 3-6 s (README section 20). Anything slower than this
    /// per probe is reported as a timeout rather than waited for.
    /// </summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(45);

    internal static IReadOnlyList<IAuditProbe> AllProbes() =>
    [
        new VssProbe(),
        new WinSxsProbe(),
        new WindowsOldProbe(),
        new WindowsUpdateCacheProbe(),
        new DeliveryOptimizationProbe(),
        new InstallerOrphansProbe(),
        new HibernationProbe(),
        new PageFileProbe(),
        new SwapFileProbe(),
        new RecycleBinProbe(),
        new WslProbe(),
        new DockerProbe(),
        new HyperVDisksProbe(),
        new DumpsProbe(),
        new OneDriveProbe(),
        new ReservedStorageProbe(),
        new LogsProbe(),
        new BrowserCacheProbe(),
        new TempProbe(),
        new DefenderHistoryProbe(),
        new NtfsMetadataProbe(),
    ];

    internal static AuditReport Run(
        IReadOnlyList<IAuditProbe> probes,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        var clock = Stopwatch.StartNew();

        var context = new AuditContext
        {
            Volumes = VolumeInfo.Enumerate(fixedOnly: true),
            Elevated = Elevation.IsElevated,
            Snapshot = TryLoadSnapshot(out var snapshotId),
            SnapshotId = snapshotId,
            Cancellation = ct,
        };

        var results = new IReadOnlyList<AuditFinding>[probes.Count];

        // Most probes are a handful of stat calls; the two that spawn a process wait on
        // it. Running them side by side keeps the whole audit near the slowest probe.
        Parallel.For(0, probes.Count, new ParallelOptions
        {
            CancellationToken = ct,
            MaxDegreeOfParallelism = Math.Max(2, Math.Min(8, Environment.ProcessorCount)),
        }, i =>
        {
            results[i] = RunOne(probes[i], context, progress);
        });

        return new AuditReport
        {
            StartedUtc = started,
            Duration = clock.Elapsed,
            Elevated = context.Elevated,
            SnapshotId = snapshotId,
            Findings = [.. results.SelectMany(r => r)],
        };
    }

    private static IReadOnlyList<AuditFinding> RunOne(
        IAuditProbe probe, AuditContext context, IProgress<string>? progress)
    {
        var clock = Stopwatch.StartNew();
        progress?.Report(probe.Title);

        // The timeout is enforced by racing the probe against a delay rather than by
        // aborting it: a stuck probe (a hung DISM, an unresponsive drive) is left to die
        // with the process, and the report simply says it did not answer.
        var work = Task.Run(() => probe.Run(context).ToList(), context.Cancellation);

        try
        {
            if (!work.Wait(ProbeTimeout, context.Cancellation))
            {
                return [Failed(probe, $"did not finish within {ProbeTimeout.TotalSeconds:F0} s", clock.Elapsed)];
            }

            return [.. work.Result.Select(f => f with { Elapsed = clock.Elapsed })];
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (AggregateException ex) when (ex.InnerException is OperationCanceledException)
        {
            throw ex.InnerException;
        }
        catch (Exception ex)
        {
            var inner = ex is AggregateException agg ? agg.InnerException ?? ex : ex;
            return [Failed(probe, inner.Message, clock.Elapsed)];
        }
    }

    private static AuditFinding Failed(IAuditProbe probe, string message, TimeSpan elapsed) => new()
    {
        Id = probe.Id,
        Title = probe.Title,
        Status = FindingStatus.Error,
        Explanation = "The probe failed; nothing was measured.",
        Note = message,
        Elapsed = elapsed,
    };

    private static SnapshotContents? TryLoadSnapshot(out long? id)
    {
        id = null;
        var latest = SnapshotStore.Latest();
        if (latest is null) return null;

        try
        {
            var snapshot = SnapshotFile.Read(latest.Path);
            id = latest.Id;
            return snapshot;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            return null;
        }
    }
}
