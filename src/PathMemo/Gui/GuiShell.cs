using PathMemo.Cli.Commands;
using PathMemo.Gui.Controls;
using PathMemo.Gui.Render;
using PathMemo.Gui.Views;
using PathMemo.Gui.Work;
using PathMemo.Platform;
using PathMemo.Snapshots;

namespace PathMemo.Gui;

internal enum GuiView
{
    Overview,
    Tree,
    Audit,
    Reclaim,
    Dupes,
}

internal enum GuiAction
{
    None,
    Scan,
    Elevate,
    OpenConfig,
    Help,
    CycleSizeMode,
    GoUp,
}

/// <summary>
/// The window's contents: the chrome, which view is showing, and where events go
/// (README section 24.3).
/// </summary>
/// <remarks>
/// <para>
/// The counterpart of <c>TuiHost</c>, and deliberately the same shape: one object owning the
/// session and the active view, a paint that draws the frame top to bottom, and a single place
/// where a key or a click turns into an action. What it is not is a window manager - there are
/// no child windows anywhere in this application, because a list, a bar and a tab strip are
/// things to draw rather than things to own.
/// </para>
/// <para>
/// The status report is collected once at startup and again after anything that could change
/// it. It is the same <c>StatusReport</c> the Overview screen and <c>pathmemo status</c> use,
/// so the three cannot disagree (README section 14.7).
/// </para>
/// </remarks>
internal sealed class GuiShell
{
    private readonly IShellWindow _window;
    private readonly HitMap _hits = new();
    private readonly OverviewView _overview = new();
    private readonly TreeView _tree = new();

    private Hit _hover = Hit.None;
    private int _pointerX = -1;
    private int _pointerY = -1;

    private ScanJob? _job;
    private Scanning.ScanProgress? _progress;
    private bool _closeWhenIdle;

    internal GuiShell(IShellWindow window, StatusReport report)
    {
        _window = window;
        Report = report;

        window.OnPaint = Paint;
        window.OnMouse = Mouse;
        window.OnKey = Key;
        window.OnThemeChanged = () => { };
        window.OnPosted = Posted;
        window.OnClosing = Closing;
    }

    /// <summary>
    /// A message from a worker thread. The value itself is collected here, on the UI thread
    /// (README section 24.4).
    /// </summary>
    private void Posted(uint message, nuint _)
    {
        if (_job is null) return;

        switch (message)
        {
            case ScanJob.ProgressMessage:
                _progress = _job.Take();
                _window.Invalidate();
                return;

            case ScanJob.FinishedMessage:
                Finished();
                return;
        }
    }

    private void Finished()
    {
        var job = _job;
        if (job is null) return;

        _job = null;
        _progress = null;

        // Exit code 3 is what a graceful stop returns: the scan kept what it had read and
        // flagged the result partial (README section 4.8). Reporting that as "finished" would
        // put a complete-looking total on screen over an incomplete one, which is the one
        // thing principle P1 forbids. Only code 4 means nothing was written at all.
        Message = job.Error is { } error
            ? $"Scan failed: {error}"
            : job.ExitCode switch
            {
                Cli.ExitCode.Partial =>
                    "Scan stopped. What was read has been kept, and the totals are incomplete.",
                Cli.ExitCode.Cancelled => "Scan cancelled before anything was recorded.",
                Cli.ExitCode.Ok => "Scan finished.",
                _ => $"Scan ended with exit code {job.ExitCode}.",
            };

        job.Dispose();

        // Somebody closed the window while the scan was running; it was held open only long
        // enough to stop the scan cleanly, so honour that now.
        if (_closeWhenIdle)
        {
            _closeWhenIdle = false;
            _window.Close();
            return;
        }

        // Everything on screen came from the old scan, including a tree that is now a
        // snapshot behind. Dropping it is what makes the next open read the new one.
        Report = StatusReport.Collect();
        _tree.Forget();

        _window.Invalidate();
    }

    /// <summary>
    /// Refuses to close while a scan is running, because closing would kill it mid-write.
    /// </summary>
    private bool Closing()
    {
        if (_job is null) return true;

        _job.Cancel();
        Say("Stopping the scan first - the window will close when it has finished.");
        _closeWhenIdle = true;

        return false;
    }

    internal StatusReport Report { get; private set; }

    internal GuiView Active { get; private set; } = GuiView.Overview;

    /// <summary>A line at the bottom of the window: the last thing that happened.</summary>
    internal string Message { get; private set; } = "";

    private Theme Theme => _window.Theme;

