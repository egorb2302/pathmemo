using System.Globalization;
using PathMemo.Cli.Output;
using PathMemo.Config;
using PathMemo.Platform;
using PathMemo.Storage;
using PathMemo.Tui.Terminal;

namespace PathMemo.Tui.Screens;

/// <summary>
/// Screen 1: where the disks stand, and what was learned about them last (README 14.1).
/// </summary>
/// <remarks>
/// It answers "should I do anything at all" before the tree answers "what". Free space
/// comes from the volume itself and is always current; everything below it comes from the
/// last scan and is dated, which is why every stored number on this screen carries its
/// scan id.
/// </remarks>
internal sealed class OverviewScreen : ITuiView
{
    private int _selected;

    /// <summary>The volume the tree screen opens on when the user presses Enter here.</summary>
    internal string? SelectedLetter { get; private set; }

    public void Render(Screen screen, TuiSession session)
    {
        var report = session.Report;
        var bottom = screen.Height - 4;

        var title = screen.Row(0)
            .Space()
            .Add($"pathmemo {AppInfo.Version}", Style.Title)
            .Space(2)
            .Add("disk space screener", Style.Dim);

        if (Elevation.IsElevated) title.Space(2).Add("administrator", Style.Good);
        if (AppPaths.IsRedirected) title.Space(2).Add("--data-dir", Style.Warning);

        screen.Put(0, title);
        Draw.Rule(screen, 1);

        var y = 2;
        _selected = Math.Clamp(_selected, 0, Math.Max(0, report.Volumes.Count - 1));
        SelectedLetter = report.Volumes.Count > 0 ? report.Volumes[_selected].Letter : null;

        for (var index = 0; index < report.Volumes.Count; index++)
        {
            if (y > bottom) break;

            var volume = report.Volumes[index];
            var line = screen.Row(y);
            line.Highlight = index == _selected;

            line.Space(2).Add(TextWidth.Pad(volume.Letter, 4), Style.Strong);
            Draw.Bar(line, volume.UsedPercent, screen.Width >= 100 ? 24 : 16,
                volume.UsedPercent >= 90 ? Style.Alert : volume.UsedPercent >= 75 ? Style.Warning : Style.Bar);

            line.Space().Add(string.Format(CultureInfo.InvariantCulture, "{0,3:F0}%", volume.UsedPercent));
            line.Space(2).Add(string.Format(CultureInfo.InvariantCulture,
                "{0,9} free of {1,-9}", SizeFormat.Bytes(volume.FreeBytes), SizeFormat.Bytes(volume.TotalBytes)));

            if (line.Remaining > 12) line.Space().Add(volume.FileSystem, Style.Dim);
            if (line.Remaining > 4 && volume.Label is { Length: > 0 } label)
                line.Space().Add(label, Style.Dim);

            screen.Put(y++, line);

            if (y > bottom) break;
            if (report.VolumeRow(volume.Letter) is not { } row) continue;

            var detail = screen.Row(y).Space(6);
            detail.Add($"scan {report.LastScan?.Id}: ", Style.Dim)
                  .Add(SizeFormat.Bytes(row.ScannedBytes))
                  .Add(" scanned", Style.Dim);

            if (row is { UnaccountedBytes: { } gap } && row.UsedBytes > 0)
            {
                var share = gap * 100.0 / row.UsedBytes;
                detail.Add("  ·  ", Style.Dim)
                      .Add(SizeFormat.Bytes(gap), share > 5 ? Style.Warning : Style.Plain)
                      .Add(string.Format(CultureInfo.InvariantCulture, " unaccounted ({0:F1}%)", share), Style.Dim);
            }

            screen.Put(y++, detail);
        }

        y++;
        y = LastScan(screen, session, y, bottom);
        y = History(screen, session, y, bottom);
        Store(screen, session, y, bottom);

        Draw.Rule(screen, screen.Height - 3);

        screen.Put(screen.Height - 2, screen.Row(screen.Height - 2)
            .Space()
            .Add(session.Message, session.MessageStyle));

        Draw.Hints(screen, screen.Height - 1,
            ("up/down", "volume"), ("enter", "browse"), ("3", "audit"),
            ("F5", "rescan"), ("?", "help"), ("Q", "quit"));
    }

