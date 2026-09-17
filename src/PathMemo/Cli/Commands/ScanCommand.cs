using System.Globalization;
using PathMemo.Cli.Output;
using PathMemo.Platform;
using PathMemo.Scanning;
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

        var scanner = new WalkScanner();
        var request = new ScanRequest
        {
            Roots = [.. volumes.Select(v => v.Root)],
            Parallelism = options.Parallelism,
            Note = options.Note,
        };

        var reporter = options.Quiet ? null : new ProgressPrinter();
        var result = await scanner.ScanAsync(request, volumes, reporter, ct);
        reporter?.Finish();

        long? snapshotId = null;
        if (options.Save)
        {
            snapshotId = SnapshotStore.Save(result);
        }

        PrintSummary(result, options, snapshotId);
        return (result.Flags & ScanFlags.Partial) != 0 ? ExitCode.Partial : ExitCode.Ok;
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
            "Scanned {0} files in {1} directories - {2} in {3:F1} s ({4:N0} entries/s)",
            result.FileCount.ToString("N0", CultureInfo.InvariantCulture),
            result.DirectoryCount.ToString("N0", CultureInfo.InvariantCulture),
            SizeFormat.Bytes(result.AllocatedBytes),
            result.Duration.TotalSeconds,
            result.Duration.TotalSeconds > 0 ? result.TotalNodes / result.Duration.TotalSeconds : 0));

        if (result.Errors.Count > 0)
        {
            w.WriteLine();
            w.WriteLine($"Unreadable paths: {result.Errors.Count}");
            foreach (var group in result.Errors.GroupBy(e => e.Kind).OrderByDescending(g => g.Count()))
                w.WriteLine($"  {group.Key,-20} {group.Count(),6}");
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
    /// States plainly what this scan could not see. A number without its caveats is the
    /// thing that makes a disk tool untrustworthy (README principle P1).
    /// </summary>
    private static void PrintLimitations(TextWriter w, ScanResult result)
    {
        if ((result.Flags & ScanFlags.Degraded) == 0) return;

        w.WriteLine();
        w.WriteLine("Accuracy limits of this scan");

        if ((result.Flags & ScanFlags.PartialHardlinkResolution) != 0)
        {
            w.WriteLine("  · hard links are counted once per name, so directories built from them");
            w.WriteLine("    (the Windows component store above all) are overstated - needs the MFT scanner");
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

            var line = string.Format(CultureInfo.InvariantCulture,
                "  {0:N0} entries  ·  {1}  ·  {2:N0}/s  ·  {3:F1} s   {4}",
                value.Entries, SizeFormat.Bytes(value.Bytes), rate, value.Elapsed.TotalSeconds,
                PathDisplay.Shorten(value.CurrentPath ?? "", Math.Max(20, ConsoleWidth() - 55)));

            Console.Write('\r' + line.PadRight(_lastLength));
            _lastLength = line.Length;
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
}
