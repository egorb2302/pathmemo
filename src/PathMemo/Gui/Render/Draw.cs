using PathMemo.Gui.Controls;

namespace PathMemo.Gui.Render;

/// <summary>
/// The handful of compound shapes more than one view draws (README section 24.2).
/// </summary>
/// <remarks>
/// Static functions over <see cref="IPainter"/> rather than control objects. A button here is
/// a rectangle, a label and an entry in the hit map - it owns no state, because the state that
/// would make it an object (hovered, pressed) belongs to the window, which knows where the
/// pointer is. One button object per toolbar entry would be three fields and a constructor
/// for something that is drawn in four lines.
/// </remarks>
internal static class Draw
{
    /// <summary>A one-pixel hairline along the bottom edge of <paramref name="area"/>.</summary>
    internal static void RuleBelow(IPainter p, Theme theme, Rect area) =>
        p.Fill(area.TakeBottom(1), theme.Border);

    internal static void RuleRight(IPainter p, Theme theme, Rect area) =>
        p.Fill(area.TakeRight(1), theme.Border);

    /// <summary>
    /// A proportion bar: the filled part, then the track. Never a border - at 6 pixels high a
    /// one-pixel outline is a third of the shape.
    /// </summary>
    internal static void Bar(IPainter p, Theme theme, Rect area, double fraction, Colour fill)
    {
        if (area.IsEmpty) return;

        p.Fill(area, theme.BarTrack);

        var width = (int)Math.Round(Math.Clamp(fraction, 0, 1) * area.Width);

        // A non-zero share must be visible: rounding 0.3% to nothing says "empty", which is a
        // different claim from "small". One pixel is the smallest thing that is not a lie.
        if (fraction > 0 && width == 0) width = 1;

        p.Fill(area.WithWidth(width), fill);
    }

    /// <summary>
    /// A clickable button. <paramref name="primary"/> fills with the accent; the rest are
    /// quiet until hovered, because a toolbar of six equally loud buttons has no first move.
    /// </summary>
    internal static void Button(
        IPainter p, Theme theme, HitMap hits, Rect area, string label, int action,
        bool hovered, bool enabled = true, bool primary = false)
    {
        var background = (primary, enabled, hovered) switch
        {
            (true, true, true) => theme.Accent.Mix(theme.Text, 0.18),
            (true, true, false) => theme.Accent,
            (true, false, _) => theme.Accent.Mix(theme.Background, 0.6),
            (false, true, true) => theme.Hover,
            _ => theme.Surface,
        };

        var foreground = (primary, enabled) switch
        {
            (true, true) => theme.OnAccent,
            (_, false) => theme.Dim.Mix(theme.Background, 0.45),
            _ => theme.Text,
        };

        p.Fill(area, background);
        p.Text(area, label, foreground, FontRole.Body, Align.Centre);

        if (enabled) hits.Add(area, HitKind.Button, action);
    }

    /// <summary>
    /// A view tab: text with an accent underline when active. No box, no gradient - the
    /// underline is the whole of the affordance and it survives both palettes.
    /// </summary>
    internal static void Tab(
        IPainter p, Theme theme, HitMap hits, Rect area, string label, int view,
        bool active, bool hovered)
    {
        if (hovered && !active) p.Fill(area, theme.Hover);

        p.Text(area, label, active ? theme.Text : theme.Dim,
            active ? FontRole.Bold : FontRole.Body, Align.Centre);

        if (active) p.Fill(area.TakeBottom(2), theme.Accent);

        hits.Add(area, HitKind.Tab, view);
    }

    /// <summary>
    /// A small word on a tinted plate: the row badges of README section 14.2 - <c>link</c>,
    /// <c>sparse</c>, a reclaim rule.
    /// </summary>
    /// <returns>The width taken, so a caller can lay several out right to left.</returns>
    internal static int Badge(IPainter p, Theme theme, Rect line, int right, string text, Colour tint)
    {
        var padding = 5;
        var width = p.Measure(text, FontRole.Small) + padding * 2;
        var height = p.Height(FontRole.Small) + 2;
        var area = new Rect(right - width, line.Y + (line.Height - height) / 2, width, height);

        p.Fill(area, tint.Mix(theme.Background, 0.75));
        p.Text(area, text, tint.Mix(theme.Text, 0.25), FontRole.Small, Align.Centre);

        return width;
    }

    /// <summary>
    /// A vertical scrollbar, drawn only when there is something to scroll.
    /// </summary>
    /// <remarks>
    /// Always visible while scrollable rather than fading in: this is the only indication of
    /// how much of a 26,000-entry directory is off screen, and a thumb that appears on movement
    /// cannot answer "how far down am I" before the movement starts.
    /// </remarks>
    internal static void Scrollbar(
        IPainter p, Theme theme, HitMap hits, Rect area, int first, int visible, int total)
    {
        if (total <= visible || area.IsEmpty) return;

        p.Fill(area, theme.Surface);
        hits.Add(area, HitKind.Scroll);

        var span = Math.Max(area.Height * visible / total, p.LineHeight);
        var travel = area.Height - span;
        var offset = total - visible <= 0 ? 0 : travel * first / (total - visible);

        p.Fill(new Rect(area.X + 1, area.Y + offset, area.Width - 2, span),
            theme.Dim.Mix(theme.Surface, 0.55));
    }
}
