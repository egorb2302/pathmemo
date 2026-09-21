using PathMemo.Cli.Commands;
using PathMemo.Config;
using PathMemo.Gui;
using PathMemo.Gui.Render;
using PathMemo.Gui.Work;
using PathMemo.Platform.Native;
using PathMemo.Snapshots;
using Xunit;

namespace PathMemo.Tests;

/// <summary>
/// The window's Reclaim tab, driven through the shell the way a person drives it
/// (README sections 7, 24.3, 24.6).
/// </summary>
/// <remarks>
/// <para>
/// Through the shell rather than against the view alone, because the part that can go wrong
/// is the part between them: the snapshot the tab has to load, the rules that run on a worker
/// and come back as a posted message, and the frame that has to be drawn meanwhile. The Tree
/// tab shipped a crash in exactly that seam (README section 24.6), so this tab's tests start
/// there.
/// </para>
/// <para>
/// The recording window does not deliver posts by itself - nothing pumps it - so every test
/// decides when the rules "arrive". That makes the frame before they do a deterministic thing
/// to assert rather than a race.
/// </para>
/// </remarks>
public sealed class ReclaimViewTests : IDisposable
{
    private static readonly DateTime When = new(2026, 9, 18, 3, 0, 0, DateTimeKind.Utc);

    private readonly string _dataDirectory = Path.Combine(
        Path.GetTempPath(), "pathmemo-reclaimgui-" + Guid.NewGuid().ToString("N")[..12]);

    private readonly RecordingWindow _window = new();
    private readonly GuiShell _shell;

    public ReclaimViewTests()
    {
        Directory.CreateDirectory(_dataDirectory);
        TestStore.Use(_dataDirectory);
        AppConfig.Reset();

        _shell = new GuiShell(_window, StatusReport.Collect());
    }

