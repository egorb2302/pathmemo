using PathMemo.Gui;
using PathMemo.Gui.Render;

namespace PathMemo.Tests;

/// <summary>
/// An <see cref="IShellWindow"/> that records instead of existing (README section 24.6).
/// </summary>
/// <remarks>
/// The companion of <see cref="RecordingPainter"/>, and the second implementation that lets
/// <see cref="IShellWindow"/> exist under the rule of README section 17.2. The painter made a
/// view testable without a desktop; this makes the <i>shell</i> testable - which view is
/// showing, what a tab click does, what the status line ends up saying - with no message loop
/// and no handle.
///
/// It holds the callbacks the shell subscribes with, so a test drives the shell the way
/// Windows would: by invoking <see cref="OnKey"/> or <see cref="OnMouse"/>.
/// </remarks>
internal sealed class RecordingWindow : IShellWindow
{
    public Theme Theme { get; } = Theme.Dusk;

    public Action<IPainter, Rect>? OnPaint { get; set; }

    public Action<MouseInput>? OnMouse { get; set; }

    public Action<KeyInput>? OnKey { get; set; }

    public Action<uint, nuint>? OnPosted { get; set; }

    public Action? OnThemeChanged { get; set; }

    public Func<bool>? OnClosing { get; set; }

    /// <summary>How many repaints were asked for.</summary>
    internal int Invalidations { get; private set; }

    internal List<(uint Message, nuint Argument)> Posts { get; } = [];

    internal bool Closed { get; private set; }

    public void Invalidate() => Invalidations++;

    /// <remarks>Called from worker threads, which is the whole point of a post - hence the lock.</remarks>
    public void Post(uint message, nuint argument = 0)
    {
        lock (Posts) Posts.Add((message, argument));
    }

    /// <summary>
    /// Waits for a worker to post <paramref name="message"/> and then delivers everything
    /// posted so far, in order, on the calling thread - one turn of the message loop.
    /// </summary>
    /// <returns>False when the message did not arrive within the budget.</returns>
    internal bool Pump(uint message, int budgetMs = 10_000)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();

        while (true)
        {
            (uint Message, nuint Argument)[] pending;

            lock (Posts)
            {
                pending = Posts.Exists(p => p.Message == message) ? [.. Posts] : [];
                if (pending.Length > 0) Posts.Clear();
            }

            if (pending.Length > 0)
            {
                foreach (var post in pending) OnPosted?.Invoke(post.Message, post.Argument);
                return true;
            }

            if (clock.ElapsedMilliseconds > budgetMs) return false;

            Thread.Sleep(5);
        }
    }

    public void Close() => Closed = true;

    /// <summary>Paints a frame of the given size and hands back what was drawn.</summary>
    /// <remarks>
    /// The hit map is built while painting (README section 24.3), so a test that wants to
    /// click something has to paint it first - exactly as the window does.
    /// </remarks>
    internal RecordingPainter Paint(int width = 1280, int height = 800)
    {
        var painter = new RecordingPainter();
        OnPaint?.Invoke(painter, new Rect(0, 0, width, height));

        return painter;
    }

    internal void Click(int x, int y) =>
        OnMouse?.Invoke(new MouseInput(MouseKind.Down, x, y, 0, false, false));

    internal void DoubleClick(int x, int y) =>
        OnMouse?.Invoke(new MouseInput(MouseKind.DoubleClick, x, y, 0, false, false));

    internal void Type(char character) =>
        OnKey?.Invoke(new KeyInput(0, character, false, false));

    /// <summary>A key that is not a character: an arrow, Enter, Backspace.</summary>
    internal void Press(int virtualKey) =>
        OnKey?.Invoke(new KeyInput(virtualKey, '\0', false, false));
}
