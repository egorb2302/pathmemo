using PathMemo.Tui.Terminal;
using Xunit;

namespace PathMemo.Tests;

/// <summary>
/// The rendering primitives (README sections 14.4, 18.1). Pure functions over strings, so
/// they are exactly what unit tests are for (README section 22.1).
/// </summary>
public class TerminalTests
{
    [Fact]
    public void Ascii_is_one_column_per_character()
    {
        Assert.Equal(11, TextWidth.Of("node_modules"[..11]));
        Assert.Equal(0, TextWidth.Of(""));
    }

    [Fact]
    public void Cjk_and_emoji_take_two_columns()
    {
        Assert.Equal(6, TextWidth.Of("日本語"));       // Japanese, three ideographs
        Assert.Equal(2, TextWidth.Of("\U0001F680"));               // rocket
        Assert.Equal(10, TextWidth.Of("日本語.mp4"));  // three ideographs plus ".mp4"
    }

    [Fact]
    public void Combining_marks_add_nothing()
    {
        Assert.Equal(1, TextWidth.Of("é"));                  // e + combining acute
        Assert.Equal(2, TextWidth.Of("\U0001F60A️"));         // emoji + variation selector
    }

    [Fact]
    public void Fit_produces_exactly_the_asked_width()
    {
        Assert.Equal(10, TextWidth.Of(TextWidth.Fit("short", 10)));
        Assert.Equal(10, TextWidth.Of(TextWidth.Fit("a-very-long-directory-name", 10)));

        // The interesting case: cutting a double-width name must not land half a cell
        // over, which is what breaks the column for every row below it.
        Assert.Equal(7, TextWidth.Of(TextWidth.Fit("日本語日本", 7)));
    }

    [Fact]
    public void Fit_marks_what_it_cut()
    {
        Assert.EndsWith("~", TextWidth.Fit("a-very-long-directory-name", 10).TrimEnd());
        Assert.DoesNotContain('~', TextWidth.Fit("short", 10));
    }

    [Fact]
    public void Bidi_override_is_removed()
    {
        // Renders as "annexe.txt" in a terminal that honours the override, while the real
        // extension is .exe (README section 14.4, threat T10).
        const string spoof = "annexe‮txt.exe";
        var clean = Sanitizer.Clean(spoof);

        Assert.DoesNotContain('‮', clean);
        Assert.EndsWith(".exe", clean);
        Assert.True(Sanitizer.NeedsCleaning(spoof));
    }

    [Fact]
    public void Control_characters_become_visible_dots()
    {
        var clean = Sanitizer.Clean("log[31mred");

        Assert.Equal("log··[31mred", clean);
        Assert.Equal(TextWidth.Of(clean), clean.Length);
    }

    [Fact]
    public void Zero_width_characters_are_dropped()
    {
        Assert.Equal("invoice.pdf", Sanitizer.Clean("invoice​.pdf﻿"));
    }

    [Fact]
    public void Ordinary_names_are_returned_unchanged()
    {
        const string name = "Program Files (x86)";
        Assert.Same(name, Sanitizer.Clean(name));
        Assert.False(Sanitizer.NeedsCleaning("日本語.txt"));
    }

    [Fact]
    public void A_row_is_clipped_to_the_screen_width()
    {
        var screen = Frame(20, 3);

        screen.Begin();
        screen.Put(0, screen.Row(0).Add("0123456789012345678901234567890"));
        screen.Flush();

        Assert.Equal(20, screen.TextAt(0).Length);
    }

    [Fact]
    public void Only_changed_rows_are_written()
    {
        var output = new StringWriter();
        var screen = new Screen(output, color: false);
        screen.Resize(30, 4);

        screen.Begin();
        for (var y = 0; y < 4; y++) screen.Put(y, screen.Row(y).Add($"row {y}"));
        screen.Flush();

        var first = output.ToString();
        Assert.Contains("row 3", first);

        // Same frame again: nothing at all should reach the terminal.
        output.GetStringBuilder().Clear();
        screen.Begin();
        for (var y = 0; y < 4; y++) screen.Put(y, screen.Row(y).Add($"row {y}"));
        screen.Flush();

        Assert.Equal("", output.ToString());

        // One row changes: only that row is repositioned and rewritten.
        screen.Begin();
        for (var y = 0; y < 4; y++) screen.Put(y, screen.Row(y).Add(y == 2 ? "moved" : $"row {y}"));
        screen.Flush();

        var second = output.ToString();
        Assert.Contains("moved", second);
        Assert.DoesNotContain("row 1", second);
        Assert.Contains("[3;1H", second);          // row 3 of the terminal, 1-based
    }

    [Fact]
    public void A_row_is_erased_before_it_is_written_not_after()
    {
        // Writing the last column of a row leaves the cursor sitting on it in the
        // pending-wrap state, so an erase-to-end-of-line issued afterwards deletes the
        // character just written. Measured in conhost: every full-width row lost its
        // last character.
        var output = new StringWriter();
        var screen = new Screen(output, color: false);
        screen.Resize(8, 1);

        screen.Begin();
        screen.Put(0, screen.Row(0).Add("12345678"));
        screen.Flush();

        Assert.Equal("[1;1H[K12345678[1;1H", output.ToString());
    }

    [Fact]
    public void Invalidate_forces_a_full_repaint()
    {
        var output = new StringWriter();
        var screen = new Screen(output, color: false);
        screen.Resize(30, 2);

        screen.Begin();
        screen.Put(0, screen.Row(0).Add("hello"));
        screen.Flush();

        output.GetStringBuilder().Clear();
        screen.Invalidate();

        screen.Begin();
        screen.Put(0, screen.Row(0).Add("hello"));
        screen.Flush();

        Assert.Contains("hello", output.ToString());
    }

    [Fact]
    public void Colour_is_dropped_when_the_terminal_asks_for_none()
    {
        var output = new StringWriter();
        var screen = new Screen(output, color: false);
        screen.Resize(30, 1);

        screen.Begin();
        screen.Put(0, screen.Row(0).Add("warning", Style.Warning));
        screen.Flush();

        Assert.DoesNotContain("[33m", output.ToString());
    }

    [Fact]
    public void A_sparkline_shows_movement_rather_than_absolute_size()
    {
        // Values only 2% apart: scaled from zero this would be a flat line, which is the
        // opposite of what the row exists to show.
        var spark = Draw.Sparkline([400_000, 404_000, 408_000], 8);

        Assert.Equal(3, spark.Length);
        Assert.NotEqual(spark[0], spark[2]);
        Assert.Equal(1, TextWidth.Of(spark[..1]));
    }

    private static Screen Frame(int width, int height)
    {
        var screen = new Screen(new StringWriter(), color: false);
        screen.Resize(width, height);
        return screen;
    }
}
