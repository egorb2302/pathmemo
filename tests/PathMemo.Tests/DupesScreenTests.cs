using PathMemo.Cli.Commands;
using PathMemo.Config;
using PathMemo.Duplicates;
using PathMemo.Storage;
using PathMemo.Tui;
using PathMemo.Tui.Screens;
using PathMemo.Tui.Terminal;
using Xunit;

namespace PathMemo.Tests;

/// <summary>
/// Screen 5 driven by keystrokes against a fabricated run (README sections 8, 14.1).
/// </summary>
/// <remarks>
/// The run is handed to the screen rather than searched for, which is the same thing the
/// screen does with a stored one: it never starts a search by being opened, because a
/// search reads the files themselves (README section 8.5).
/// </remarks>
public sealed class DupesScreenTests : IDisposable
{
    private readonly string _dataDirectory = Path.Combine(
        Path.GetTempPath(), "pathmemo-dupeui-" + Guid.NewGuid().ToString("N")[..12]);

    public DupesScreenTests()
    {
        Directory.CreateDirectory(_dataDirectory);
        AppPaths.Redirect(_dataDirectory);
        AppConfig.Reset();
    }

    public void Dispose()
    {
        try { Directory.Delete(_dataDirectory, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException) { }

        AppConfig.Reset();
    }

    [Fact]
    public void Lists_the_groups_with_the_most_to_give_back_first()
    {
        var (_, _, screen) = Open(Sample());

        Assert.Contains("clip.mp4", Rows(screen)[0], StringComparison.Ordinal);
        Assert.Contains("shot.jpg", Rows(screen)[1], StringComparison.Ordinal);
        Assert.Contains("Recoverable", screen.TextAt(2), StringComparison.Ordinal);
    }

    [Fact]
    public void Opens_with_the_recommendation_already_marked()
    {
        var (_, _, screen) = Open(Sample());

        // Two groups, one spare copy each: the screen's proposal, visible before any key.
        Assert.Contains("2 marked", screen.TextAt(2), StringComparison.Ordinal);
    }

    [Fact]
    public void Entering_a_group_names_the_copy_that_stays_and_the_one_that_goes()
    {
        var (view, session, screen) = Open(Sample());

        Press(view, session, 'l');
        Render(view, session, screen);

        var rows = Rows(screen);
        Assert.Contains("keep", rows[0], StringComparison.Ordinal);
        Assert.Contains("delete", rows[1], StringComparison.Ordinal);
    }

    [Fact]
    public void The_last_unmarked_copy_of_a_group_cannot_be_marked()
    {
        var (view, session, screen) = Open(Sample());

        Press(view, session, 'l');                       // into the first group
        Press(view, session, 'x');                       // mark the keeper too
        Render(view, session, screen);

        Assert.Contains("has to survive", Frame(screen), StringComparison.Ordinal);
    }

    [Fact]
    public void Unmarking_a_copy_and_marking_the_other_is_allowed()
    {
        var (view, session, screen) = Open(Sample());

        Press(view, session, 'l');                       // into the first group
        Press(view, session, 'j');                       // onto the spare copy
        Press(view, session, 'x');                       // unmark it
        Press(view, session, 'k');
        Press(view, session, 'x');                       // now the keeper may go instead
        Render(view, session, screen);

        var rows = Rows(screen);
        Assert.Contains("delete", rows[0], StringComparison.Ordinal);
        Assert.Contains("keep", rows[1], StringComparison.Ordinal);
    }

    [Fact]
    public void A_hard_link_set_is_out_of_the_way_until_t_asks_for_it()
    {
        var (view, session, screen) = Open(Sample());

        Assert.DoesNotContain("package.bin", Frame(screen), StringComparison.Ordinal);
        Assert.Contains("hard-link sets hidden", screen.TextAt(2), StringComparison.Ordinal);

        Press(view, session, 't');
        Render(view, session, screen);

        Assert.Contains("package.bin", Frame(screen), StringComparison.Ordinal);
        Assert.Contains("link", Frame(screen), StringComparison.Ordinal);
    }

    [Fact]
    public void A_hard_link_set_offers_nothing_to_delete()
    {
        var (view, session, screen) = Open(Sample());

        Press(view, session, 't');                       // include the hard-link sets
        Press(view, session, 'X');                       // clear the ordinary marks
        Press(view, session, 'G');                       // onto the set, which frees nothing
        Press(view, session, 'l');
        Press(view, session, 'x');
        Render(view, session, screen);

        Assert.Contains("frees nothing", Frame(screen), StringComparison.Ordinal);
    }

    [Fact]
    public void Deleting_re_checks_the_copies_before_it_offers_a_dialog()
    {
        // The paths are fabricated, so the re-check cannot read them - and that is exactly
        // the case it exists for: no dialog, and a reason (README section 8.4).
        var (view, session, screen) = Open(Sample());

        Press(view, session, 'd');
        Render(view, session, screen);

        Assert.Null(session.Modal);
        Assert.Contains("cannot be read now", Frame(screen), StringComparison.Ordinal);
    }

    [Fact]
    public void Deleting_with_nothing_marked_says_what_marks_are_for()
    {
        var (view, session, screen) = Open(Sample());

        Press(view, session, 'X');
        Press(view, session, 'd');
        Render(view, session, screen);

        Assert.Null(session.Modal);
        Assert.Contains("nothing is marked", Frame(screen), StringComparison.Ordinal);
    }

    [Fact]
    public void Keeping_a_copy_writes_it_into_the_configuration_and_unmarks_it()
    {
        var (view, session, screen) = Open(Sample());

        Press(view, session, 'l');                       // into the first group
        Press(view, session, 'j');                       // onto the spare copy
        Press(view, session, 'K');
        Render(view, session, screen);

        var keep = Assert.Single(AppConfig.Load(AppPaths.ConfigPath).Protect.Keep);
        Assert.EndsWith("clip.mp4", keep, StringComparison.Ordinal);
        Assert.Contains("1 marked", screen.TextAt(2), StringComparison.Ordinal);
    }

    [Fact]
    public void The_screen_picks_up_the_run_the_last_search_stored()
    {
        // The path the TUI actually takes: no search, no snapshot, just the database.
        using (var database = Database.Open(AppPaths.DatabasePath))
            new DupeRepository(database).Save(Sample() with { ScanId = 0 });

        var session = new TuiSession { Report = new StatusReport { Volumes = [] } };
        var screen = new Screen(new StringWriter(), color: false);
        screen.Resize(110, 24);

        var view = new DupesScreen();
        Render(view, session, screen);

        Assert.Contains("clip.mp4", Frame(screen), StringComparison.Ordinal);
        Assert.Contains("Recoverable", screen.TextAt(2), StringComparison.Ordinal);
    }

    [Fact]
    public void Without_a_stored_run_the_screen_says_how_to_start_one()
    {
        var session = new TuiSession { Report = new StatusReport { Volumes = [] } };
        var screen = new Screen(new StringWriter(), color: false);
        screen.Resize(110, 24);

        var view = new DupesScreen();
        Render(view, session, screen);

        Assert.Contains("No duplicate search has been run yet", Frame(screen), StringComparison.Ordinal);
        Assert.Contains("Press r", Frame(screen), StringComparison.Ordinal);
    }

    private static DupeReport Sample()
    {
        var clips = Group(DupeKind.Duplicate, 900L << 20,
            @"D:\video\clip.mp4", @"C:\Users\me\Downloads\clip.mp4");

        var shots = Group(DupeKind.Duplicate, 12L << 20,
            @"C:\photos\shot.jpg", @"C:\Users\me\Downloads\shot.jpg");

        var links = Group(DupeKind.HardlinkSet, 40L << 20,
            @"C:\store\package.bin", @"C:\project\node_modules\package.bin");

        return new DupeReport
        {
            Groups = [clips, shots, links],
            ScanId = 7,
            Algorithm = HashKind.Xxh128,
            MinBytes = 1 << 20,
        };
    }

    private static DupeGroup Group(DupeKind kind, long bytes, params string[] paths) => new()
    {
        Kind = kind,
        Bytes = bytes,
        Hash = "0123456789abcdef0123456789abcdef",
        Files = Keeper.Mark(
            [.. paths.Select(p => new DupeFile { Path = p, Bytes = bytes, Allocated = bytes })], []),
    };

    private static (DupesScreen View, TuiSession Session, Screen Screen) Open(
        DupeReport report, int width = 110, int height = 24)
    {
        var session = new TuiSession { Report = new StatusReport { Volumes = [] } };

        var screen = new Screen(new StringWriter(), color: false);
        screen.Resize(width, height);

        var view = new DupesScreen();
        view.Load(report);

        Render(view, session, screen);
        return (view, session, screen);
    }

    private static void Render(DupesScreen view, TuiSession session, Screen screen)
    {
        screen.Begin();
        view.Render(screen, session);
        session.Modal?.Render(screen, session);
        screen.Flush();
    }

    private static void Press(DupesScreen view, TuiSession session, char c) =>
        view.HandleKey(new TuiKey(Letter(c), c, false, char.IsUpper(c), false), session);

    private static ConsoleKey Letter(char c) =>
        char.IsAsciiLetter(c) ? Enum.Parse<ConsoleKey>(char.ToUpperInvariant(c).ToString()) : 0;

    private static string Frame(Screen screen) =>
        string.Join('\n', Enumerable.Range(0, screen.Height).Select(screen.TextAt));

    private static List<string> Rows(Screen screen)
    {
        var rows = new List<string>();

        for (var y = 4; y <= screen.Height - 4; y++)
        {
            var text = screen.TextAt(y);
            if (text.Trim().Length > 0) rows.Add(text);
        }

        return rows;
    }
}
