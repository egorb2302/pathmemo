using PathMemo.Analysis;
using PathMemo.Cli.Commands;
using PathMemo.Snapshots;
using PathMemo.Tui.Terminal;

namespace PathMemo.Tui;

/// <summary>Which of the five screens is in front (README section 14.1).</summary>
internal enum ViewKind
{
    Overview,
    Tree,
    Reclaim,
}

/// <summary>
/// A screen or a modal dialog: something that draws a frame and answers keys.
/// </summary>
/// <remarks>
/// One of the few interfaces the design allows, on the same rule as <c>IScanner</c> and
/// <c>IAuditProbe</c>: there are several real implementations and the host has to treat
/// them uniformly (README section 17.2).
/// </remarks>
internal interface ITuiView
{
    void Render(Screen screen, TuiSession session);

    /// <summary>True when the key was consumed; false lets the host try its global bindings.</summary>
    bool HandleKey(in TuiKey key, TuiSession session);
}

/// <summary>
/// State shared by the screens: the loaded snapshot, the display mode, the marks and the
/// one-line message at the bottom.
/// </summary>
internal sealed class TuiSession
{
    internal required StatusReport Report { get; set; }

    internal SnapshotContents? Snapshot { get; private set; }

    internal long ScanId { get; private set; }

    internal NodeStore Tree => Snapshot!.Tree;

    internal SizeMode Mode { get; set; } = SizeMode.Unique;

    /// <summary>
    /// Nodes the user marked, by index into the loaded snapshot. Cleared whenever the
    /// snapshot changes: an index means nothing against a different tree, and a deletion
    /// list that silently refers to other files is the worst kind of bug (P6).
    /// </summary>
    internal HashSet<int> Marks { get; } = [];

    /// <summary>
    /// The reclaim rules applied to the loaded snapshot, once the background pass has
    /// finished (README section 7.2). <see cref="ReclaimIndex.Empty"/> until then, so
    /// every caller can read it without asking whether it is ready.
    /// </summary>
    internal ReclaimIndex Reclaim =>
        _reclaim is { IsCompletedSuccessfully: true } done ? done.Result : ReclaimIndex.Empty;

    internal bool ReclaimReady => _reclaim is { IsCompleted: true };

    private Task<ReclaimIndex>? _reclaim;

    internal ViewKind Active { get; set; } = ViewKind.Overview;

    internal ITuiView? Modal { get; set; }

    internal bool Quit { get; set; }

    internal string Message { get; private set; } = "";

    internal Style MessageStyle { get; private set; } = Style.Dim;

    /// <summary>
    /// Work that needs the ordinary console: a rescan with its progress line, the audit
    /// view, an editor. The host leaves the alternate buffer, runs it, and comes back
    /// (README section 14.6 - nothing writes to stdout while the TUI is drawing).
    /// </summary>
    internal Action? Suspended { get; private set; }

    internal void Say(string text)
    {
        Message = text;
        MessageStyle = Style.Dim;
    }

    internal void Warn(string text)
    {
        Message = text;
        MessageStyle = Style.Warning;
    }

    internal void Clear() => Message = "";

    internal void Suspend(Action work) => Suspended = work;

    internal Action? TakeSuspended()
    {
        var work = Suspended;
        Suspended = null;
        return work;
    }

    /// <summary>
    /// Loads the newest readable snapshot, if it is not already in memory.
    /// </summary>
    /// <returns>False when there is nothing to browse; the message says why.</returns>
    internal bool EnsureSnapshot()
    {
        if (Snapshot is not null) return true;

        var entry = SnapshotStore.List().FirstOrDefault();
        if (entry is null)
        {
            Warn("No scans yet. Press F5 to scan this machine.");
            return false;
        }

        try
        {
            Load(SnapshotFile.Read(entry.Path), entry.Id);
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            Warn($"Scan {entry.Id} cannot be opened: {ex.Message}");
            return false;
        }
    }

    /// <summary>Puts a tree in front of the screens without going through the store.</summary>
    internal void Load(SnapshotContents snapshot, long scanId)
    {
        Snapshot = snapshot;
        ScanId = scanId;
        Marks.Clear();

        // Off the render thread: applying thirty rules to a million nodes is a few hundred
        // milliseconds, and the frame budget is sixteen (README section 20). The result is
        // immutable and published by the task, so the screens only ever read it.
        var tree = snapshot.Tree;
        _reclaim = Task.Run(() =>
        {
            try { return ReclaimIndex.Build(tree); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return ReclaimIndex.Empty;
            }
        });
    }

    /// <summary>Forgets the loaded tree, so the next screen entry picks up a newer scan.</summary>
    internal void Reload()
    {
        Snapshot = null;
        ScanId = 0;
        _reclaim = null;
        Marks.Clear();
        Report = StatusReport.Collect();
    }

    internal long MarkedBytes()
    {
        if (Snapshot is null) return 0;

        long total = 0;
        foreach (var node in Marks) total += TreeQuery.Size(Tree, node, Mode);
        return total;
    }

    internal string ModeName => Mode switch
    {
        SizeMode.Allocated => "allocated",
        SizeMode.Logical => "logical",
        _ => "unique",
    };

    internal void CycleMode()
    {
        Mode = Mode switch
        {
            SizeMode.Unique => SizeMode.Allocated,
            SizeMode.Allocated => SizeMode.Logical,
            _ => SizeMode.Unique,
        };

        Say(Mode switch
        {
            SizeMode.Allocated => "allocated: on-disk bytes, hard links counted once per name",
            SizeMode.Logical => "logical: stream bytes, what Explorer shows in properties",
            _ => "unique: on-disk bytes with hard links counted once - the total that adds up",
        });
    }
}
