using PathMemo.Cli.Commands;
using PathMemo.Cli.Output;
using PathMemo.Gui.Controls;
using PathMemo.Gui.Render;
using PathMemo.Platform;

namespace PathMemo.Gui.Views;

/// <summary>
/// The first thing the window shows: the volumes, the last scan, and this tool's own
/// footprint (README sections 14.1, 24.3).
/// </summary>
/// <remarks>
/// <para>
/// Free space comes from the volume and is true now; everything else comes from a scan and is
/// dated, so every stored number is shown with the scan it came from. That rule is older than
/// the window - it is why <c>StatusReport</c> exists at all (README section 14.7) - and it
/// matters more in a GUI, where a number on a card looks live in a way a printed line does not.
/// </para>
/// <para>
/// A volume whose bar is drawn from the volume's own used bytes, not from the scan's total:
/// the two differ by whatever the scan could not read, and the honest picture is the disk's,
/// with the gap named next to it (principle P1).
/// </para>
/// </remarks>
internal sealed class OverviewView
{
    internal void Paint(
        IPainter p, Theme theme, HitMap hits, Rect area, StatusReport report, Hit hover)
    {
        var pad = p.Scale(20);
        var body = area.Deflate(pad, p.Scale(14));

        body = Section(p, theme, body, "Volumes");

        // Derived from the fonts rather than picked: a card is a line of text, a bar and a line
        // of small text, plus the paddings between them. Written as a constant it was 58, which
        // was right for this machine's fonts and cut the last line off on any other.
        var height = p.LineHeight + p.Height(FontRole.Small) + p.Scale(35);

        for (var i = 0; i < report.Volumes.Count; i++)
        {
            if (body.Height < height) break;

            Volume(p, theme, hits, body.TakeTop(height), report, report.Volumes[i], i, hover);
            body = body.DropTop(height);
        }

        body = body.DropTop(p.Scale(10));

        var columns = body.Width > p.Scale(760);
        var left = columns ? body.TakeLeft(body.Width / 2 - p.Scale(10)) : body;

        var scan = Section(p, theme, left, "Last scan");
        LastScan(p, theme, scan, report);

        var right = columns
            ? body.DropLeft(body.Width / 2 + p.Scale(10))
            : body.DropTop(p.Scale(150));

        var own = Section(p, theme, right, "pathmemo's own data");
        OwnData(p, theme, own, report);
    }

    /// <summary>A heading and a hairline, returning what is left below them.</summary>
    private static Rect Section(IPainter p, Theme theme, Rect area, string title)
    {
        var head = area.TakeTop(p.Height(FontRole.Bold) + p.Scale(8));

        p.Text(head, title, theme.Dim, FontRole.Bold);
        p.Fill(head.TakeBottom(1), theme.Border);

        return area.DropTop(head.Height + p.Scale(8));
    }

    private static void Volume(
        IPainter p, Theme theme, HitMap hits, Rect area, StatusReport report,
        VolumeInfo volume, int index, Hit hover)
    {
        var hovered = hover == new Hit(HitKind.Volume, index);
        var card = area.DropBottom(p.Scale(8));

        if (hovered) p.Fill(card, theme.Hover);
        hits.Add(card, HitKind.Volume, index);

        var inner = card.Deflate(p.Scale(10), p.Scale(6));

        var letter = inner.TakeLeft(p.Scale(52));
        p.Text(letter, volume.Letter, theme.Text, FontRole.Title);

        var rest = inner.DropLeft(letter.Width);
        var top = rest.TakeTop(p.Height(FontRole.Body));

        var name = volume.Label is { Length: > 0 } label ? $"{label}  ·  {volume.FileSystem}" : volume.FileSystem;
        p.Text(top, name, theme.Text);

        var used = SizeFormat.Bytes(volume.UsedBytes);
        var total = SizeFormat.Bytes(volume.TotalBytes);
        var free = SizeFormat.Bytes(volume.FreeBytes);
        p.Text(top, $"{used} of {total}  ·  {free} free", theme.Dim, FontRole.Body, Align.Right);

        var bar = rest.DropTop(top.Height + p.Scale(4)).TakeTop(p.Scale(8));

        // Red as it fills up, because a disk at 96% is a different fact from one at 40% and the
        // number alone does not carry it.
        var fill = volume.UsedPercent switch
        {
            >= 92 => theme.Danger,
            >= 80 => theme.Warning,
            _ => theme.Bar,
        };

        Draw.Bar(p, theme, bar, volume.UsedPercent / 100.0, fill);

        var note = rest.DropTop(top.Height + p.Scale(4) + bar.Height + p.Scale(3))
            .TakeTop(p.Height(FontRole.Small));

        p.Text(note, $"{volume.UsedPercent:0.#}% used", theme.Dim, FontRole.Small);

        // What the last scan could not account for on this volume, which is the number
        // principle P1 exists for.
        if (report.VolumeRow(volume.Letter) is { UnaccountedBytes: { } gap } && gap > 0)
            p.Text(note, $"{SizeFormat.Bytes(gap)} unaccounted by scan {report.LastScan?.Id}",
                theme.Warning, FontRole.Small, Align.Right);
    }

