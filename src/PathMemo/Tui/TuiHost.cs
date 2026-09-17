using PathMemo.Cli;
using PathMemo.Cli.Commands;
using PathMemo.Cli.Interactive;
using PathMemo.Config;
using PathMemo.Tui.Dialogs;
using PathMemo.Tui.Screens;
using PathMemo.Tui.Terminal;

namespace PathMemo.Tui;

/// <summary>
/// The input loop and the frame loop (README sections 14, 18.1).
/// </summary>
/// <remarks>
/// <para>
/// Two screens, a modal on top, and one place that decides what a key means: the modal
/// first, then the screen, then the global bindings. Keys fall through rather than being
/// registered, which keeps every binding visible in one file and makes the help dialog
/// something that can be checked against the code by reading it.
/// </para>
/// <para>
/// Anything that has to write to the console - a rescan with its progress line, the audit
/// view, an editor - leaves the alternate buffer first and comes back afterwards. That is
/// what keeps the rule in README section 14.6 (nothing but the frame reaches the terminal
/// while the TUI is up) true without threading a logger through every call.
/// </para>
/// </remarks>
internal static class TuiHost
{
    private const int MinWidth = 80;
    private const int MinHeight = 24;

    /// <summary>True while the TUI owns the terminal, so nothing else writes to it.</summary>
    internal static bool Active { get; private set; }

    /// <summary>
    /// Runs the TUI, or reports that this host cannot have one - a redirected stream, or a
    /// terminal that will not do escape sequences. The caller then falls back to the
    /// line-based session, which works anywhere (README section 2.1).
    /// </summary>
    internal static bool TryRun(CancellationToken ct, out int exitCode)
    {
        exitCode = ExitCode.Ok;

        if (Console.IsOutputRedirected || Console.IsInputRedirected) return false;
        if (!VirtualTerminal.Enable()) return false;

        try
        {
            Active = true;
            exitCode = Loop(ct);
            return true;
        }
        finally
        {
            Active = false;
            VirtualTerminal.Restore();
        }
    }

    private static int Loop(CancellationToken ct)
    {
        var session = new TuiSession { Report = StatusReport.Collect() };
        var overview = new OverviewScreen();
        var tree = new TreeScreen();

        var color = Environment.GetEnvironmentVariable("NO_COLOR") is null;
        var screen = new Screen(Console.Out, color);

        VirtualTerminal.EnterAlternateBuffer();
        Fit(screen);

        session.Say("press ? for keys");

        while (!ct.IsCancellationRequested && !session.Quit)
        {
            if (Fit(screen)) screen.Invalidate();

            screen.Begin();

            if (screen.Width < MinWidth || screen.Height < MinHeight) TooSmall(screen);
            else
            {
                ITuiView active = session.Active == ViewKind.Tree ? tree : overview;
                active.Render(screen, session);
                session.Modal?.Render(screen, session);
            }

            screen.Flush();

            if (!KeyReader.TryRead(120, out var key)) continue;

            if (session.Modal is { } modal) modal.HandleKey(key, session);
            else
            {
                ITuiView active = session.Active == ViewKind.Tree ? tree : overview;
                if (!active.HandleKey(key, session)) Global(key, session, tree, overview, ct);
            }

            if (session.TakeSuspended() is { } work) Suspend(work, session, screen, tree);
        }

        return ExitCode.Ok;
    }

