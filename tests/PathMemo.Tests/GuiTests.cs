using PathMemo.Gui;
using PathMemo.Gui.Controls;
using PathMemo.Gui.Render;
using PathMemo.Gui.Views;
using Xunit;

namespace PathMemo.Tests;

/// <summary>
/// The window's own logic, without a window (README section 24.6).
/// </summary>
/// <remarks>
/// Layout arithmetic, the treemap, virtualisation and hit testing are pure functions, which is
/// the whole reason the GUI was built this way round: everything that can be wrong about a
/// frame can be asserted here, and only the pixels themselves need a screenshot.
/// </remarks>
public class GuiTests
{
    // ------------------------------------------------------------------ Rect

    [Fact]
    public void Taking_from_an_edge_does_not_move_the_rest()
    {
        var area = new Rect(10, 20, 100, 200);

        Assert.Equal(new Rect(10, 20, 100, 30), area.TakeTop(30));
        Assert.Equal(new Rect(10, 50, 100, 170), area.DropTop(30));
        Assert.Equal(new Rect(10, 190, 100, 30), area.TakeBottom(30));
        Assert.Equal(new Rect(90, 20, 20, 200), area.TakeRight(20));
    }

    [Fact]
    public void Taking_more_than_there_is_takes_what_there_is()
    {
        var area = new Rect(0, 0, 40, 10);

        Assert.Equal(area, area.TakeTop(999));
        Assert.True(area.DropTop(999).IsEmpty);
        Assert.Equal(40, area.TakeRight(999).Width);
    }

    [Fact]
    public void Cutting_one_area_twice_gives_two_independent_answers()
    {
        // The property that makes a value type the right choice: a view can hand the same
        // rectangle to two children and neither sees the other's cut.
        var area = new Rect(0, 0, 100, 100);

        var left = area.TakeLeft(40);
        var right = area.TakeRight(40);

        Assert.Equal(0, left.X);
        Assert.Equal(60, right.X);
        Assert.Equal(new Rect(0, 0, 100, 100), area);
    }

    [Fact]
    public void Rectangles_that_do_not_meet_intersect_to_nothing()
    {
        var a = new Rect(0, 0, 10, 10);
        var b = new Rect(20, 20, 10, 10);

        Assert.True(a.Intersect(b).IsEmpty);
        Assert.Equal(new Rect(5, 5, 5, 5), a.Intersect(new Rect(5, 5, 50, 50)));
    }

    // ------------------------------------------------------------------ Colour

    [Fact]
    public void A_colour_is_stored_the_way_gdi_wants_it_not_the_way_it_is_written()
    {
        // COLORREF is 0x00BBGGRR. Pure red written as 0xFF0000 has to reach GDI as 0x0000FF,
        // and getting this backwards produces a window that looks deliberately blue.
        Assert.Equal(0x0000FFu, Colour.Rgb(0xFF0000).Ref);
        Assert.Equal(0x00FF00u, Colour.Rgb(0x00FF00).Ref);
        Assert.Equal(0xFF0000u, Colour.Rgb(0x0000FF).Ref);

        var colour = Colour.Rgb(0x123456);
        Assert.Equal(0x12, colour.R);
        Assert.Equal(0x34, colour.G);
        Assert.Equal(0x56, colour.B);
    }

    [Fact]
    public void Mixing_ends_where_it_is_told_and_stays_in_range()
    {
        var black = Colour.Rgb(0x000000);
        var white = Colour.Rgb(0xFFFFFF);

        Assert.Equal(black, black.Mix(white, 0));
        Assert.Equal(white, black.Mix(white, 1));
        Assert.Equal(black, black.Mix(white, -5));
        Assert.Equal(white, black.Mix(white, 5));

        var middle = black.Mix(white, 0.5);
        Assert.InRange(middle.R, 126, 129);
    }

    [Fact]
    public void Every_category_has_its_own_colour_in_both_palettes()
    {
        // Two categories sharing a hue would make the map say two things with one colour,
        // which is worse than not colouring it at all.
        foreach (var theme in new[] { Theme.Dusk, Theme.Day })
        {
            var categories = Enum.GetValues<PathMemo.Analysis.FileCategory>();
            var seen = new HashSet<uint>();

            foreach (var category in categories)
            {
                var colour = theme.Category(category);

                Assert.NotEqual(theme.Background, colour);
                Assert.True(seen.Add(colour.Ref), $"{category} repeats a colour in {(theme.Dark ? "dusk" : "day")}");
            }

            Assert.Equal(categories.Length, seen.Count);
        }
    }