    public void Dispose()
    {
        try { Directory.Delete(_dataDirectory, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

        AppConfig.Reset();
    }

    [Fact]
    public void With_nothing_scanned_the_tab_says_so_and_stays_open()
    {
        Click(_window.Paint(), "Reclaim", FontRole.Body);

        Assert.Equal(GuiView.Reclaim, _shell.Active);
        Assert.Contains("scan", _shell.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(_window.Paint().Mentions("No snapshot"));

        // And again, from another tab and from itself: the state that fed the Tree tab's
        // recursion is this one, a view selected with nothing behind it.
        _window.Type('4');
        _window.Type('1');
        _window.Type('4');

        Assert.Equal(GuiView.Reclaim, _shell.Active);
    }

    [Fact]
    public void The_frame_before_the_rules_finish_says_they_are_running()
    {
        Save(Sample());

        _window.Type('4');

        // Nothing has been pumped, so as far as the shell knows the worker is still busy.
        var frame = _window.Paint();

        Assert.Equal(GuiView.Reclaim, _shell.Active);
        Assert.True(frame.Mentions("Applying"), string.Join(" | ", frame.AllText));
        Assert.False(frame.Mentions("dev.node_modules"));
    }

    [Fact]
    public void Lists_the_rules_that_matched_largest_first_with_both_axes()
    {
        var frame = Open(Sample());

        Assert.True(frame.Said("dev.node_modules"), string.Join(" | ", frame.AllText));
        Assert.True(frame.Said("safe redownload"));
        Assert.True(frame.Said("caution irreversible"));

        Assert.True(Top(frame, "dev.node_modules") < Top(frame, "user.large_media"));
        Assert.True(Top(frame, "user.large_media") < Top(frame, "dev.dotnet_artifacts"));

        Assert.True(frame.Said("6 GB"), string.Join(" | ", frame.AllText));

        // The headline is what pathmemo can give back: node_modules and obj. The disk image
        // needs a person, so its bytes are named beside the total and kept out of it.
        Assert.True(frame.Mentions("6.88 GB reclaimable"), string.Join(" | ", frame.AllText));
        Assert.True(frame.Mentions("1.46 GB for another tool or a decision"));

        // Stored numbers name the scan they came from.
        Assert.True(frame.Mentions("scan 1"));

        // It reports; it does not delete, and says where deleting is.
        Assert.True(frame.Mentions("reclaim --apply"));
    }

    [Fact]
    public void A_double_click_lists_the_paths_and_the_heading_leads_back()
    {
        var frame = Open(Sample());

        DoubleClick(frame, "dev.node_modules", FontRole.Bold);
        frame = _window.Paint();

        Assert.True(frame.Said(@"C:\work\app\node_modules"), string.Join(" | ", frame.AllText));
        Assert.True(frame.Said(@"C:\work\other\node_modules"));
        Assert.True(Top(frame, @"C:\work\app\node_modules") < Top(frame, @"C:\work\other\node_modules"));
        Assert.False(frame.Said("user.large_media"));

        // The active tab is drawn bold, so the body-weight "Reclaim" is the way back.
        Click(frame, "Reclaim", FontRole.Body);
        frame = _window.Paint();

        Assert.True(frame.Said("user.large_media"));
        Assert.False(frame.Said(@"C:\work\app\node_modules"));
    }

    [Fact]
    public void The_ceiling_hides_what_is_above_it_by_click_and_by_key()
    {
        var frame = Open(Sample());

        Click(frame, "safe", FontRole.Body);
        frame = _window.Paint();

        Assert.False(frame.Said("user.large_media"));
        Assert.True(frame.Said("dev.node_modules"));
        Assert.Contains("Safe only", _shell.Message, StringComparison.Ordinal);

        _window.Type('t');
        frame = _window.Paint();

        Assert.True(frame.Said("user.large_media"));
        Assert.Contains("caution", _shell.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_keys_go_in_and_come_back_to_the_rule_they_left()
    {
        Open(Sample());

        _window.Press(User32.VkDown);
        _window.Press(User32.VkReturn);

        var frame = _window.Paint();
        Assert.True(frame.Said(@"C:\images\backup.vhdx"), string.Join(" | ", frame.AllText));

        // A rule pathmemo will not act on says so where its paths are listed.
        Assert.True(frame.Mentions("reported only"));

        _window.Press(User32.VkBack);
        _window.Press(User32.VkReturn);

        // Back on the second rule rather than at the top, so Enter opens the same one again.
        Assert.True(_window.Paint().Said(@"C:\images\backup.vhdx"));
    }

    [Fact]
    public void A_scan_with_nothing_to_recommend_says_that_rather_than_showing_an_empty_table()
    {
        var frame = Open(new TestTree().File(@"C:\notes\todo.txt", 4096));

        Assert.True(frame.Mentions("No rule matched"), string.Join(" | ", frame.AllText));
    }

    [Fact]
    public void A_scan_that_was_stopped_early_is_called_a_floor_above_its_own_numbers()
    {
        // The rows of a partial scan look exactly like a complete one's, and a cache the scan
        // never reached is simply absent. The snapshot knows; the tab has to say.
        SnapshotStore.Save(Sample().Result(When, flags: Scanning.ScanFlags.Partial), 1);

        _window.Type('4');
        Assert.True(_window.Pump(ReclaimJob.ReadyMessage));

        var frame = _window.Paint();

        Assert.True(frame.Mentions("stopped early"), string.Join(" | ", frame.AllText));
        Assert.True(frame.Said("dev.node_modules"));
        Assert.True(Top(frame, frame.Texts.First(t => t.Text.Contains("stopped early", StringComparison.Ordinal)).Text)
                    < Top(frame, "dev.node_modules"));
    }

    [Fact]
    public void A_complete_scan_carries_no_such_warning()
    {
        Assert.False(Open(Sample()).Mentions("stopped early"));
    }

    [Fact]
    public void A_ready_message_with_no_job_behind_it_changes_nothing()
    {
        // What a late worker would send after a scan replaced its snapshot.
        _window.OnPosted?.Invoke(ReclaimJob.ReadyMessage, 0);

        Assert.Equal(GuiView.Overview, _shell.Active);

        var frame = Open(Sample());
        _window.OnPosted?.Invoke(ReclaimJob.ReadyMessage, 0);

        Assert.True(_window.Paint().Said("dev.node_modules"));
        Assert.True(frame.Said("dev.node_modules"));
    }

    private static TestTree Sample() => new TestTree()
        .File(@"C:\work\app\node_modules\react\index.js", 4L << 30)
        .File(@"C:\work\other\node_modules\vue\index.js", 2L << 30)
        .File(@"C:\work\app\obj\Debug\app.dll", 900L << 20)
        .File(@"C:\work\app\src\main.ts", 4096)
        .File(@"C:\images\backup.vhdx", 1500L << 20);

    private static void Save(TestTree tree) => SnapshotStore.Save(tree.Result(When), 1);

    /// <summary>Saves a scan, opens the tab, lets the rules arrive, and paints.</summary>
    private RecordingPainter Open(TestTree tree)
    {
        Save(tree);

        _window.Type('4');

        Assert.True(_window.Pump(ReclaimJob.ReadyMessage), "the rules did not finish within ten seconds");

        return _window.Paint();
    }

    /// <summary>Where a piece of text was drawn, top edge, for asserting an order.</summary>
    private static int Top(RecordingPainter frame, string text)
    {
        foreach (var run in frame.Texts)
            if (run.Text == text) return run.Area.Y;

        Assert.Fail($"the frame never drew '{text}': {string.Join(" | ", frame.AllText)}");
        return -1;
    }

    private void Click(RecordingPainter frame, string text, FontRole role)
    {
        var (x, y) = Middle(frame, text, role);
        _window.Click(x, y);
    }

    private void DoubleClick(RecordingPainter frame, string text, FontRole role)
    {
        var (x, y) = Middle(frame, text, role);
        _window.DoubleClick(x, y);
    }

    /// <summary>The middle of a piece of text the last frame drew (README section 24.3).</summary>
    private static (int X, int Y) Middle(RecordingPainter frame, string text, FontRole role)
    {
        foreach (var run in frame.Texts)
        {
            if (run.Role != role || !string.Equals(run.Text, text, StringComparison.Ordinal)) continue;

            return (run.Area.X + run.Area.Width / 2, run.Area.Y + run.Area.Height / 2);
        }

        Assert.Fail($"the frame never drew '{text}' in {role}: {string.Join(" | ", frame.AllText)}");
        return (-1, -1);
    }
}