    private void Paint(IPainter p, Rect area)
    {
        _hits.Clear();

        var toolbar = area.TakeTop(p.Scale(46));
        var status = area.TakeBottom(p.Scale(28));
        var content = area.DropTop(toolbar.Height).DropBottom(status.Height);

        // The content first, then the scan card - which seals everything registered before it
        // out of the hit map - and the chrome last, so the toolbar stays live while a scan
        // runs. Painted in this order the toolbar's Stop is a button that works; registered
        // before the seal it was a button that looked pressable and silently was not.
        // Nothing overlaps, so the order changes what is clickable and not what is drawn.
        PaintContent(p, content);

        if (_job is { } job) ScanView.Paint(p, Theme, _hits, content, job, _progress, _hover);

        PaintToolbar(p, toolbar);
        PaintStatus(p, status);
    }

    private void PaintToolbar(IPainter p, Rect area)
    {
        p.Fill(area, Theme.Surface);
        Draw.RuleBelow(p, Theme, area);

        var pad = p.Scale(14);
        var row = area.DropBottom(1);

        // The name, then the tabs. Left to right in reading order, because the tab strip is
        // the primary navigation and a user who has just opened the window looks there first.
        var brand = row.TakeLeft(p.Measure("pathmemo", FontRole.Bold) + pad * 2);
        p.Text(brand, "pathmemo", Theme.Accent, FontRole.Bold, Align.Centre);

        var rest = row.DropLeft(brand.Width);

        foreach (var view in Enum.GetValues<GuiView>())
        {
            var label = Label(view);
            var width = p.Measure(label, FontRole.Bold) + pad * 2;
            var tab = rest.TakeLeft(width);

            Draw.Tab(p, Theme, _hits, tab, label, (int)view,
                view == Active, _hover == new Hit(HitKind.Tab, (int)view));

            rest = rest.DropLeft(width);
        }

        // Actions from the right edge inwards, so adding one never moves the others.
        var right = rest;
        right = PaintAction(p, right, _job is null ? "Scan" : "Stop", GuiAction.Scan, primary: true);

        if (!Elevation.IsElevated)
            right = PaintAction(p, right, "As administrator", GuiAction.Elevate);

        PaintAction(p, right, "Config", GuiAction.OpenConfig);
    }

    private Rect PaintAction(IPainter p, Rect right, string label, GuiAction action, bool primary = false)
    {
        var width = p.Measure(label, FontRole.Body) + p.Scale(24);
        var margin = p.Scale(8);
        var area = right.TakeRight(width + margin).DropRight(margin).Deflate(0, p.Scale(8));

        Draw.Button(p, Theme, _hits, area, label, (int)action,
            _hover == new Hit(HitKind.Button, (int)action), enabled: true, primary: primary);

        return right.DropRight(width + margin);
    }

    private void PaintContent(IPainter p, Rect area)
    {
        switch (Active)
        {
            case GuiView.Overview:
                _overview.Paint(p, Theme, _hits, area, Report, _hover);
                break;

            case GuiView.Tree:
                _tree.Paint(p, Theme, _hits, area, _hover);
                break;

            default:
                Placeholder(p, area);
                break;
        }
    }

    /// <summary>
    /// What a view that is not built yet says.
    /// </summary>
    /// <remarks>
    /// Naming the command that does the same job today is the difference between an unfinished
    /// window and a dead end. The TUI made the same promise on its screens 4 and 5 before P7
    /// and P8 filled them in (README section 14.1).
    /// </remarks>
    private void Placeholder(IPainter p, Rect area)
    {
        var (title, hint) = Active switch
        {
            GuiView.Audit => ("Audit", "Not in the window yet. `pathmemo audit` finds the invisible space."),
            GuiView.Reclaim => ("Reclaim", "Not in the window yet. `pathmemo reclaim` lists what is worth deleting."),
            _ => ("Duplicates", "Not in the window yet. `pathmemo dupes` reads the files themselves."),
        };

        var middle = new Rect(area.X, area.Y + area.Height / 2 - p.LineHeight, area.Width, p.LineHeight);

        p.Text(middle, title, Theme.Text, FontRole.Title, Align.Centre);
        p.Text(middle.Offset(0, p.Height(FontRole.Title)), hint, Theme.Dim, FontRole.Body, Align.Centre);
    }