    private static int LastScan(Screen screen, TuiSession session, int y, int bottom)
    {
        if (y > bottom) return y;

        var report = session.Report;
        screen.Put(y, screen.Row(y).Space().Add("LAST SCAN", Style.Title));
        y++;
        if (y > bottom) return y;

        if (report.LastScan is not { } last)
        {
            screen.Put(y, screen.Row(y).Space(2)
                .Add("no scans yet - press ", Style.Dim)
                .Add("F5", Style.Accent)
                .Add(" to look at this machine", Style.Dim));
            return y + 2;
        }

        var line = screen.Row(y).Space(2);
        line.Add($"scan {last.Id}", Style.Strong)
            .Add("  ·  ", Style.Dim)
            .Add(last.StartedUtc.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture))
            .Add(" UTC", Style.Dim)
            .Add("  ·  ", Style.Dim)
            .Add(ScanRepository.Name(last.Scanner))
            .Add("  ·  ", Style.Dim)
            .Add(last.TotalFiles.ToString("N0", CultureInfo.InvariantCulture))
            .Add(" files", Style.Dim)
            .Add("  ·  ", Style.Dim)
            .Add(SizeFormat.Bytes(last.AllocatedBytes));

        if (last.DurationMs is { } ms)
            line.Add("  ·  ", Style.Dim)
                .Add(string.Format(CultureInfo.InvariantCulture, "{0:F1} s", ms / 1000.0), Style.Dim);

        screen.Put(y++, line);
        if (y > bottom) return y;

        var notes = screen.Row(y).Space(2);
        var wrote = false;

        if (last.Status != ScanStatus.Completed)
        {
            notes.Add(last.Status, Style.Warning).Add(": totals are incomplete", Style.Dim);
            wrote = true;
        }

        if (last.ErrorCount > 0)
        {
            if (wrote) notes.Add("  ·  ", Style.Dim);
            notes.Add(last.ErrorCount.ToString("N0", CultureInfo.InvariantCulture), Style.Warning)
                 .Add(" unreadable path(s)", Style.Dim);
            wrote = true;
        }

        if (!last.SnapshotAvailable)
        {
            if (wrote) notes.Add("  ·  ", Style.Dim);
            notes.Add("tree no longer stored", Style.Dim);
            wrote = true;
        }

        if (!Elevation.IsElevated && last.Scanner == Scanning.ScannerKind.Walk)
        {
            if (wrote) notes.Add("  ·  ", Style.Dim);
            notes.Add("as administrator the MFT scan is ~10x faster", Style.Dim);
            wrote = true;
        }

        if (wrote) screen.Put(y++, notes);
        return y + 1;
    }

    private static int History(Screen screen, TuiSession session, int y, int bottom)
    {
        var report = session.Report;
        if (y + 1 > bottom || report.Series.Count < 2) return y;

        screen.Put(y, screen.Row(y).Space()
            .Add("USED SPACE", Style.Title)
            .Add($"  {report.SeriesLetter}", Style.Dim));
        y++;

        var values = report.Series.Select(p => p.UsedBytes).ToArray();
        var spark = Draw.Sparkline(values, Math.Min(48, screen.Width - 40));
        var change = values[^1] - values[0];

        var line = screen.Row(y).Space(2).Add(spark, Style.Accent).Space(2);
        line.Add(SizeFormat.Bytes(values[^1]))
            .Add(" used", Style.Dim)
            .Add("  ·  ", Style.Dim)
            .Add((change >= 0 ? "+" : "") + SizeFormat.Bytes(change), change > 0 ? Style.Warning : Style.Good)
            .Add($" over {report.Series.Count} scans", Style.Dim);

        screen.Put(y++, line);
        return y + 1;
    }

    private static void Store(Screen screen, TuiSession session, int y, int bottom)
    {
        if (y + 1 > bottom) return;

        var report = session.Report;
        screen.Put(y, screen.Row(y).Space().Add("STORE", Style.Title));
        y++;
        if (y > bottom) return;

        var line = screen.Row(y).Space(2);
        line.Add($"{report.SnapshotCount} snapshot(s)")
            .Add($" {SizeFormat.Bytes(report.SnapshotBytes)}", Style.Dim)
            .Add("  ·  ", Style.Dim)
            .Add($"database {SizeFormat.Bytes(report.DatabaseBytes)}")
            .Add("  ·  ", Style.Dim)
            .Add($"{report.ScanCount} scan(s) recorded");

        if (report.QuarantineBytes > 0)
            line.Add("  ·  ", Style.Dim).Add($"quarantine {SizeFormat.Bytes(report.QuarantineBytes)}");

        screen.Put(y++, line);
        if (y > bottom) return;

        screen.Put(y, screen.Row(y).Space(2).Add(
            report.DatabaseError is { } error ? $"history unavailable: {error}" : AppPaths.DataDirectory,
            report.DatabaseError is null ? Style.Dim : Style.Warning));
    }

    public bool HandleKey(in TuiKey key, TuiSession session)
    {
        var volumes = session.Report.Volumes;

        if (key.Is('j') || key.Is(ConsoleKey.DownArrow))
        {
            _selected = Math.Min(_selected + 1, Math.Max(0, volumes.Count - 1));
            return true;
        }

        if (key.Is('k') || key.Is(ConsoleKey.UpArrow))
        {
            _selected = Math.Max(0, _selected - 1);
            return true;
        }

        return false;
    }
}
