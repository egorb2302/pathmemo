using PathMemo.Cli;
using PathMemo.Cli.Commands;
using PathMemo.Cli.Output;
using PathMemo.Config;
using PathMemo.Platform;

namespace PathMemo.Gui;

/// <summary>
/// Opens the window, or says it could not (README section 24.1).
/// </summary>
/// <remarks>
/// <para>
/// The counterpart of <c>TuiHost.TryRun</c>, and it declines for the same kind of reason: a
/// window that cannot be created is not a fatal error, it is a reason to fall back to the
/// terminal interface that needs nothing from the window manager. That happens on a session
/// with no desktop - a service account, an SSH login, a container - where <c>CreateWindowEx</c>
/// fails and the TUI works perfectly.
/// </para>
/// <para>
/// The executable stays a console application on purpose (README section 24.1). This is where
/// the console window that Explorer created for a double-click is hidden, and it is hidden only
/// when it is ours: run from a terminal, the window opens beside the shell and the shell is
/// left alone.
/// </para>
/// </remarks>
internal static class GuiHost
{
    /// <summary>True while the window owns the session, so nothing writes to the console.</summary>
    internal static bool Active { get; private set; }

    internal static bool TryRun(out int exitCode)
    {
        exitCode = ExitCode.Ok;

        // Collected before the window exists, so the first frame has real numbers in it rather
        // than a window that appears empty and fills in (README section 20: tens of milliseconds).
        var report = StatusReport.Collect();

        using var window = new GuiWindow();

        if (!window.Create($"pathmemo {AppInfo.Version}")) return false;

        ConsoleOwnership.HideOurWindow();
        Active = true;

        try
        {
            // The shell subscribes in its constructor, so it exists before the window is shown
            // and the very first frame is a real one. Held in a local because the window keeps
            // it alive only through the delegates it handed over.
            var shell = new GuiShell(window, report);

            window.Show();
            window.Run();

            GC.KeepAlive(shell);
        }
        finally
        {
            Active = false;
            ConsoleOwnership.RestoreOurWindow();
        }

        Diagnostics.Memory(Console.Error, "the GUI at exit");
        return true;
    }
}