    private void PaintStatus(IPainter p, Rect area)
    {
        p.Fill(area, Theme.Surface);
        p.Fill(area.TakeTop(1), Theme.Border);

        var pad = p.Scale(14);
        var line = area.Deflate(pad, 0);

        var left = Message.Length > 0 ? Message : Describe();
        p.Text(line, left, Theme.Dim, FontRole.Small);

        var right = Elevation.IsElevated ? "administrator" : "standard rights";
        p.Text(line, right, Theme.Dim, FontRole.Small, Align.Right);
    }

    /// <summary>The scan behind the numbers on screen, and how old it is.</summary>
    private string Describe()
    {
        if (Report.DatabaseError is { } error) return error;
        if (Report.LastScan is not { } scan) return "No scan yet. Press Scan to look at this machine.";

        var age = DateTime.UtcNow - scan.StartedUtc;
        var when = age.TotalMinutes < 90 ? $"{(int)age.TotalMinutes} min ago"
            : age.TotalHours < 36 ? $"{(int)age.TotalHours} h ago"
            : $"{(int)age.TotalDays} days ago";

        return $"scan {scan.Id} · {when} · {scan.TotalFiles:N0} files · {scan.Scanner.ToString().ToLowerInvariant()}"
               + (scan.ErrorCount > 0 ? $" · {scan.ErrorCount:N0} unreadable" : "");
    }

    private static string Label(GuiView view) => view switch
    {
        GuiView.Overview => "Overview",
        GuiView.Tree => "Tree",
        GuiView.Audit => "Audit",
        GuiView.Reclaim => "Reclaim",
        _ => "Duplicates",
    };

    private void Mouse(MouseInput input)
    {
        switch (input.Kind)
        {
            case MouseKind.Leave:
                if (_hover == Hit.None) return;
                _hover = Hit.None;
                _pointerX = _pointerY = -1;
                _window.Invalidate();
                return;

            case MouseKind.Move:
                _pointerX = input.X;
                _pointerY = input.Y;

                var under = _hits.At(input.X, input.Y);

                // Repaint only when the answer changed: a move event arrives for every pixel,
                // and redrawing the frame for each is how a window starts to feel heavy.
                if (under == _hover) return;

                _hover = under;
                _window.Invalidate();
                return;

            case MouseKind.Wheel:
                if (Active == GuiView.Tree && _tree.Wheel(input.Wheel)) _window.Invalidate();
                return;

            case MouseKind.Down:
            case MouseKind.DoubleClick:
                Click(_hits.At(input.X, input.Y), input.Kind == MouseKind.DoubleClick);
                return;
        }
    }

    private void Click(Hit what, bool doubleClick)
    {
        switch (what.Kind)
        {
            case HitKind.Tab:
                Show((GuiView)what.Index);
                return;

            case HitKind.Button:
                Act((GuiAction)what.Index);
                return;

            case HitKind.Volume:
                // A volume card is a way into the tree, which is where its bytes are.
                OpenTree(Report.Volumes[what.Index].Letter);
                return;

            case HitKind.Row:
                if (Active == GuiView.Tree && _tree.Click(what.Index, doubleClick)) _window.Invalidate();
                else _window.Invalidate();
                return;

            case HitKind.Crumb:
                if (Active == GuiView.Tree && _tree.Crumb(what.Index)) _window.Invalidate();
                return;

            case HitKind.Block:
                if (Active == GuiView.Tree) _tree.ClickBlock(what.Index, doubleClick);
                _window.Invalidate();
                return;
        }
    }

    private void Key(KeyInput key)
    {
        if (key.Char is >= '1' and <= '5')
        {
            Show((GuiView)(key.Char - '1'));
            return;
        }

        // The size mode, on the same key the TUI uses, because a person who knows one should
        // not have to learn the other (README section 14.3).
        if (key.Char is 'm' or 'M' && Active == GuiView.Tree)
        {
            _tree.CycleMode();
            Say($"Sizes are now {_tree.ModeName}.");
            return;
        }

        switch (key.VirtualKey)
        {
            case Platform.Native.User32.VkF5:
                Act(GuiAction.Scan);
                return;
        }

        if (Active == GuiView.Tree && _tree.Key(key)) _window.Invalidate();
    }