    // ------------------------------------------------------------------ Treemap

    [Fact]
    public void Every_block_stays_inside_the_frame()
    {
        var area = new Rect(10, 20, 400, 300);
        long[] sizes = [500, 300, 120, 40, 20, 12, 5, 3];

        foreach (var block in Treemap.Layout(sizes, area))
        {
            Assert.True(block.Area.X >= area.X, $"{block.Area} left of {area}");
            Assert.True(block.Area.Y >= area.Y, $"{block.Area} above {area}");
            Assert.True(block.Area.Right <= area.Right, $"{block.Area} right of {area}");
            Assert.True(block.Area.Bottom <= area.Bottom, $"{block.Area} below {area}");
        }
    }

    [Fact]
    public void A_blocks_area_is_its_share_of_the_space()
    {
        // The one property a treemap has to have: area is proportional to value. Without it
        // the picture is decoration.
        var area = new Rect(0, 0, 600, 400);
        long[] sizes = [400, 300, 200, 100];

        var blocks = Treemap.Layout(sizes, area);
        Assert.Equal(4, blocks.Count);

        double total = 600 * 400;
        long values = 1000;

        foreach (var block in blocks)
        {
            var expected = sizes[block.Index] / (double)values;
            var actual = block.Area.Width * (double)block.Area.Height / total;

            // Generous: the layout rounds to whole pixels and the frame is deflated a little.
            Assert.InRange(actual, expected - 0.06, expected + 0.06);
        }
    }

    [Fact]
    public void Blocks_do_not_overlap()
    {
        var blocks = Treemap.Layout([900, 500, 260, 140, 90, 50, 25, 10], new Rect(0, 0, 500, 320));

        for (var i = 0; i < blocks.Count; i++)
        {
            for (var j = i + 1; j < blocks.Count; j++)
            {
                var overlap = blocks[i].Area.Intersect(blocks[j].Area);
                Assert.True(overlap.IsEmpty,
                    $"{blocks[i].Area} overlaps {blocks[j].Area} by {overlap}");
            }
        }
    }

    [Fact]
    public void Squarifying_beats_slicing_on_aspect_ratio()
    {
        // Twenty equal items in a wide frame. Sliced, each would be 30x400 - a ratio of 13.
        // Squarified they should be close to square, which is the entire point of the
        // algorithm and the difference between a map you can click and a barcode.
        var sizes = new long[20];
        Array.Fill(sizes, 100L);

        var blocks = Treemap.Layout(sizes, new Rect(0, 0, 600, 400));
        Assert.NotEmpty(blocks);

        foreach (var block in blocks)
        {
            var ratio = block.Area.Width / (double)block.Area.Height;
            Assert.InRange(ratio, 0.25, 4.0);
        }
    }

    [Fact]
    public void Nothing_to_show_lays_out_nothing()
    {
        Assert.Empty(Treemap.Layout([], new Rect(0, 0, 100, 100)));
        Assert.Empty(Treemap.Layout([0, 0, 0], new Rect(0, 0, 100, 100)));
        Assert.Empty(Treemap.Layout([10, 5], new Rect(0, 0, 1, 1)));
    }

    // ------------------------------------------------------------------ RowList

    [Fact]
    public void Only_the_visible_rows_are_drawn()
    {
        var list = new RowList { Count = 200_000 };
        var painter = new RecordingPainter();
        var drawn = 0;

        list.Paint(painter, Theme.Dusk, new HitMap(), new Rect(0, 0, 400, 270),
            (_, _, _, _, _) => drawn++, Hit.None);

        // 270 pixels of 27-pixel rows is ten, plus the partially visible one below.
        Assert.InRange(drawn, 10, 12);
    }

    [Fact]
    public void The_cursor_pulls_the_view_with_it()
    {
        var list = new RowList { Count = 100 };
        var painter = new RecordingPainter();
        var area = new Rect(0, 0, 400, 270);

        list.Paint(painter, Theme.Dusk, new HitMap(), area, (_, _, _, _, _) => { }, Hit.None);

        list.Select(50);
        Assert.Equal(50, list.Selected);
        Assert.InRange(list.First, 41, 50);

        list.Select(0);
        Assert.Equal(0, list.First);
    }

    [Fact]
    public void The_cursor_cannot_leave_the_list()
    {
        var list = new RowList { Count = 5 };

        list.Select(999);
        Assert.Equal(4, list.Selected);

        list.Select(-999);
        Assert.Equal(0, list.Selected);
    }

