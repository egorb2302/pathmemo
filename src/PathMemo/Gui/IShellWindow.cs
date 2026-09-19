using PathMemo.Gui.Render;

namespace PathMemo.Gui;

/// <summary>
/// The window as the shell sees it: somewhere to send events and something to repaint
/// (README section 24.6).
/// </summary>
/// <remarks>
/// <para>
/// Two implementations, which is what section 17.2 requires before an interface may exist:
/// <see cref="GuiWindow"/>, which is Win32, and a recording one in the test suite. It is the
/// same split that <c>IPainter</c> has, one layer up - <c>IPainter</c> made a <i>view</i>
/// testable without a desktop, and this makes the <i>navigation</i> testable, which is
/// everything about which view is showing and what a click does to it.
/// </para>
/// <para>
/// It exists because that was the one part of the window with no tests at all, and it shipped
/// a crash: with no snapshot to browse, asking for the Tree view sent <c>Show</c> and
/// <c>OpenTree</c> calling each other until the stack ran out, and the process died with
/// <c>0xC00000FD</c> before it could draw the message that says to run a scan. Layout,
/// hit testing and the treemap were covered; the five lines deciding which view to show
/// were not, because they were the only ones that needed a window.
/// </para>
/// </remarks>
internal interface IShellWindow
{
    Theme Theme { get; }

    Action<IPainter, Rect>? OnPaint { get; set; }

    Action<MouseInput>? OnMouse { get; set; }

    Action<KeyInput>? OnKey { get; set; }

    /// <summary>A message posted from a worker thread (README section 24.4).</summary>
    Action<uint, nuint>? OnPosted { get; set; }

    Action? OnThemeChanged { get; set; }

    /// <summary>Asked before closing; false keeps the window open.</summary>
    Func<bool>? OnClosing { get; set; }

    /// <summary>Asks for a repaint.</summary>
    void Invalidate();

    /// <summary>Wakes the window from any thread.</summary>
    void Post(uint message, nuint argument = 0);

    void Close();
}
