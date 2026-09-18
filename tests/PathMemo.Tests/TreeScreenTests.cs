using System.Diagnostics;
using PathMemo.Cli.Commands;
using PathMemo.Snapshots;
using PathMemo.Tui;
using PathMemo.Tui.Screens;
using PathMemo.Tui.Terminal;
using Xunit;

namespace PathMemo.Tests;

/// <summary>
/// The tree screen driven by keystrokes against a synthetic snapshot (README section 14.2).
/// </summary>
/// <remarks>
/// Frames are inspected as plain text, which is the whole reason <see cref="Screen"/>
/// keeps the unstyled form of every row: asserting on escape sequences would test the
/// colour scheme rather than the behaviour.
/// </remarks>
public class TreeScreenTests
{
    private static readonly DateTime When = new(2026, 9, 18, 3, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Lists_children_largest_first()
    {
        var (view, session, screen) = Open(Sample());

        Assert.Contains("Windows", Rows(screen)[0]);
        Assert.Contains("Users", Rows(screen)[1]);
        Assert.Contains("temp", Rows(screen)[2]);
        Assert.Contains("Total", screen.TextAt(2));
        Assert.Equal(session.Tree.Roots[0], view.Directory);
    }

    [Fact]
    public void Enters_a_directory_and_comes_back_to_the_row_it_left()
    {
        var (view, session, screen) = Open(Sample());

        Press(view, session, 'j');                       // Users
        Press(view, session, 'l');                       // into it
        Render(view, session, screen);

        Assert.Contains("me", Rows(screen)[0]);
        Assert.Contains("Users", screen.TextAt(0));

        Press(view, session, 'h');                       // back out
        Render(view, session, screen);

        Assert.Equal("Users", session.Tree.Name(view.Current));
    }

    [Fact]
    public void The_size_mode_changes_what_the_rows_measure()
    {
        // The second name of a hard-linked file frees nothing when deleted, so unique
        // sizes show it as zero; allocated shows what the name claims (README section 3.1).
        var tree = new TestTree()
            .File(@"C:\links\original.bin", 4L << 30)
            .File(@"C:\links\alias.bin", 4L << 30, NodeFlags.HardlinkAlias);

        var (view, session, screen) = Open(tree);
        Press(view, session, 'l');
        Render(view, session, screen);

        Assert.Contains("0 B", RowWith(screen, "alias.bin"));
        Assert.Contains("unique", screen.TextAt(0));

        Press(view, session, 'm');
        Render(view, session, screen);

        Assert.Contains("4 GB", RowWith(screen, "alias.bin"));
        Assert.Contains("allocated", screen.TextAt(0));
    }

    [Fact]
    public void The_filter_cycles_through_directories_files_and_both()
    {
        var tree = new TestTree()
            .File(@"C:\big\inner.bin", 8L << 30)
            .File(@"C:\loose.bin", 2L << 30);

        var (view, session, screen) = Open(tree);
        Assert.Equal(2, Rows(screen).Count);

        Press(view, session, 't');                       // directories only
        Render(view, session, screen);
        Assert.Single(Rows(screen));
        Assert.Contains("big", Rows(screen)[0]);

        Press(view, session, 't');                       // files only
        Render(view, session, screen);
        Assert.Single(Rows(screen));
        Assert.Contains("loose.bin", Rows(screen)[0]);

        Press(view, session, 't');                       // both again
        Render(view, session, screen);
        Assert.Equal(2, Rows(screen).Count);
    }

    [Fact]
    public void Search_jumps_into_the_directory_that_holds_the_match()
    {
        var tree = new TestTree()
            .File(@"C:\Users\me\projects\app\node_modules\big.js", 900L << 20)
            .File(@"C:\Windows\System32\kernel32.dll", 2L << 20);

        var (view, session, screen) = Open(tree);

        Press(view, session, '/');
        foreach (var c in "node_mod") Press(view, session, c);
        Key(view, session, new TuiKey(ConsoleKey.Enter, '\r', false, false, false));
        Render(view, session, screen);

        Assert.Equal("node_modules", session.Tree.Name(view.Current));
        Assert.Contains("app", screen.TextAt(0));
        Assert.Contains("match 1 of 1", screen.TextAt(screen.Height - 2));
    }

    [Fact]
    public void Search_that_matches_nothing_says_so_and_stays_put()
    {
        var (view, session, screen) = Open(Sample());
        var before = view.Directory;

        Press(view, session, '/');
        foreach (var c in "zzz") Press(view, session, c);
        Key(view, session, new TuiKey(ConsoleKey.Enter, '\r', false, false, false));
        Render(view, session, screen);

        Assert.Equal(before, view.Directory);
        Assert.Contains("nothing matching", screen.TextAt(screen.Height - 2));
    }

    [Fact]
    public void Marks_are_counted_and_cleared()
    {
        var (view, session, screen) = Open(Sample());

        Press(view, session, 'x');
        Press(view, session, 'x');
        Render(view, session, screen);

        Assert.Equal(2, session.Marks.Count);
        Assert.Contains("2 marked", screen.TextAt(2));

        Press(view, session, 'X');
        Render(view, session, screen);

        Assert.Empty(session.Marks);
    }

    [Fact]
    public void Deleting_opens_a_dialog_that_the_guard_has_already_had_its_say_in()
    {
        // The first row of the sample tree is C:\Windows, which exists on the machine
        // running this test and is protected. The dialog is built from a real plan, so it
        // reports the refusal rather than offering a button that would fail
        // (README sections 9.3, 14.3).
        var (view, session, screen) = Open(Sample());

        Press(view, session, 'x');
        Press(view, session, 'd');
        Render(view, session, screen);

        Assert.IsType<Tui.Dialogs.DeleteDialog>(session.Modal);
        Assert.Contains("Nothing here can be deleted", Frame(screen), StringComparison.Ordinal);
        Assert.Contains("the Windows directory", Frame(screen), StringComparison.Ordinal);
    }

    /// <summary>The whole frame as one string, for assertions about panels.</summary>
    private static string Frame(Screen screen) =>
        string.Join('\n', Enumerable.Range(0, screen.Height).Select(screen.TextAt));

    [Fact]
    public void Executables_are_refused_without_a_dialog()
    {
        var tree = new TestTree().File(@"C:\Downloads\invoice.pdf.exe", 400L << 10);
        var (view, session, screen) = Open(tree);

        Press(view, session, 'l');                       // into Downloads
        Press(view, session, 'o');                       // try to open the file
        Render(view, session, screen);

        Assert.Null(session.Modal);
        Assert.Contains("Refusing to launch executable files", screen.TextAt(screen.Height - 2));
    }

    [Fact]
    public void Details_open_on_a_file_and_name_the_facts_a_row_cannot_show()
    {
        var tree = new TestTree().File(@"C:\vm\disk.vhdx", 40L << 30, NodeFlags.Sparse);
        var (view, session, screen) = Open(tree);

        Press(view, session, 'l');
        Press(view, session, 'i');
        Render(view, session, screen);

        Assert.NotNull(session.Modal);

        var text = string.Join('\n', Enumerable.Range(0, screen.Height).Select(screen.TextAt));
        Assert.Contains("logical", text);
        Assert.Contains("sparse", text);
    }

    [Fact]
    public void A_name_with_an_override_or_an_ideograph_keeps_the_table_aligned()
    {
        var tree = new TestTree()
            .File("C:\\media\\annexe\u202Etxt.exe", 3L << 30)
            .File("C:\\media\\\u65e5\u672c\u8a9e\u306e\u52d5\u753b.mp4", 2L << 30)
            .File("C:\\media\\plain.mp4", 1L << 30);

        var (view, session, screen) = Open(tree);
        Press(view, session, 'l');
        Render(view, session, screen);

        for (var y = 0; y < screen.Height; y++)
            Assert.True(TextWidth.Of(screen.TextAt(y)) <= screen.Width,
                $"row {y} is {TextWidth.Of(screen.TextAt(y))} columns wide on an {screen.Width} column screen");

        Assert.DoesNotContain('\u202E', string.Join("", Enumerable.Range(0, screen.Height).Select(screen.TextAt)));
    }

    [Fact]
    public void A_directory_with_two_hundred_thousand_children_opens_quickly_and_draws_one_page()
    {
        var tree = new TestTree().Fill(@"C:\huge", 200_000, i => 1024 + i);

        var (view, session, screen) = Open(tree);

        var clock = Stopwatch.StartNew();
        Press(view, session, 'l');                       // into the 200k directory
        Render(view, session, screen);
        clock.Stop();

        // The budget is 50 ms for the descent (README section 20); the assert is loose
        // enough to survive a loaded CI machine but tight enough to catch the day someone
        // materialises a row per child.
        Assert.True(clock.ElapsedMilliseconds < 400, $"descending took {clock.ElapsedMilliseconds} ms");
        Assert.Equal(screen.Height - 7, Rows(screen).Count);
        Assert.Contains("1 of 200,000", screen.TextAt(screen.Height - 2));

        clock.Restart();
        for (var i = 0; i < 50; i++)
        {
            Press(view, session, 'j');
            Render(view, session, screen);
        }
        clock.Stop();

        Assert.True(clock.ElapsedMilliseconds < 400, $"50 frames took {clock.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void G_and_shift_g_reach_both_ends()
    {
        var tree = new TestTree().Fill(@"C:\many", 40, i => (40 - i) * 1024L);

        var (view, session, screen) = Open(tree);
        Press(view, session, 'l');

        Press(view, session, 'G');
        Render(view, session, screen);
        Assert.Contains("40 of 40", screen.TextAt(screen.Height - 2));

        Press(view, session, 'g');
        Render(view, session, screen);
        Assert.Contains("1 of 40", screen.TextAt(screen.Height - 2));
    }

    [Fact]
    public void Narrow_terminals_drop_the_bar_before_they_drop_the_name()
    {
        var wide = Open(Sample(), 110, 24);
        Render(wide.View, wide.Session, wide.Screen);
        Assert.Contains('\u2588', Rows(wide.Screen)[0]);

        var narrow = Open(Sample(), 80, 24);
        Render(narrow.View, narrow.Session, narrow.Screen);

        Assert.DoesNotContain('\u2588', Rows(narrow.Screen)[0]);
        Assert.Contains("Windows", Rows(narrow.Screen)[0]);
        Assert.Contains("%", Rows(narrow.Screen)[0]);
    }

    [Fact]
    public void A_console_without_a_truetype_font_gets_an_all_ascii_frame()
    {
        // The legacy raster font - what an old profile hands to a double-clicked exe -
        // cannot draw block or box-drawing characters, so the whole frame switches sets.
        try
        {
            Glyphs.Ascii = true;

            var (view, session, screen) = Open(Sample());
            Press(view, session, 'i');                   // a panel, with its border
            Render(view, session, screen);

            for (var y = 0; y < screen.Height; y++)
                foreach (var c in screen.TextAt(y))
                    Assert.True(c < 128, $"row {y} contains U+{(int)c:X4}, which a raster font cannot draw");
        }
        finally
        {
            Glyphs.Ascii = false;
        }
    }

    private static TestTree Sample() => new TestTree()
        .File(@"C:\Windows\WinSxS\big.dll", 12L << 30)
        .File(@"C:\Users\me\video.mp4", 6L << 30)
        .File(@"C:\temp\cache.bin", 1L << 30);

    private static (TreeScreen View, TuiSession Session, Screen Screen) Open(
        TestTree tree, int width = 110, int height = 24)
    {
        var session = new TuiSession { Report = new StatusReport { Volumes = [] } };
        session.Load(tree.Snapshot(When), scanId: 7);

        var screen = new Screen(new StringWriter(), color: false);
        screen.Resize(width, height);

        var view = new TreeScreen();
        view.Enter(session);
        Render(view, session, screen);

        return (view, session, screen);
    }

    private static void Render(TreeScreen view, TuiSession session, Screen screen)
    {
        screen.Begin();
        view.Render(screen, session);
        session.Modal?.Render(screen, session);
        screen.Flush();
    }

    private static void Press(TreeScreen view, TuiSession session, char c) =>
        Key(view, session, new TuiKey(Letter(c), c, false, char.IsUpper(c), false));

    private static void Key(TreeScreen view, TuiSession session, TuiKey key) =>
        view.HandleKey(key, session);

    private static ConsoleKey Letter(char c) =>
        char.IsAsciiLetter(c) ? Enum.Parse<ConsoleKey>(char.ToUpperInvariant(c).ToString()) : 0;

    private static string RowWith(Screen screen, string name) =>
        Rows(screen).FirstOrDefault(row => row.Contains(name, StringComparison.Ordinal))
        ?? throw new Xunit.Sdk.XunitException($"no row mentions {name}");

    /// <summary>The entry rows of the current frame, without the blank tail.</summary>
    private static List<string> Rows(Screen screen)
    {
        var rows = new List<string>();
        for (var y = 4; y <= screen.Height - 4; y++)
        {
            var text = screen.TextAt(y);
            if (text.Trim().Length == 0) break;
            rows.Add(text);
        }

        return rows;
    }
}