    private static void Global(in TuiKey key, TuiSession session, TreeScreen tree, OverviewScreen overview,
                               CancellationToken ct)
    {
        if (key.Is('?'))
        {
            session.Modal = new HelpDialog();
            return;
        }

        if (key.Is('1'))
        {
            session.Active = ViewKind.Overview;
            session.Clear();
            return;
        }

        if (key.Is('2') || key.Is(ConsoleKey.Enter) || key.Is('l'))
        {
            if (!session.EnsureSnapshot()) return;

            tree.Enter(session, session.Active == ViewKind.Overview ? overview.SelectedLetter : null);
            session.Active = ViewKind.Tree;
            session.Clear();
            return;
        }

        if (key.Is('3'))
        {
            session.Suspend(() => AuditView.Run(ct));
            return;
        }

        if (key.Is('4'))
        {
            session.Warn("the reclaim screen arrives with P7");
            return;
        }

        if (key.Is('5'))
        {
            session.Warn("the duplicates screen arrives with P8");
            return;
        }

        if (key.Is(ConsoleKey.F5))
        {
            Rescan(session, tree, ct);
            return;
        }

        if (key.Is('c'))
        {
            OpenConfig(session);
            return;
        }

        if (key.Is('Q'))
        {
            session.Quit = true;
            return;
        }

        if (key.Is('q') || key.Is(ConsoleKey.Escape))
        {
            // Back, one level at a time: the tree returns to the overview, and the
            // overview has nowhere further to go.
            if (session.Active == ViewKind.Tree) { session.Active = ViewKind.Overview; session.Clear(); }
            else session.Quit = true;

            return;
        }

        session.Say($"'{(key.Char == '\0' ? key.Key.ToString() : key.Char.ToString())}' does nothing here - ? for keys");
    }

    private static void Rescan(TuiSession session, TreeScreen tree, CancellationToken ct)
    {
        // The volume in front of the user, not "everything": a full sweep of a 900 GB
        // spinning disk is fifteen minutes, and nobody meant that by pressing F5.
        var root = session.Active == ViewKind.Tree && session.Snapshot is not null
            ? tree.CurrentRootPath(session)
            : session.Report.PrimaryLetter is { } letter ? letter + Path.DirectorySeparatorChar : "";

        session.Suspend(() =>
        {
            Console.WriteLine();
            Console.WriteLine(root.Length == 0 ? "Scanning every fixed volume." : $"Scanning {root}");
            Console.WriteLine("Ctrl+C stops it and keeps what was found so far.");
            Console.WriteLine();

            var options = new ScanOptions { Roots = root.Length == 0 ? [] : [root], Top = 10 };

            try
            {
                ScanCommand.RunAsync(options, ct).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine();
                Console.WriteLine("Cancelled.");
            }
        });
    }

    private static void OpenConfig(TuiSession session)
    {
        if (!File.Exists(AppPaths.ConfigPath))
        {
            session.Warn($"no config file yet - it arrives with P7, in {AppPaths.DataDirectory}");
            return;
        }

        if (Platform.FileLaunch.TryOpen(AppPaths.ConfigPath, out var error))
            session.Say("opened the config in its default editor - press F5 after saving");
        else
            session.Warn($"could not open the config: {error}");
    }

    /// <summary>
    /// Hands the terminal back, runs something that prints, and takes it again.
    /// </summary>
    private static void Suspend(Action work, TuiSession session, Screen screen, TreeScreen tree)
    {
        Active = false;
        VirtualTerminal.LeaveAlternateBuffer();

        try
        {
            work();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Console.WriteLine();
            Console.WriteLine($"pathmemo: {ex.Message}");
        }

        Console.WriteLine();
        Console.Write("  press Enter to return ");
        Console.ReadLine();

        // What just ran may have produced a new scan, so nothing loaded is trusted.
        session.Reload();
        tree.Reset();

        VirtualTerminal.EnterAlternateBuffer();
        KeyReader.Drain();
        screen.Invalidate();
        Active = true;
    }

    private static void TooSmall(Screen screen)
    {
        var line = screen.Row(0).Space()
            .Add($"pathmemo needs {MinWidth}x{MinHeight}; this window is {screen.Width}x{screen.Height}.", Style.Warning);
        screen.Put(0, line);

        if (screen.Height > 1)
            screen.Put(1, screen.Row(1).Space().Add("Resize it, or press q to leave.", Style.Dim));
    }

    /// <summary>Picks up a window resize. Returns true when the size changed.</summary>
    private static bool Fit(Screen screen)
    {
        int width, height;

        try
        {
            width = Console.WindowWidth;
            height = Console.WindowHeight;
        }
        catch (IOException)
        {
            width = MinWidth;
            height = MinHeight;
        }

        if (width == screen.Width && height == screen.Height) return false;

        screen.Resize(Math.Max(20, width), Math.Max(4, height));
        return true;
    }
}