    private static void LastScan(IPainter p, Theme theme, Rect area, StatusReport report)
    {
        if (report.DatabaseError is { } error)
        {
            p.Text(area.TakeTop(p.LineHeight), error, theme.Warning);
            return;
        }

        if (report.LastScan is not { } scan)
        {
            p.Text(area.TakeTop(p.LineHeight),
                "Nothing scanned yet on this machine.", theme.Dim);
            return;
        }

        // A scan that was stopped produced real numbers for part of a disk, and the numbers
        // look exactly like a complete scan's. Saying so above them is the difference between
        // a partial answer and a wrong one (principle P1).
        if (scan.Status != Storage.ScanStatus.Completed)
        {
            var warning = area.TakeTop(p.LineHeight);
            p.Text(warning,
                scan.Status == Storage.ScanStatus.Cancelled
                    ? "This scan was stopped early - its totals are incomplete."
                    : $"This scan {scan.Status}.",
                theme.Warning);

            area = area.DropTop(p.LineHeight + p.Scale(4));
        }

        var rows = new (string Key, string Value)[]
        {
            ("scan", scan.Id.ToString()),
            ("started", scan.StartedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm")),
            ("scanner", scan.Scanner.ToString().ToLowerInvariant()),
            ("files", $"{scan.TotalFiles:N0}"),
            ("directories", $"{scan.TotalDirectories:N0}"),
            ("on disk", SizeFormat.Bytes(scan.AllocatedBytes)),
            ("took", scan.DurationMs is { } ms ? $"{ms / 1000.0:0.0} s" : "-"),
            ("unreadable", scan.ErrorCount == 0 ? "none" : $"{scan.ErrorCount:N0} paths"),
        };

        Facts(p, theme, area, rows);
    }

    private static void OwnData(IPainter p, Theme theme, Rect area, StatusReport report)
    {
        var total = report.SnapshotBytes + report.DatabaseBytes + report.QuarantineBytes;

        var rows = new (string Key, string Value)[]
        {
            ("snapshots", $"{report.SnapshotCount} · {SizeFormat.Bytes(report.SnapshotBytes)}"),
            ("database", SizeFormat.Bytes(report.DatabaseBytes)),
            ("quarantine", SizeFormat.Bytes(report.QuarantineBytes)),
            ("scans recorded", $"{report.ScanCount}"),
            ("total", SizeFormat.Bytes(total)),
        };

        Facts(p, theme, area, rows);

        // Principle P7: this tool's own data is bounded, and the bound is worth showing rather
        // than promising.
        var limit = 500L * 1024 * 1024;
        var bar = area.DropTop(rows.Length * p.LineHeight + p.Scale(10)).TakeTop(p.Scale(6));

        Draw.Bar(p, theme, bar, total / (double)limit,
            total > limit * 0.9 ? theme.Warning : theme.Bar);

        p.Text(area.DropTop(rows.Length * p.LineHeight + p.Scale(10) + bar.Height + p.Scale(3))
                .TakeTop(p.Height(FontRole.Small)),
            "hard limit 500 MB", theme.Dim, FontRole.Small);
    }

    /// <summary>A two-column list of facts: a dim key on the left, the value on the right.</summary>
    private static void Facts(IPainter p, Theme theme, Rect area, (string Key, string Value)[] rows)
    {
        var y = area;

        foreach (var (key, value) in rows)
        {
            if (y.Height < p.LineHeight) return;

            var line = y.TakeTop(p.LineHeight);
            p.Text(line, key, theme.Dim);
            p.Text(line, value, theme.Text, FontRole.Body, Align.Right);

            y = y.DropTop(p.LineHeight);
        }
    }
}