    /// <summary>
    /// Opens the tree, loading the snapshot the first time it is asked for.
    /// </summary>
    /// <remarks>
    /// Loading is synchronous: a 1.58M-node snapshot opens in 181 ms (README section 20), which
    /// is a pause rather than a freeze, and moving it to a worker would mean a tree screen with
    /// a loading state, a cancellation and a race against the user switching tabs - all for a
    /// fifth of a second. A <b>scan</b> is the thing that takes 40 seconds, and that one does
    /// belong on a thread.
    /// </remarks>
    private void OpenTree(string? preferredLetter = null)
    {
        if (!_tree.HasSnapshot)
        {
            if (!SnapshotLoader.TryLoad(null, SnapshotParts.Tree | SnapshotParts.Volumes,
                    out var snapshot, out var id))
            {
                // Switch, not Show: Show would come straight back here, because what sends it
                // here is the absence of a snapshot and that is exactly what has just failed
                // to change. Until this was split in two the pair called each other until the
                // stack ran out, and a machine with nothing scanned yet lost the process the
                // moment the Tree tab was clicked (README section 24.6).
                Switch(GuiView.Tree);
                Say("No snapshot to browse yet. Press Scan.");
                return;
            }

            _tree.Load(snapshot, id, preferredLetter ?? Report.PrimaryLetter);
        }
        else if (preferredLetter is { Length: > 0 })
        {
            _tree.Load(_tree.Snapshot!, _tree.ScanId, preferredLetter);
        }

        Switch(GuiView.Tree);
    }

    /// <summary>
    /// Asks for a view, loading the tree first if that is what it takes.
    /// </summary>
    /// <remarks>
    /// The one place that decides. Everything it can decide to do ends in <see cref="Switch"/>,
    /// which decides nothing - and that is the whole point of there being two methods rather
    /// than one: a decision that can re-enter itself is a decision that can loop.
    /// </remarks>
    private void Show(GuiView view)
    {
        // Switching to the tree by tab or by key has to load it too, or the one route that
        // works is the volume card.
        if (view == GuiView.Tree && !_tree.HasSnapshot && Active != GuiView.Tree)
        {
            OpenTree();
            return;
        }

        Switch(view);
    }

    /// <summary>Makes a view the current one. Asks nothing and calls nothing back.</summary>
    private void Switch(GuiView view)
    {
        if (view == Active) return;

        Active = view;
        Message = "";

        // The hover answer belongs to the frame that registered it, and that frame is gone.
        _hover = Hit.None;
        _window.Invalidate();
    }

    private void Act(GuiAction action)
    {
        switch (action)
        {
            case GuiAction.Scan:
                StartOrStopScan();
                return;

            case GuiAction.Elevate:
                Elevate();
                return;

            case GuiAction.OpenConfig:
                Say(ConfigCommand.EnsureFile(out var error)
                    ? "Wrote a commented config.json and opened it."
                    : error ?? "Could not write the configuration file.");

                if (error is null) FileLaunch.TryOpen(Config.AppPaths.ConfigPath, out _);
                return;
        }
    }

    /// <summary>
    /// Starts a scan of every fixed volume, or stops the one that is running.
    /// </summary>
    /// <remarks>
    /// One button for both, because while a scan is running there is nothing else to press and
    /// a separate Stop that only appears sometimes is a button that moves. The roots are left
    /// empty, which <c>ScanCommand</c> reads as every fixed volume - the same default the
    /// command line has (README section 13.2).
    /// </remarks>
    private void StartOrStopScan()
    {
        if (_job is { } running)
        {
            running.Cancel();
            Say("Stopping - the partial result will be kept.");
            return;
        }

        var job = new ScanJob(_window, [.. Report.Volumes.Select(v => v.Letter)]);

        _job = job;
        _progress = null;
        Message = "";

        job.Start();
        _window.Invalidate();
    }

    /// <summary>
    /// Restarts this application with administrator rights (README sections 4.2, 15.4).
    /// </summary>
    /// <remarks>
    /// The manifest stays <c>asInvoker</c>, so the prompt appears only because it was asked
    /// for. The elevated copy is told to open a window rather than a terminal, or the button
    /// would answer a request for a GUI with a console.
    /// </remarks>
    private void Elevate()
    {
        if (_job is not null)
        {
            Say("Finish or stop the scan before restarting as administrator.");
            return;
        }

        if (Relaunch.AsAdministrator(["gui"]))
        {
            _window.Close();
            return;
        }

        Say("The elevation prompt was declined, so nothing changed.");
    }

    private void Say(string message)
    {
        Message = message;
        _window.Invalidate();
    }

    /// <summary>Re-reads everything the window shows. Cheap: tens of milliseconds (README section 20).</summary>
    internal void Refresh()
    {
        Report = StatusReport.Collect();
        _window.Invalidate();
    }

    /// <summary>Where the pointer is, for a view that needs it during paint.</summary>
    internal (int X, int Y) Pointer => (_pointerX, _pointerY);
}
