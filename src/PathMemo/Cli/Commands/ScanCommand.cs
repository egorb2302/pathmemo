using System.Globalization;
using PathMemo.Cli.Interactive;
using PathMemo.Cli.Output;
using PathMemo.Platform;
using PathMemo.Scanning;
using PathMemo.Scanning.Mft;
using PathMemo.Snapshots;

namespace PathMemo.Cli.Commands;

internal static class ScanCommand
{
    internal static async Task<int> RunAsync(ScanOptions options, CancellationToken ct)
    {
        var volumes = ResolveVolumes(options.Roots);
        if (volumes.Count == 0)
        {
            Console.Error.WriteLine("pathmemo: nothing to scan");
            return ExitCode.NoData;
        }

        // README section 4.2: NTFS without elevation gets the offer once, then degrades.
        if (options.Scanner is null && !options.Quiet && !options.NoElevate
            && !Elevation.IsElevated && volumes.Any(v => v.IsNtfs))
        {
            switch (ElevationPrompt.Ask())
            {
                case ElevationChoice.Restart:
                    return Relaunch.AsAdministrator(options.RelaunchArguments) ? ExitCode.Ok : ExitCode.Failure;
                case ElevationChoice.Quit:
                    return ExitCode.Cancelled;
            }
        }

        var request = new ScanRequest
        {
            Roots = [.. volumes.Select(v => v.Root)],
            Parallelism = options.Parallelism,
            Note = options.Note,
            ForceScanner = options.Scanner,
        };

        var reporter = options.Quiet ? null : new ProgressPrinter();
        var result = await ScanAllAsync(request, volumes, reporter, ct);
        reporter?.Finish();

        long? snapshotId = null;
        if (options.Save)
        {
            snapshotId = SnapshotStore.Save(result);
        }

        PrintSummary(result, options, snapshotId);

        if (options.Pause) Launcher.Pause();

        return (result.Flags & ScanFlags.Partial) != 0 ? ExitCode.Partial : ExitCode.Ok;
    }

    /// <summary>
    /// Runs the MFT scanner on every volume that qualifies and the walk scanner on the
    /// rest, including any volume the MFT path gave up on, and joins the results.
    /// </summary>
    internal static async Task<ScanResult> ScanAllAsync(
        ScanRequest request,
        IReadOnlyList<VolumeInfo> volumes,
        IProgress<ScanProgress>? progress,
        CancellationToken ct)
    {
        var mft = new MftScanner();
        var walk = new WalkScanner();

        var viaMft = new List<VolumeInfo>();
        var viaWalk = new List<VolumeInfo>();
        foreach (var volume in volumes)
        {
            var useMft = request.ForceScanner switch
            {
                ScannerKind.Mft => true,
                ScannerKind.Walk => false,
                _ => mft.CanScan(volume).Can,
            };
            (useMft ? viaMft : viaWalk).Add(volume);
        }

        var results = new List<ScanResult>();
        var fallbacks = new List<ScanError>();

        foreach (var volume in viaMft)
        {
            try
            {
                results.Add(await mft.ScanAsync(request, [volume], progress, ct));
            }
            catch (MftUnavailableException ex)
            {
                // Any surprise in the $MFT means the walk, with the reason on record
                // (README section 4.3).
                fallbacks.Add(new ScanError(volume.Root, ScanErrorKind.IoError, ex.Win32Code,
                    $"MFT scan unavailable, fell back to the directory walk: {ex.Message}"));
                viaWalk.Add(volume);
            }

            if (ct.IsCancellationRequested) break;
        }

        if (viaWalk.Count > 0 && !ct.IsCancellationRequested)
            results.Add(await walk.ScanAsync(request, viaWalk, progress, ct));

        return Merge(results, fallbacks, request);
    }

    private static ScanResult Merge(List<ScanResult> results, List<ScanError> extraErrors, ScanRequest request)
    {
        if (results.Count == 1 && extraErrors.Count == 0) return results[0];
        if (results.Count == 0)
        {
            return new ScanResult
            {
                Scanner = ScannerKind.Walk,
                Flags = ScanFlags.Partial,
                StartedUtc = DateTime.UtcNow,
                Duration = TimeSpan.Zero,
                Volumes = [],
                Tree = TreeAssembly.Concat([]),
                Errors = extraErrors,
                Note = request.Note,
            };
        }

        var flags = ScanFlags.None;
        var errors = new List<ScanError>(extraErrors);
        var volumes = new List<VolumeInfo>();
        var trees = new List<NodeStore>();
        var duration = TimeSpan.Zero;

        foreach (var r in results)
        {
            flags |= r.Flags;
            errors.AddRange(r.Errors);
            volumes.AddRange(r.Volumes);
            trees.Add(r.Tree);
            duration += r.Duration;
        }

        var anyMft = results.Any(r => r.Scanner == ScannerKind.Mft);

        return new ScanResult
        {
            Scanner = anyMft ? ScannerKind.Mft : ScannerKind.Walk,
            Flags = flags,
            StartedUtc = results.Min(r => r.StartedUtc),
            Duration = duration,
            Volumes = volumes,
            Tree = TreeAssembly.Concat(trees),
            Errors = errors,
            Note = request.Note,
        };
    }

