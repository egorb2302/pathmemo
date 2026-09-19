using System.Diagnostics;
using PathMemo.Cli.Commands;
using PathMemo.Config;
using PathMemo.Tui;
using PathMemo.Tui.Screens;
using PathMemo.Tui.Terminal;
using Xunit;

namespace PathMemo.Tests;

/// <summary>
/// Screen 4 driven by keystrokes against a synthetic snapshot (README sections 7.3, 14.1).
/// </summary>
/// <remarks>
/// The rules run on a background thread when a snapshot is loaded, so every test here
/// waits for that pass before pressing anything - which is also the assertion that the
/// screen is usable while it is still running, since the first frame is drawn before the
/// wait (README section 20).
/// </remarks>
public sealed class ReclaimScreenTests : IDisposable
{
    private static readonly DateTime When = new(2026, 9, 18, 3, 0, 0, DateTimeKind.Utc);

    private readonly string _dataDirectory = Path.Combine(
        Path.GetTempPath(), "pathmemo-reclaimui-" + Guid.NewGuid().ToString("N")[..12]);

    public ReclaimScreenTests()
    {
        Directory.CreateDirectory(_dataDirectory);
        TestStore.Use(_dataDirectory);
        AppConfig.Reset();
    }

    public void Dispose()
    {
        try { Directory.Delete(_dataDirectory, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException) { }

        AppConfig.Reset();
    }

    [Fact]
    public void Lists_the_rules_that_matched_largest_first()
    {
        var (view, session, screen) = Open(Sample());

        Assert.Contains("dev.node_modules", Rows(screen)[0], StringComparison.Ordinal);
        Assert.Contains("safe redownload", Rows(screen)[0], StringComparison.Ordinal);
        Assert.Contains("user.large_media", Rows(screen)[1], StringComparison.Ordinal);
        Assert.Contains("dev.dotnet_artifacts", Rows(screen)[2], StringComparison.Ordinal);
        Assert.Contains("Reclaimable", screen.TextAt(2), StringComparison.Ordinal);
    }

    [Fact]
    public void Entering_a_rule_lists_the_paths_behind_it()
    {
        var (view, session, screen) = Open(Sample());

        Press(view, session, 'l');
        Render(view, session, screen);

        Assert.Contains("dev.node_modules", screen.TextAt(0), StringComparison.Ordinal);
        Assert.Contains("node_modules", Rows(screen)[0], StringComparison.Ordinal);

        Press(view, session, 'h');
        Render(view, session, screen);

        Assert.Contains("dev.node_modules", Rows(screen)[0], StringComparison.Ordinal);
    }

    [Fact]
    public void The_risk_ceiling_hides_and_shows_the_rules_above_it()
    {
        var (view, session, screen) = Open(Sample());

        Assert.Contains("user.large_media", Frame(screen), StringComparison.Ordinal);

        Press(view, session, 't');                       // caution -> danger
        Press(view, session, 't');                       // danger  -> safe
        Render(view, session, screen);

        Assert.DoesNotContain("user.large_media", Frame(screen), StringComparison.Ordinal);
        Assert.Contains("dev.node_modules", Frame(screen), StringComparison.Ordinal);
    }

    [Fact]
    public void A_rule_pathmemo_will_not_act_on_says_so_instead_of_offering_a_dialog()
    {
        var tree = new TestTree()
            .File(@"C:\work\repo\.git\objects\pack\pack-1.pack", 900L << 20);

        var (view, session, screen) = Open(tree);

        Assert.Contains("dev.git_gc", Frame(screen), StringComparison.Ordinal);

        Press(view, session, 'd');
        Render(view, session, screen);

        Assert.Null(session.Modal);
        Assert.Contains("git gc", Frame(screen), StringComparison.Ordinal);
    }

    [Fact]
    public void Deleting_a_rule_opens_the_dialog_the_guard_has_already_had_its_say_in()
    {
        // The paths are synthetic, so every one of them is refused - which is the point:
        // the screen hands its list to the same guarded plan the tree screen does.
        var (view, session, screen) = Open(Sample());

        Press(view, session, 'd');
        Render(view, session, screen);

        Assert.NotNull(session.Modal);
        Assert.Contains("Delete", Frame(screen), StringComparison.Ordinal);
    }

    [Fact]
    public void Keeping_at_the_rule_level_disables_the_rule_in_the_configuration()
    {
        var (view, session, screen) = Open(Sample());

        Press(view, session, 'K');

        Assert.Contains("dev.node_modules", AppConfig.Load(AppPaths.ConfigPath).Rules.Disabled);
    }

    [Fact]
    public void Keeping_a_single_path_adds_it_to_the_keep_list()
    {
        var (view, session, screen) = Open(Sample());

        Press(view, session, 'l');                       // into the rule's paths
        Press(view, session, 'K');

        var keep = Assert.Single(AppConfig.Load(AppPaths.ConfigPath).Protect.Keep);
        Assert.EndsWith("node_modules", keep, StringComparison.Ordinal);
    }

    [Fact]
    public void Marks_survive_moving_between_the_two_levels()
    {
        var (view, session, screen) = Open(Sample());

        Press(view, session, 'l');                       // into dev.node_modules
        Press(view, session, 'x');                       // mark the first path
        Press(view, session, 'h');                       // back to the rules
        Render(view, session, screen);

        Assert.Contains("1 marked", screen.TextAt(2), StringComparison.Ordinal);
    }

    [Fact]
    public void A_scan_with_nothing_to_reclaim_says_so_rather_than_drawing_an_empty_table()
    {
        var tree = new TestTree().File(@"C:\photos\holiday.jpg", 4L << 20);
        var (_, _, screen) = Open(tree);

        Assert.Contains("No rule matched", Frame(screen), StringComparison.Ordinal);
    }

    private static TestTree Sample() => new TestTree()
        .File(@"C:\work\app\node_modules\react\index.js", 4L << 30)
        .File(@"C:\work\other\node_modules\vue\index.js", 2L << 30)
        .File(@"C:\work\app\obj\Debug\app.dll", 900L << 20)
        .File(@"C:\work\app\src\main.ts", 4096)
        .File(@"C:\images\backup.vhdx", 1500L << 20);

    private (ReclaimScreen View, TuiSession Session, Screen Screen) Open(
        TestTree tree, int width = 110, int height = 24)
    {
        var session = new TuiSession { Report = new StatusReport { Volumes = [] } };
        session.Load(tree.Snapshot(When), scanId: 7);

        var screen = new Screen(new StringWriter(), color: false);
        screen.Resize(width, height);

        var view = new ReclaimScreen();

        // The frame before the rules have finished is a legal frame, and drawing it is
        // what keeps the screen responsive; assert it says what it is doing.
        Render(view, session, screen);

        Wait(session);
        Render(view, session, screen);

        return (view, session, screen);
    }

    /// <summary>Waits for the background rule pass, with a budget rather than forever.</summary>
    private static void Wait(TuiSession session)
    {
        var clock = Stopwatch.StartNew();
        while (!session.ReclaimReady && clock.Elapsed < TimeSpan.FromSeconds(10)) Thread.Sleep(5);

        Assert.True(session.ReclaimReady, "the rules did not finish within ten seconds");
    }

    private static void Render(ReclaimScreen view, TuiSession session, Screen screen)
    {
        screen.Begin();
        view.Render(screen, session);
        session.Modal?.Render(screen, session);
        screen.Flush();
    }

    private static void Press(ReclaimScreen view, TuiSession session, char c) =>
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
            if (text.Trim().Length == 0) break;
            rows.Add(text);
        }

        return rows;
    }
}
