using System.Collections.Concurrent;
using System.Diagnostics;
using PathMemo.Config;
using PathMemo.Platform;
using PathMemo.Snapshots;

namespace PathMemo.Scanning;

/// <summary>
/// Directory-walk scanner: the fallback path. Works on any filesystem without elevation,
/// and is roughly 40x slower than reading the MFT (README section 4.1).
/// </summary>
internal sealed class WalkScanner : IScanner
{
    public ScannerKind Kind => ScannerKind.Walk;

    public (bool Can, string Reason) CanScan(VolumeInfo volume) => (true, "ready");

    /// <summary>One directory awaiting enumeration, with its volume's cluster size.</summary>
    private readonly record struct DirTask(int DirId, string Path, long ClusterBytes);

    public async Task<ScanResult> ScanAsync(
        ScanRequest request,
        IReadOnlyList<VolumeInfo> volumes,
        IProgress<ScanProgress>? progress,
        CancellationToken ct)
    {
        var startedUtc = DateTime.UtcNow;
        var stopwatch = Stopwatch.StartNew();

        var dirs = new ChunkedList<string>();
        var results = new ChunkedList<WalkDirResult?>();
        var errors = new ConcurrentBag<ScanError>();
        var queue = new ConcurrentQueue<DirTask>();

        var rootDirIds = new int[volumes.Count];
        for (var v = 0; v < volumes.Count; v++)
        {
            var root = volumes[v].Root;
            var id = dirs.Add(root);
            results.Add(null);
            rootDirIds[v] = id;
            queue.Enqueue(new DirTask(id, root, volumes[v].ClusterBytes));
        }

        var parallelism = ResolveParallelism(request, volumes);
        var counters = new Counters();

        // One name blob per worker: interning from the kernel's own span allocates nothing,
        // and a per-worker table avoids 1.5M lock acquisitions on a shared one.
        var listers = new DirectoryLister[parallelism];
        for (var i = 0; i < parallelism; i++)
            listers[i] = new DirectoryLister(new NameBlobBuilder(1 << 15), (byte)i, resolveAllocatedSize: true);

        using var progressLoop = StartProgressLoop(progress, counters, stopwatch, ct);

        await Task.WhenAll(Enumerable.Range(0, parallelism).Select(worker => Task.Run(
            () => Work(listers[worker], queue, dirs, results, errors, counters, volumes.Count, ct),
            CancellationToken.None)));

        stopwatch.Stop();

        var cancelled = ct.IsCancellationRequested;

        var tree = SnapshotBuilder.Build(dirs, results, rootDirIds, volumes, listers);

        var flags = ScanFlags.Degraded
                  | ScanFlags.PartialHardlinkResolution
                  | ScanFlags.NoAdsAccounting;
        if (cancelled) flags |= ScanFlags.Partial;
        if (Elevation.IsElevated) flags |= ScanFlags.Elevated;

        return new ScanResult
        {
            Scanner = ScannerKind.Walk,
            Flags = flags,
            StartedUtc = startedUtc,
            Duration = stopwatch.Elapsed,
            Volumes = volumes,
            Tree = tree,
            Errors = [.. errors],
            Note = request.Note,
        };
    }

    private sealed class Counters
    {
        internal long Entries;
        internal long Bytes;
        internal long Errors;
        internal string? CurrentPath;
    }