    private static IReadOnlyList<VolumeInfo> ResolveVolumes(IReadOnlyList<string> roots)
    {
        var all = VolumeInfo.Enumerate();

        if (roots.Count == 0)
            return [.. all.Where(v => v.DriveType == DriveType.Fixed)];

        // A root may be any directory, not just a volume root. Attribute it to the volume
        // it lives on so free-space reconciliation still has real numbers to compare to.
        var selected = new List<VolumeInfo>();
        foreach (var root in roots)
        {
            var full = Path.GetFullPath(root);
            if (!Directory.Exists(full))
            {
                Console.Error.WriteLine($"pathmemo: not a directory: {full}");
                continue;
            }

            var owner = all.FirstOrDefault(v =>
                full.StartsWith(v.Root, StringComparison.OrdinalIgnoreCase));

            if (owner is null)
            {
                Console.Error.WriteLine($"pathmemo: no volume found for {full}");
                continue;
            }

            selected.Add(owner with { Root = EnsureTrailingSeparator(full) });
        }

        return selected;
    }

    private static string EnsureTrailingSeparator(string path) =>
        path.EndsWith(Path.DirectorySeparatorChar) ? path : path + Path.DirectorySeparatorChar;

    private static void PrintSummary(ScanResult result, ScanOptions options, long? snapshotId)
    {
        var tree = result.Tree;
        var w = Console.Out;

        w.WriteLine();
        w.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "Scanned {0} files in {1} directories - {2} in {3:F1} s ({4:N0} entries/s)  ·  {5} mode",
            result.FileCount.ToString("N0", CultureInfo.InvariantCulture),
            result.DirectoryCount.ToString("N0", CultureInfo.InvariantCulture),
            SizeFormat.Bytes(result.AllocatedBytes),
            result.Duration.TotalSeconds,
            result.Duration.TotalSeconds > 0 ? result.TotalNodes / result.Duration.TotalSeconds : 0,
            result.Scanner == ScannerKind.Mft ? "MFT" : "walk"));

        PrintReconciliation(w, result);

        if (result.Errors.Count > 0)
        {
            w.WriteLine();
            w.WriteLine($"Unreadable paths: {result.Errors.Count}");
            foreach (var group in result.Errors.GroupBy(e => e.Kind).OrderByDescending(g => g.Count()))
                w.WriteLine($"  {group.Key,-20} {group.Count(),6}");

            foreach (var note in result.Errors.Where(e => e.Kind is ScanErrorKind.Unknown or ScanErrorKind.IoError && e.Win32Code == 0).Take(4))
                w.WriteLine($"  · {note.Path}: {note.Message}");
        }

        PrintLimitations(w, result);

        if (Environment.GetEnvironmentVariable("PATHMEMO_DIAG") == "1")
        {
            var process = System.Diagnostics.Process.GetCurrentProcess();
            w.WriteLine();
            w.WriteLine("Diagnostics");
            w.WriteLine($"  live managed      {SizeFormat.Bytes(GC.GetTotalMemory(forceFullCollection: true))}");
            w.WriteLine($"  managed heap peak {SizeFormat.Bytes((long)GC.GetGCMemoryInfo().TotalCommittedBytes)}");
            w.WriteLine($"  private bytes     {SizeFormat.Bytes(process.PrivateMemorySize64)}");
            w.WriteLine($"  working set       {SizeFormat.Bytes(process.WorkingSet64)}");
            w.WriteLine($"  peak working set  {SizeFormat.Bytes(process.PeakWorkingSet64)}");
            w.WriteLine($"  gen0/1/2 GCs      {GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)}");
            w.WriteLine($"  bytes per node    {(result.TotalNodes > 0 ? GC.GetTotalMemory(false) / result.TotalNodes : 0)}");
        }

        if (snapshotId is { } id)
        {
            w.WriteLine();
            w.WriteLine($"Saved as scan {id}.  Browse it with:  pathmemo tree   ·   pathmemo top --min 1GB");
        }

        w.WriteLine();
        w.WriteLine("Largest directories");
        PrintTop(w, tree, directories: true, options.Top);

        w.WriteLine();
        w.WriteLine("Largest files");
        PrintTop(w, tree, directories: false, options.Top);
    }

    /// <summary>
    /// The scan total against what the volume says is used (README section 3.5). Only
    /// for whole-volume scans: a subtree has nothing to reconcile against.
    /// </summary>
    private static void PrintReconciliation(TextWriter w, ScanResult result)
    {
        var tree = result.Tree;
        var printedHeader = false;

        for (var v = 0; v < result.Volumes.Count && v < tree.Roots.Length; v++)
        {
            var volume = result.Volumes[v];
            if (volume.Root.TrimEnd(Path.DirectorySeparatorChar).Length > 2) continue;   // a subtree, not a volume

            var scanned = tree.Allocated[tree.Roots[v]];
            var used = (long)volume.UsedBytes;
            var gap = used - scanned;
            var share = used > 0 ? gap * 100.0 / used : 0;

            if (!printedHeader)
            {
                w.WriteLine();
                printedHeader = true;
            }

            w.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "Volume {0}  used {1}  ·  scanned {2}  ·  unaccounted {3} ({4:F1}%){5}",
                volume.Letter, SizeFormat.Bytes(used), SizeFormat.Bytes(scanned),
                SizeFormat.Bytes(gap), share,
                result.Scanner == ScannerKind.Mft
                    ? "  - run 'pathmemo audit' for restore points and other invisible space"
                    : "  - hard links, metadata and skipped paths; the MFT scan closes most of it"));
        }
    }

    /// <summary>
    /// States plainly what this scan could not see. A number without its caveats is the
    /// thing that makes a disk tool untrustworthy (README principle P1).
    /// </summary>
    private static void PrintLimitations(TextWriter w, ScanResult result)
    {
        if ((result.Flags & (ScanFlags.Degraded | ScanFlags.Partial)) == 0) return;

        w.WriteLine();
        w.WriteLine("Accuracy limits of this scan");

        if ((result.Flags & ScanFlags.PartialHardlinkResolution) != 0)
        {
            w.WriteLine("  · hard links were resolved only for files of 1 MB and more; directories built");
            w.WriteLine("    from small hard-linked files (the component store) are still overstated");
        }

        if ((result.Flags & ScanFlags.NoAdsAccounting) != 0)
            w.WriteLine("  · alternate data streams are not counted");

        if ((result.Flags & ScanFlags.Elevated) == 0)
            w.WriteLine("  · protected directories and other user profiles were skipped");

        if ((result.Flags & ScanFlags.Partial) != 0)
            w.WriteLine("  · CANCELLED: totals are incomplete");
    }

    private static void PrintTop(TextWriter w, NodeStore tree, bool directories, int limit)
    {
        var candidates = new List<int>();
        for (var i = 0; i < tree.Count; i++)
        {
            if (tree.IsDirectory(i) != directories) continue;
            if (tree.Allocated[i] <= 0) continue;
            if ((tree.Flags[i] & NodeFlags.HardlinkAlias) != 0) continue;
            candidates.Add(i);
        }

        candidates.Sort((a, b) => tree.Allocated[b].CompareTo(tree.Allocated[a]));

        foreach (var node in candidates.Take(limit))
        {
            w.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "  {0,9}  {1}",
                SizeFormat.Bytes(tree.Allocated[node]),
                PathDisplay.Shorten(tree.GetPath(node), Math.Max(40, ConsoleWidth() - 14))));
        }

        if (candidates.Count == 0) w.WriteLine("  (none)");
    }

    private static int ConsoleWidth()
    {
        try { return Console.IsOutputRedirected ? 120 : Console.WindowWidth; }
        catch (IOException) { return 120; }
    }

    private sealed class ProgressPrinter : IProgress<ScanProgress>
    {
        private int _lastLength;

        public void Report(ScanProgress value)
        {
            if (Console.IsOutputRedirected) return;

            var rate = value.Elapsed.TotalSeconds > 0
                ? value.Entries / value.Elapsed.TotalSeconds
                : 0;

            // MFT mode knows the record count up front, so its percentage is honest.
            // Walk mode does not, and a made-up percentage is worse than none
            // (README section 4.7).
            var tail = value.Fraction is { } fraction
                ? $"$MFT {fraction * 100:F0}%  {Meter(fraction, 20)}"
                : PathDisplay.Shorten(value.CurrentPath ?? "", Math.Max(20, ConsoleWidth() - 55));

            var line = string.Format(CultureInfo.InvariantCulture,
                "  {0:N0} entries  ·  {1}  ·  {2:N0}/s  ·  {3:F1} s   {4}",
                value.Entries, SizeFormat.Bytes(value.Bytes), rate, value.Elapsed.TotalSeconds, tail);

            Console.Write('\r' + line.PadRight(_lastLength));
            _lastLength = line.Length;
        }

        private static string Meter(double fraction, int width)
        {
            var filled = Math.Clamp((int)Math.Round(fraction * width), 0, width);
            return new string('#', filled) + new string('.', width - filled);
        }

        internal void Finish()
        {
            if (Console.IsOutputRedirected || _lastLength == 0) return;
            Console.Write('\r' + new string(' ', _lastLength) + '\r');
        }
    }
}

internal sealed record ScanOptions
{
    internal IReadOnlyList<string> Roots { get; init; } = [];
    internal int Parallelism { get; init; }
    internal int Top { get; init; } = 15;
    internal bool Quiet { get; init; }
    internal bool Save { get; init; } = true;
    internal string? Note { get; init; }

    /// <summary>Forced scanner, or null for the README section 4.2 choice.</summary>
    internal ScannerKind? Scanner { get; init; }

    /// <summary>Wait for Enter before exiting, for a console window that would otherwise vanish.</summary>
    internal bool Pause { get; init; }

    /// <summary>Skip the restart-as-administrator offer and go straight to the degraded scan.</summary>
    internal bool NoElevate { get; init; }

    /// <summary>What to pass to the elevated copy if the user asks for one.</summary>
    internal IReadOnlyList<string> RelaunchArguments { get; init; } = ["--interactive"];
}