    [Fact]
    public void An_empty_list_has_no_cursor_to_go_wrong()
    {
        var list = new RowList { Count = 0 };
        var painter = new RecordingPainter();

        list.Paint(painter, Theme.Dusk, new HitMap(), new Rect(0, 0, 100, 100),
            (_, _, _, _, _) => Assert.Fail("nothing should be drawn"), Hit.None);

        Assert.Equal(0, list.Selected);
    }

    [Fact]
    public void A_wheel_notch_moves_three_rows_and_stops_at_the_ends()
    {
        var list = new RowList { Count = 100 };
        var painter = new RecordingPainter();
        var area = new Rect(0, 0, 400, 270);

        list.Paint(painter, Theme.Dusk, new HitMap(), area, (_, _, _, _, _) => { }, Hit.None);

        Assert.False(list.Wheel(1));            // already at the top
        Assert.True(list.Wheel(-1));
        Assert.Equal(3, list.First);

        Assert.True(list.Wheel(-9999));
        var bottom = list.First;
        Assert.False(list.Wheel(-1));
        Assert.Equal(bottom, list.First);
    }

    // ------------------------------------------------------------------ HitMap

    [Fact]
    public void What_was_drawn_last_is_what_is_hit()
    {
        var hits = new HitMap();

        hits.Add(new Rect(0, 0, 100, 100), HitKind.Row, 1);
        hits.Add(new Rect(10, 10, 20, 20), HitKind.Button, 7);

        Assert.Equal(new Hit(HitKind.Button, 7), hits.At(15, 15));
        Assert.Equal(new Hit(HitKind.Row, 1), hits.At(50, 50));
        Assert.Equal(Hit.None, hits.At(500, 500));
    }

    [Fact]
    public void A_sealed_layer_hides_everything_under_it()
    {
        // This is what stops a click aimed at a scan dialog from reaching the list behind it.
        var hits = new HitMap();

        hits.Add(new Rect(0, 0, 100, 100), HitKind.Row, 3);
        hits.Seal();

        Assert.Equal(Hit.None, hits.At(50, 50));

        hits.Add(new Rect(40, 40, 20, 20), HitKind.Button, 2);
        Assert.Equal(new Hit(HitKind.Button, 2), hits.At(50, 50));
    }

    [Fact]
    public void Clearing_also_lifts_the_seal()
    {
        var hits = new HitMap();

        hits.Add(new Rect(0, 0, 10, 10), HitKind.Row, 0);
        hits.Seal();
        hits.Clear();
        hits.Add(new Rect(0, 0, 10, 10), HitKind.Row, 4);

        Assert.Equal(new Hit(HitKind.Row, 4), hits.At(5, 5));
    }

    [Fact]
    public void An_empty_region_is_not_registered()
    {
        var hits = new HitMap();
        hits.Add(new Rect(10, 10, 0, 50), HitKind.Button, 1);

        Assert.Equal(Hit.None, hits.At(10, 20));
    }

    // ------------------------------------------------------------------ The tree view

    [Fact]
    public void The_tree_draws_a_row_per_child_with_its_size_and_name()
    {
        var view = Loaded(out _);
        var painter = new RecordingPainter();

        view.Paint(painter, Theme.Dusk, new HitMap(), new Rect(0, 0, 800, 400), Hit.None);

        Assert.True(painter.Mentions("big"), string.Join(" | ", painter.AllText));
        Assert.True(painter.Mentions("small"));
        Assert.True(painter.Said("size"));
        Assert.True(painter.Said("name"));
    }

    [Fact]
    public void A_directory_is_drawn_with_a_separator_and_a_file_is_not()
    {
        var view = Loaded(out _);
        var painter = new RecordingPainter();

        view.Paint(painter, Theme.Dusk, new HitMap(), new Rect(0, 0, 800, 400), Hit.None);

        Assert.True(painter.Said("sub" + Path.DirectorySeparatorChar));
        Assert.True(painter.Said("big.iso"));
    }

    [Fact]
    public void Entering_a_directory_and_coming_back_puts_the_cursor_where_it_was()
    {
        var view = Loaded(out _);
        var painter = new RecordingPainter();
        var area = new Rect(0, 0, 800, 400);

        view.Paint(painter, Theme.Dusk, new HitMap(), area, Hit.None);

        // "sub" is the only directory with children, so find it and go in. Bounded: an
        // unbounded search here would hang the suite rather than fail it if the row ever moved.
        var index = -1;
        for (var i = 0; i < 16; i++)
        {
            if (!view.Click(i, false)) break;
            if (view.Tree.Name(view.Current) == "sub") { index = i; break; }
        }

        Assert.True(index >= 0, "the sample tree should contain a directory called 'sub'");
        Assert.True(view.Enter());
        Assert.True(view.Up());

        Assert.Equal("sub", view.Tree.Name(view.Current));
    }

