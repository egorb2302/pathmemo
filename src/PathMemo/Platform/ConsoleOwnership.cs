using System.Runtime.InteropServices;

namespace PathMemo.Platform;

/// <summary>
/// Tells apart "launched from a terminal" from "launched by double-clicking the exe".
/// </summary>
/// <remarks>
/// <para>
/// When Explorer starts a console application it allocates a fresh console for it, and
/// tears that console down the moment the process exits - so anything printed vanishes.
/// When a shell starts it, the process joins the shell's existing console instead.
/// </para>
/// <para>
/// <c>GetConsoleProcessList</c> distinguishes the two: it reports how many processes are
/// attached to this console. Exactly one means nobody else is there, so the window is ours
/// and disappears with us.
/// </para>
/// </remarks>
internal static partial class ConsoleOwnership
{
    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint GetConsoleProcessList(
        [Out] uint[] lpdwProcessList, uint dwProcessCount);

    [LibraryImport("kernel32.dll")]
    private static partial nint GetConsoleWindow();

    private static bool? _cached;

    /// <summary>
    /// Hides the console window when it belongs to us, for a session that has a real window
    /// of its own (README section 24.1).
    /// </summary>
    /// <remarks>
    /// The executable stays a console application, because <c>pathmemo scan</c> printing to the
    /// terminal it was typed into is the whole of principle P8 and a windowed subsystem would
    /// end it. A double-click therefore still gets a console - Explorer made it - and the GUI
    /// takes it down. The check is not a nicety: called from a terminal, this would hide the
    /// user's own shell window, which is as close to destroying their work as drawing a window
    /// can get.
    /// </remarks>
    internal static bool HideOurWindow()
    {
        if (!OwnsTheWindow) return false;

        var console = GetConsoleWindow();
        if (console == nint.Zero) return false;

        if (!Native.User32.ShowWindow(console, Native.User32.SwHide)) return false;

        _hidden = console;
        return true;
    }

    /// <summary>
    /// Brings back a console hidden by <see cref="HideOurWindow"/>, and does nothing otherwise.
    /// </summary>
    /// <remarks>
    /// Called before the process exits. Leaving it hidden would be invisible in the common case
    /// - our console dies with us - but a hidden window that outlives the call is a window the
    /// user cannot get back, and a fatal error still has a message to print into it.
    /// </remarks>
    internal static void RestoreOurWindow()
    {
        if (_hidden == nint.Zero) return;

        Native.User32.ShowWindow(_hidden, Native.User32.SwShow);
        _hidden = nint.Zero;
    }

    private static nint _hidden;

    internal static bool OwnsTheWindow
    {
        get
        {
            if (_cached is { } value) return value;
            _cached = Detect();
            return _cached.Value;
        }
    }

    private static bool Detect()
    {
        if (Console.IsOutputRedirected || Console.IsInputRedirected) return false;

        try
        {
            var buffer = new uint[8];
            var count = GetConsoleProcessList(buffer, (uint)buffer.Length);

            // 0 means the call failed - no console at all, or an unusual host. Assume we do
            // not own the window: holding it open when nobody asked is the worse mistake.
            return count == 1;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
    }
}
