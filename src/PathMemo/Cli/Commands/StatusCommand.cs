using System.Globalization;
using PathMemo.Cli.Output;
using PathMemo.Config;
using PathMemo.Platform;
using PathMemo.Snapshots;
using PathMemo.Storage;

namespace PathMemo.Cli.Commands;

/// <summary>One point of the "how full was this disk" series, for the Overview sparkline.</summary>
internal readonly record struct UsedPoint(long ScanId, DateTime WhenUtc, long UsedBytes);

/// <summary>
/// Everything the Overview screen shows, gathered once (README section 14.1).
/// </summary>
/// <remarks>
/// A record rather than a screen method, because the same facts are printed by
/// <c>pathmemo status</c> when there is no terminal to draw on (README section 14.5).
/// Collecting is deliberately total: a locked database costs the history block and
/// nothing else, so the free-space numbers still appear.
/// </remarks>
internal sealed record StatusReport
{
    internal required IReadOnlyList<VolumeInfo> Volumes { get; init; }
    internal ScanRow? LastScan { get; init; }
    internal IReadOnlyList<ScanVolumeRow> LastScanVolumes { get; init; } = [];

    /// <summary>Used bytes of <see cref="SeriesLetter"/> over the recorded scans, oldest first.</summary>
    internal IReadOnlyList<UsedPoint> Series { get; init; } = [];
    internal string? SeriesLetter { get; init; }

    internal int ScanCount { get; init; }
    internal int SnapshotCount { get; init; }
    internal long SnapshotBytes { get; init; }
    internal long DatabaseBytes { get; init; }
    internal long QuarantineBytes { get; init; }
    internal string? DatabaseError { get; init; }

    /// <summary>The volume the tree screen opens on: the one the last scan covered.</summary>
    internal string? PrimaryLetter => SeriesLetter ?? Volumes.FirstOrDefault()?.Letter;

    internal static StatusReport Collect()
    {
        var volumes = VolumeInfo.Enumerate(fixedOnly: true);
        var snapshots = SnapshotStore.List();

        var report = new StatusReport
        {
            Volumes = volumes,
            SnapshotCount = snapshots.Count,
            SnapshotBytes = snapshots.Sum(s => s.Bytes),
            DatabaseBytes = FileBytes(AppPaths.DatabasePath),
            QuarantineBytes = DirectoryBytes(AppPaths.QuarantineDirectory),
        };

        using var catalog = ScanCatalog.TryOpen(out var error);
        if (catalog is null) return report with { DatabaseError = error };

        catalog.Reconcile();

        var rows = catalog.Scans.List(limit: 40);
        var last = rows.FirstOrDefault(r => r.Status == ScanStatus.Completed) ?? rows.FirstOrDefault();

        var lastVolumes = last is null ? [] : catalog.Scans.Volumes(last.Id);

        // The series follows one volume - the largest the last scan saw - because two
        // volumes on one sparkline is two stories drawn as one.
        var letter = lastVolumes.OrderByDescending(v => v.TotalBytes).FirstOrDefault()?.Letter;
        var series = new List<UsedPoint>();

        if (letter is not null)
        {
            foreach (var row in rows.Reverse())
            {
                if (row.Status != ScanStatus.Completed) continue;

                foreach (var volume in catalog.Scans.Volumes(row.Id))
                {
                    if (!string.Equals(volume.Letter, letter, StringComparison.OrdinalIgnoreCase)) continue;
                    if (volume.TotalBytes <= 0) continue;

                    series.Add(new UsedPoint(row.Id, row.StartedUtc, volume.UsedBytes));
                    break;
                }
            }
        }

        return report with
        {
            LastScan = last,
            LastScanVolumes = lastVolumes,
            Series = series,
            SeriesLetter = letter,
            ScanCount = catalog.Scans.Count(),
        };
    }

    internal ScanVolumeRow? VolumeRow(string letter) =>
        LastScanVolumes.FirstOrDefault(v => string.Equals(v.Letter, letter, StringComparison.OrdinalIgnoreCase));

    private static long FileBytes(string path)
    {
        try { return File.Exists(path) ? new FileInfo(path).Length : 0; }
        catch (IOException) { return 0; }
    }

    private static long DirectoryBytes(string path)
    {
        try
        {
            if (!Directory.Exists(path)) return 0;

            long total = 0;
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                total += new FileInfo(file).Length;

            return total;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }
}

/// <summary>
/// <c>pathmemo status</c>: the Overview screen without a terminal to draw it in.
/// </summary>
/// <remarks>
/// This is what <c>pathmemo</c> with no arguments does when stdout is redirected: a TUI
/// would be meaningless there, and printing the help to a pipe is not an answer either
/// (README sections 13, 14.5).
/// </remarks>
internal static class StatusCommand
{
    internal static int Run()
    {
        var report = StatusReport.Collect();
        var w = Console.Out;

        w.WriteLine();
        w.WriteLine($"pathmemo {AppInfo.Version}   ·   {report.Volumes.Count} fixed volume(s)");
        w.WriteLine();

        foreach (var volume in report.Volumes)
        {
            w.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "{0,-3} {1,9} free of {2,-9}  {3,5:F1}% used   {4}{5}",
                volume.Letter,
                SizeFormat.Bytes(volume.FreeBytes),
                SizeFormat.Bytes(volume.TotalBytes),
                volume.UsedPercent,
                volume.FileSystem,
                volume.Label is { Length: > 0 } label ? $"   {label}" : ""));

            if (report.VolumeRow(volume.Letter) is { UnaccountedBytes: { } gap } row && row.UsedBytes > 0)
                w.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "    scanned {0}  ·  unaccounted {1} ({2:F1}%)",
                    SizeFormat.Bytes(row.ScannedBytes), SizeFormat.Bytes(gap),
                    gap * 100.0 / row.UsedBytes));
        }

        w.WriteLine();

        if (report.LastScan is { } last)
        {
            w.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "Last scan {0}  ·  {1:yyyy-MM-dd HH:mm} UTC  ·  {2}  ·  {3:N0} files  ·  {4}  ·  {5}",
                last.Id, last.StartedUtc, ScanRepository.Name(last.Scanner),
                last.TotalFiles, SizeFormat.Bytes(last.AllocatedBytes), last.Status));

            if (last.ErrorCount > 0)
                w.WriteLine($"          {last.ErrorCount:N0} unreadable path(s)");
            if (!last.SnapshotAvailable)
                w.WriteLine("          its tree is gone; only the numbers remain");
        }
        else
        {
            w.WriteLine("No scans yet - run 'pathmemo scan' first.");
        }

        w.WriteLine();
        w.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "Store  {0} snapshot(s), {1}  ·  database {2}  ·  {3} scan(s) recorded{4}",
            report.SnapshotCount, SizeFormat.Bytes(report.SnapshotBytes),
            SizeFormat.Bytes(report.DatabaseBytes), report.ScanCount,
            report.QuarantineBytes > 0 ? $"  ·  quarantine {SizeFormat.Bytes(report.QuarantineBytes)}" : ""));
        w.WriteLine($"       {AppPaths.DataDirectory}");

        if (report.DatabaseError is { } error)
            Console.Error.WriteLine($"pathmemo: history unavailable - {error}");

        return ExitCode.Ok;
    }
}
