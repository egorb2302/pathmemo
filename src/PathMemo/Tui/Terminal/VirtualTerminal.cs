using PathMemo.Platform.Native;

namespace PathMemo.Tui.Terminal;

/// <summary>
/// Puts the console into the state the renderer needs, and puts it back afterwards.
/// </summary>
/// <remarks>
/// <para>
/// Every terminal that matters on Windows understands ANSI, but a console application
/// does not get escape processing for free: conhost needs
/// <c>ENABLE_VIRTUAL_TERMINAL_PROCESSING</c> switched on first, and Windows Terminal
/// inherits a conhost-compatible mode for the same reason. If that call fails we are in a
/// host that would render the frame as literal escape sequences, so the TUI declines to
/// start and the caller falls back to the line-based session (README section 2.1).
/// </para>
/// <para>
/// The alternate screen buffer is what lets the user get their scrollback back on exit:
/// the tree never enters the history, and quitting restores the shell exactly as it was.
/// Leaving is registered on process exit as well as in a finally block, because a
/// terminal left in the alternate buffer with a hidden cursor looks broken and the user
/// has no obvious way to fix it.
/// </para>
/// </remarks>
internal static class VirtualTerminal
{
    private static uint _originalMode;
    private static bool _modeChanged;
    private static bool _inAlternateBuffer;
    private static bool _exitHookInstalled;

    /// <summary>Whether escape sequences will be interpreted rather than printed.</summary>
    internal static bool Enable()
    {
        if (Console.IsOutputRedirected || Console.IsInputRedirected) return false;

        try
        {
            var handle = Kernel32Extra.GetStdHandle(Kernel32Extra.StdOutputHandle);
            if (handle == 0 || handle == -1) return false;

            if (!Kernel32Extra.GetConsoleMode(handle, out var mode)) return false;

            if ((mode & Kernel32Extra.EnableVirtualTerminalProcessing) != 0) return true;

            var wanted = mode | Kernel32Extra.EnableVirtualTerminalProcessing
                              | Kernel32Extra.EnableProcessedOutput;

            if (!Kernel32Extra.SetConsoleMode(handle, wanted)) return false;

            _originalMode = mode;
            _modeChanged = true;
            return true;
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            return false;
        }
    }

    internal static void EnterAlternateBuffer()
    {
        if (_inAlternateBuffer) return;

        InstallExitHook();
        _inAlternateBuffer = true;

        // Alternate buffer, cursor home, cursor hidden, screen cleared.
        Console.Out.Write("[?1049h[H[2J[?25l");
        Console.Out.Flush();
    }

    internal static void LeaveAlternateBuffer()
    {
        if (!_inAlternateBuffer) return;
        _inAlternateBuffer = false;

        try
        {
            Console.Out.Write("[?25h[0m[?1049l");
            Console.Out.Flush();
        }
        catch (IOException)
        {
            // The console went away underneath us. Nothing left to restore.
        }
    }

    /// <summary>Restores the console mode this process changed. Safe to call twice.</summary>
    internal static void Restore()
    {
        LeaveAlternateBuffer();

        if (!_modeChanged) return;
        _modeChanged = false;

        try
        {
            var handle = Kernel32Extra.GetStdHandle(Kernel32Extra.StdOutputHandle);
            if (handle != 0 && handle != -1) Kernel32Extra.SetConsoleMode(handle, _originalMode);
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            // Nothing to restore on a host without these entry points.
        }
    }

    /// <summary>
    /// The safety net for the paths that skip our finally block: a second Ctrl+C, or a
    /// crash. Both end in ProcessExit, and both would otherwise leave the user staring at
    /// an empty alternate buffer.
    /// </summary>
    private static void InstallExitHook()
    {
        if (_exitHookInstalled) return;
        _exitHookInstalled = true;

        AppDomain.CurrentDomain.ProcessExit += (_, _) => Restore();
    }
}
