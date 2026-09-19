using PathMemo.Cli.Commands;
using PathMemo.Scanning;

namespace PathMemo.Gui.Work;

/// <summary>
/// A scan running behind the window (README section 24.4).
/// </summary>
/// <remarks>
/// <para>
/// A walk of C: takes 40 seconds (README section 20), which is the difference between a
/// window and a hung window, so the scan runs on a worker and the message loop keeps turning.
/// This is the one thing the terminal interface never had to solve: the TUI hands the console
/// back and lets the scan print to it (README section 14.7), and a window has nothing to hand
/// back.
/// </para>
/// <para>
/// <b>Nothing here touches the window.</b> A UI object may only be read and written on the
/// thread that owns the message loop, so progress is left in a field and a message is posted;
/// the window then comes and collects the latest value. Posting the value itself would need it
/// to survive as a handle across the boundary, and posting one message per directory would
/// flood a queue that a 1.2 million file scan reports into thousands of times.
/// </para>
/// <para>
/// The posts are <b>coalesced</b>: while one is outstanding no other is sent. What the window
/// reads is therefore always the most recent progress and never a backlog of stale ones, which
/// is what a progress display wants anyway - nobody needs to see the counter that was true
/// four hundred milliseconds ago.
/// </para>
/// </remarks>
internal sealed class ScanJob : IProgress<ScanProgress>
{
    /// <summary>Posted when there is fresh progress to read.</summary>
    internal const uint ProgressMessage = 1;

    /// <summary>Posted once, when the scan has finished, failed or been cancelled.</summary>
    internal const uint FinishedMessage = 2;

    private readonly GuiWindow _window;
    private readonly CancellationTokenSource _cancel = new();

    private ScanProgress? _latest;
    private int _pending;

    internal ScanJob(GuiWindow window, IReadOnlyList<string> roots)
    {
        _window = window;
        Roots = roots;
        Started = DateTime.UtcNow;
    }

    internal IReadOnlyList<string> Roots { get; }

    internal DateTime Started { get; }

    internal bool Running { get; private set; } = true;

    internal bool Cancelling => _cancel.IsCancellationRequested;

    /// <summary>What went wrong, or null. Read on the UI thread after <see cref="FinishedMessage"/>.</summary>
    internal string? Error { get; private set; }

    internal int ExitCode { get; private set; }

    internal void Start()
    {
        var options = new ScanOptions
        {
            Roots = Roots,

            // The window offers administrator rights on its own toolbar; a console prompt
            // from a background thread would be a question nobody can see or answer.
            NoElevate = true,

            // No progress line and no summary into a console the window has hidden.
            Quiet = true,
        };

        _ = Task.Run(async () =>
        {
            try
            {
                ExitCode = await ScanCommand.RunAsync(options, _cancel.Token, this);
            }
            catch (OperationCanceledException)
            {
                // A cancelled scan still wrote a partial snapshot and a 'cancelled' row
                // (README section 4.8); it is an outcome, not a failure.
                ExitCode = Cli.ExitCode.Cancelled;
            }
            catch (Exception ex)
            {
                Error = ex.Message;
                ExitCode = Cli.ExitCode.Failure;
            }
            finally
            {
                Running = false;
                _window.Post(FinishedMessage);
            }
        });
    }

    /// <summary>
    /// Asks the scan to stop. The first press is graceful, which keeps what has been read so
    /// far - the same bargain Ctrl+C makes in the terminal (README section 4.8).
    /// </summary>
    internal void Cancel()
    {
        if (!Running) return;

        _cancel.Cancel();
    }

    public void Report(ScanProgress value)
    {
        Volatile.Write(ref _latest, value);

        // One outstanding post at a time. Exchange returns the old value, so only the thread
        // that found it clear does the posting.
        if (Interlocked.Exchange(ref _pending, 1) == 0) _window.Post(ProgressMessage);
    }

    /// <summary>The most recent progress. Called on the UI thread when the post arrives.</summary>
    internal ScanProgress? Take()
    {
        Volatile.Write(ref _pending, 0);
        return Volatile.Read(ref _latest);
    }

    internal void Dispose() => _cancel.Dispose();
}
