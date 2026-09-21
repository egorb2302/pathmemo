using PathMemo.Cli.Output;
using PathMemo.Gui.Controls;
using PathMemo.Gui.Render;
using PathMemo.Gui.Work;
using PathMemo.Scanning;

namespace PathMemo.Gui.Views;

/// <summary>
/// What a running scan looks like (README section 24.4).
/// </summary>
/// <remarks>
/// <para>
/// A card over the window rather than a separate dialog, and everything behind it is sealed
/// out of the hit map: while a scan runs there is exactly one thing to press, and a tab switch
/// that leaves a scan running invisibly is how a user ends up with two.
/// </para>
/// <para>
/// <b>Only the MFT scanner knows a fraction</b> - it reads the record count up front, while a
/// walk finds out how much there is by finishing. So a bar is drawn when there is one to draw
/// and counters when there are not, instead of an animation that implies a progress nobody
/// measured (principle P1, honest numbers).
/// </para>
/// </remarks>
internal static class ScanView
{
    internal static void Paint(
        IPainter p, Theme theme, HitMap hits, Rect area, ScanJob job, ScanProgress? progress, Hit hover)
    {
        // Everything underneath is unreachable while this is up.
        hits.Seal();

        // Dim rather than cover: the numbers behind stay legible, so it is clear the window is
        // busy rather than replaced.
        p.Fill(area, theme.Background.Mix(theme.Dark ? Colour.Rgb(0x000000) : Colour.Rgb(0xFFFFFF), 0.55));

        var width = Math.Min(area.Width - p.Scale(80), p.Scale(560));

        // Added up from what is drawn below rather than picked: the padding, the title, the
        // roots, four facts, the bar, the path and the button's row. As a constant it was one
        // line short, and the path ran underneath the Stop button - the same mistake as the
        // overview's card height, which was right for one machine's fonts.
        var height = p.Scale(18) + p.Height(FontRole.Title) + p.Scale(2) + p.LineHeight + p.Scale(12)
                     + p.LineHeight * 4 + p.Scale(8)
                     + p.Scale(8) + p.Scale(6)
                     + p.Height(FontRole.Small) + p.Scale(6)
                     + p.LineHeight + p.Scale(26);

        var card = new Rect(
            area.X + (area.Width - width) / 2,
            area.Y + (area.Height - height) / 2,
            width, height);

        p.Fill(card, theme.Surface);
        p.Fill(card.TakeTop(p.Scale(3)), theme.Accent);

        var inner = card.Deflate(p.Scale(22), p.Scale(18));

        var title = inner.TakeTop(p.Height(FontRole.Title));
        p.Text(title, job.Cancelling ? "Stopping" : "Scanning", theme.Text, FontRole.Title);

        var roots = inner.DropTop(title.Height + p.Scale(2)).TakeTop(p.LineHeight);
        p.Text(roots, string.Join("  ", job.Roots), theme.Dim);

        var body = inner.DropTop(title.Height + p.Scale(2) + roots.Height + p.Scale(12));

        // The elapsed time comes from the scan itself where it can, so a paused or slow
        // reporter does not make the clock disagree with the counters.
        var elapsed = progress?.Elapsed ?? DateTime.UtcNow - job.Started;

        var facts = new (string, string)[]
        {
            ("entries", $"{progress?.Entries ?? 0:N0}"),
            ("bytes", SizeFormat.Bytes(progress?.Bytes ?? 0)),
            ("unreadable", $"{progress?.Errors ?? 0:N0}"),
            ("elapsed", $"{elapsed.TotalSeconds:0} s"),
        };

        foreach (var (key, value) in facts)
        {
            var line = body.TakeTop(p.LineHeight);
            p.Text(line, key, theme.Dim);
            p.Text(line, value, theme.Text, FontRole.Body, Align.Right);
            body = body.DropTop(p.LineHeight);
        }

        body = body.DropTop(p.Scale(8));

        if (progress?.Fraction is { } fraction)
        {
            Draw.Bar(p, theme, body.TakeTop(p.Scale(8)), fraction, theme.Accent);
            body = body.DropTop(p.Scale(8) + p.Scale(6));
        }

        // The directory being read: the one line that shows the scan is alive and where it is.
        if (progress?.CurrentPath is { Length: > 0 } path)
        {
            p.Text(body.TakeTop(p.Height(FontRole.Small)), path, theme.Dim, FontRole.Small,
                Align.Left, middleEllipsis: true);
        }

        var button = card.TakeBottom(p.LineHeight + p.Scale(26))
            .Deflate(p.Scale(22), p.Scale(9))
            .TakeRight(p.Scale(130));

        // Primary, because a quiet button on a card of the same colour is invisible until it is
        // hovered - and the only action on this card should not have to be found.
        Draw.Button(p, theme, hits, button,
            job.Cancelling ? "Stopping…" : "Stop", (int)GuiAction.Scan,
            hover == new Hit(HitKind.Button, (int)GuiAction.Scan),
            enabled: !job.Cancelling, primary: true);

        if (job.Cancelling)
        {
            p.Text(card.TakeBottom(p.LineHeight + p.Scale(26)).Deflate(p.Scale(22), p.Scale(9)),
                "keeping what has been read so far", theme.Dim, FontRole.Small);
        }
    }
}