    [Fact]
    public void A_file_is_not_something_to_enter()
    {
        var view = Loaded(out _);
        var painter = new RecordingPainter();

        view.Paint(painter, Theme.Dusk, new HitMap(), new Rect(0, 0, 800, 400), Hit.None);

        // The biggest child is the file, so the cursor starts on it.
        Assert.Equal("big.iso", view.Tree.Name(view.Current));
        Assert.False(view.Enter());
    }

    [Fact]
    public void The_root_has_nowhere_to_go_up_to()
    {
        var view = Loaded(out _);
        Assert.False(view.Up());
    }

    [Fact]
    public void Forgetting_the_snapshot_leaves_nothing_pointing_into_it()
    {
        // An index into a tree that has been replaced is the worst kind of stale state, so
        // after a scan the view drops everything rather than keeping a cursor.
        var view = Loaded(out _);
        view.Forget();

        Assert.False(view.HasSnapshot);
        Assert.Equal(PathMemo.Snapshots.NodeStore.NoNode, view.Current);

        var painter = new RecordingPainter();
        view.Paint(painter, Theme.Dusk, new HitMap(), new Rect(0, 0, 800, 400), Hit.None);

        Assert.True(painter.Mentions("Run a scan"));
    }

    [Fact]
    public void The_size_mode_cycles_through_all_three_and_back()
    {
        var view = Loaded(out _);

        Assert.Equal("unique", view.ModeName);
        view.CycleMode();
        Assert.Equal("allocated", view.ModeName);
        view.CycleMode();
        Assert.Equal("logical", view.ModeName);
        view.CycleMode();
        Assert.Equal("unique", view.ModeName);
    }

    [Fact]
    public void Rows_are_clipped_to_the_list_so_a_long_name_cannot_escape_it()
    {
        var view = Loaded(out _);
        var painter = new RecordingPainter();
        var area = new Rect(0, 0, 800, 400);

        view.Paint(painter, Theme.Dusk, new HitMap(), area, Hit.None);

        Assert.NotEmpty(painter.ClipHistory);
        foreach (var clip in painter.ClipHistory)
            Assert.True(clip.Intersect(area) == clip, $"{clip} is not inside {area}");
    }

    /// <summary>A tiny snapshot: a big file, a directory with children, and a small file.</summary>
    private static TreeView Loaded(out PathMemo.Snapshots.SnapshotContents snapshot)
    {
        snapshot = new TestTree()
            .File(@"C:\big.iso", 8L << 30)
            .File(@"C:\sub\one.mp4", 2L << 30)
            .File(@"C:\sub\two.txt", 4096)
            .File(@"C:\small.txt", 512)
            .Snapshot(new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc));

        var view = new TreeView { ShowMap = false };
        view.Load(snapshot, 1, null);

        return view;
    }

    // ------------------------------------------------------------------ The scan card

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_scan_card_keeps_the_path_clear_of_its_button(bool withFraction)
    {
        // The card's height was a constant one line short of what is drawn into it, so the
        // directory being read ran underneath Stop. Seen in a screenshot, not in a test,
        // because no test looked: every line on the card has to end above the button's row,
        // with the bar the MFT scanner adds as well as without it.
        var painter = new RecordingPainter();
        var hits = new HitMap();
        var job = new PathMemo.Gui.Work.ScanJob(new RecordingWindow(), ["C:", "D:"]);

        var progress = new PathMemo.Scanning.ScanProgress
        {
            Entries = 182_338,
            Bytes = 46L << 30,
            Errors = 92,
            Elapsed = TimeSpan.FromSeconds(4),
            CurrentPath = @"C:\Windows\WinSxS\wow64_microsoft-windows-accountscontrolexp",
            Fraction = withFraction ? 0.4 : null,
        };

        ScanView.Paint(painter, Theme.Dusk, hits, new Rect(0, 0, 1180, 680), job, progress, Hit.None);

        var stop = painter.Texts.Single(t => t.Text == "Stop").Area;
        var path = painter.Texts.Single(t => t.Text.Contains("WinSxS", StringComparison.Ordinal)).Area;

        Assert.True(path.Bottom <= stop.Y, $"the path ends at {path.Bottom}, the button starts at {stop.Y}");

        foreach (var run in painter.Texts)
            if (run.Text != "Stop") Assert.True(run.Area.Intersect(stop).IsEmpty, $"'{run.Text}' is under the button");
    }
}
