using PathMemo.Cli.Commands;
using PathMemo.Gui;
using PathMemo.Gui.Render;
using PathMemo.Snapshots;
using Xunit;

namespace PathMemo.Tests;

/// <summary>
/// The window's navigation: which view a click or a key selects, and what happens when the
/// thing it selects cannot be loaded (README section 24.6).
/// </summary>
/// <remarks>
/// <para>
/// This was the one part of the window with no tests, and it is the part that shipped a crash
/// in v0.2.0. On a machine where nothing had been scanned yet, clicking the Tree tab - or a
/// volume card, which is the other way in - sent <c>Show</c> and <c>OpenTree</c> calling each
/// other forever, and the process died of a stack overflow rather than printing the message
/// that was waiting one line further down. Everything around it was covered: the treemap, the
/// virtualised list, hit testing, the tree view's own drawing. The five lines choosing a view
/// were not, because they were the only ones that needed a window to exercise.
/// </para>
/// <para>
/// So the assertion that matters most here is not what any of these tests check afterwards -
/// it is that they <i>return at all</i>. With the defect present these do not fail, they take
/// the test host down with them.
/// </para>
/// </remarks>
public sealed class ShellTests : IDisposable
{
    private readonly string _dataDirectory = Path.Combine(
        Path.GetTempPath(), "pathmemo-shell-" + Guid.NewGuid().ToString("N")[..12]);

    private readonly RecordingWindow _window = new();
    private readonly GuiShell _shell;

    public ShellTests()
    {
        Directory.CreateDirectory(_dataDirectory);
        TestStore.Use(_dataDirectory);

        // An empty store: no scans, so nothing to browse. That is a fresh install, and it was
        // the state in which the window could not be navigated at all.
        _shell = new GuiShell(_window, StatusReport.Collect());
    }

    public void Dispose()
    {
        try { Directory.Delete(_dataDirectory, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    [Fact]
    public void The_tree_tab_with_nothing_scanned_says_so_instead_of_dying()
    {
        var painter = _window.Paint();

        Click(painter, "Tree", FontRole.Body);

        Assert.Equal(GuiView.Tree, _shell.Active);
        Assert.Contains("scan", _shell.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_volume_card_with_nothing_scanned_does_the_same()
    {
        // The other route in, and the one that does not go through Show at all: a card asks
        // for a particular volume. Both have to end somewhere.
        Assert.NotEmpty(_shell.Report.Volumes);

        var painter = _window.Paint();

        Click(painter, _shell.Report.Volumes[0].Letter, FontRole.Title);

        Assert.Equal(GuiView.Tree, _shell.Active);
        Assert.Contains("scan", _shell.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void And_so_does_the_key_that_selects_it()
    {
        _window.Type('2');

        Assert.Equal(GuiView.Tree, _shell.Active);
        Assert.Contains("scan", _shell.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Asking_twice_is_not_worse_than_asking_once()
    {
        // The failure leaves the view selected and the tree still unloaded, which is the
        // state the recursion fed on. Clicking again has to be as harmless as the first time.
        _window.Type('2');
        _window.Type('2');
        _window.Type('2');

        Assert.Equal(GuiView.Tree, _shell.Active);
    }

    [Fact]
    public void With_a_snapshot_the_tab_opens_the_tree_rather_than_an_excuse()
    {
        // The other half of the fix: a Tree tab that always refused would pass every test
        // above and be useless. Given something to browse it has to browse it.
        var scan = new TestTree()
            .File(@"C:\big.iso", 8L << 30)
            .File(@"C:\sub\one.mp4", 2L << 30)
            .Result(new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc));

        SnapshotStore.Save(scan, 1);

        var painter = _window.Paint();
        Click(painter, "Tree", FontRole.Body);

        Assert.Equal(GuiView.Tree, _shell.Active);
        Assert.Equal("", _shell.Message);

        var frame = _window.Paint();
        Assert.True(frame.Mentions("big.iso"), string.Join(" | ", frame.AllText));
    }

    [Fact]
    public void The_tabs_that_are_not_built_yet_still_select()
    {
        // Nothing here loads anything, so nothing here could recurse - but they share the
        // path, and a fix that made only the tree behave would be half a fix.
        var painter = _window.Paint();

        Click(painter, "Duplicates", FontRole.Body);

        Assert.Equal(GuiView.Dupes, _shell.Active);
    }

    /// <summary>
    /// Clicks the middle of a piece of text that the last frame drew.
    /// </summary>
    /// <remarks>
    /// Aiming at the text rather than at computed coordinates is the same rule the window
    /// itself follows: a click is resolved against what was actually drawn (README section
    /// 24.3), so a test that clicks where a word appeared cannot drift out of a layout's way.
    /// </remarks>
    private void Click(RecordingPainter painter, string text, FontRole role)
    {
        foreach (var run in painter.Texts)
        {
            if (run.Role != role || !string.Equals(run.Text, text, StringComparison.Ordinal)) continue;

            _window.Click(run.Area.X + run.Area.Width / 2, run.Area.Y + run.Area.Height / 2);
            return;
        }

        Assert.Fail($"the frame never drew '{text}' in {role}: {string.Join(" | ", painter.AllText)}");
    }
}