    private static void Work(
        DirectoryLister lister,
        ConcurrentQueue<DirTask> queue,
        ChunkedList<string> dirs,
        ChunkedList<WalkDirResult?> results,
        ConcurrentBag<ScanError> errors,
        Counters counters,
        int volumeCount,
        CancellationToken ct)
    {
        var buffer = new List<RawEntry>(256);

        // Work stealing rather than partitioning by top-level folder: C:\Windows\WinSxS
        // alone is ~40% of all files on a typical disk, so a static split leaves one
        // worker doing almost everything (README section 4.4).
        while (!ct.IsCancellationRequested)
        {
            if (!queue.TryDequeue(out var task))
            {
                // The queue can be transiently empty while other workers are still
                // discovering subdirectories. Spin briefly before concluding it is done.
                if (!WaitForMoreWork(queue, ct)) return;
                continue;
            }

            Volatile.Write(ref counters.CurrentPath, task.Path);

            var result = new WalkDirResult { BlobId = lister.BlobId };

            if (!lister.TryList(task.Path, buffer, task.ClusterBytes, out var error))
            {
                result.Failed = true;
                results[task.DirId] = result;
                errors.Add(error!);
                Interlocked.Increment(ref counters.Errors);
                continue;
            }

            var entries = buffer.ToArray();
            var subdirIds = new int[entries.Length];
            if (lister.PendingLinks.Count > 0) result.Links = [.. lister.PendingLinks];
            long bytes = 0;

            for (var i = 0; i < entries.Length; i++)
            {
                var entry = entries[i];
                bytes += entry.Allocated;

                var isDir = (entry.Flags & NodeFlags.Directory) != 0;
                var isReparse = (entry.Flags & NodeFlags.Reparse) != 0;

                // Reparse points are counted as a node of zero bytes and never entered:
                // a junction loop would otherwise recurse forever, and a mount point
                // would count another volume twice (README section 4.6).
                if (isDir && !isReparse)
                {
                    var childPath = string.Concat(
                        task.Path.AsSpan(),
                        task.Path.EndsWith(Path.DirectorySeparatorChar) ? "" : Path.DirectorySeparatorChar.ToString(),
                        System.Text.Encoding.UTF8.GetString(lister.Blob.Read(entry.NameOffset)));

                    var childId = dirs.Add(childPath);
                    results.Add(null);
                    subdirIds[i] = childId;
                    queue.Enqueue(new DirTask(childId, childPath, task.ClusterBytes));
                }
                else
                {
                    subdirIds[i] = -1;
                }
            }

            result.Entries = entries;
            result.SubdirIds = subdirIds;
            results[task.DirId] = result;

            // The path has served its purpose. Only volume roots are needed later, and
            // 360k retained path strings cost ~60 MB.
            if (dirs[task.DirId] is not null && task.DirId >= volumeCount) dirs[task.DirId] = null!;

            Interlocked.Add(ref counters.Entries, entries.Length);
            Interlocked.Add(ref counters.Bytes, bytes);
        }
    }

    private static bool WaitForMoreWork(ConcurrentQueue<DirTask> queue, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            if (ct.IsCancellationRequested) return false;
            if (!queue.IsEmpty) return true;
            Thread.Sleep(attempt < 10 ? 0 : 2);
        }
        return !queue.IsEmpty;
    }

    private static Timer StartProgressLoop(
        IProgress<ScanProgress>? progress, Counters counters, Stopwatch stopwatch, CancellationToken ct)
    {
        if (progress is null) return new Timer(static _ => { }, null, Timeout.Infinite, Timeout.Infinite);

        // A separate timer reading volatile counters, rather than an event per file:
        // a million progress callbacks would cost more than the scan (README section 4.7).
        return new Timer(_ =>
        {
            if (ct.IsCancellationRequested) return;
            progress.Report(new ScanProgress
            {
                Entries = Volatile.Read(ref counters.Entries),
                Bytes = Volatile.Read(ref counters.Bytes),
                Errors = Volatile.Read(ref counters.Errors),
                CurrentPath = Volatile.Read(ref counters.CurrentPath),
                Elapsed = stopwatch.Elapsed,
            });
        }, null, 250, 250);
    }

    private static int ResolveParallelism(ScanRequest request, IReadOnlyList<VolumeInfo> volumes)
    {
        if (request.Parallelism > 0) return request.Parallelism;

        // Mixed media: take the most conservative recommendation, because a single
        // rotational volume in the set is what will bottleneck.
        var best = int.MaxValue;
        foreach (var volume in volumes)
            best = Math.Min(best, StorageMedium.RecommendedParallelism(StorageMedium.Detect(volume.Letter)));

        return best == int.MaxValue ? 4 : best;
    }
}
