using PathMemo.Analysis;
using PathMemo.Snapshots;

namespace PathMemo.Gui.Work;

/// <summary>
/// The reclaim rules running over a snapshot behind the window (README section 24.4).
/// </summary>
/// <remarks>
/// <para>
/// Applying thirty rules to a million nodes is a few hundred milliseconds, and a frame is
/// sixteen (README section 20), so the pass runs on a worker and posts once when it is done -
/// the same bargain the terminal screens make in <c>TuiSession.Load</c>. There is no progress
/// to report, only a result, so this is <see cref="ScanJob"/> with the coalescing left out.
/// </para>
/// <para>
/// <b>Nothing here touches the window.</b> The index is left in a field and a message is
/// posted; the shell collects it on the UI thread. A job that was cancelled - because a scan
/// replaced the snapshot it was reading - posts nothing, and the shell drops its reference
/// before the worker could answer, so a late result has nobody to be delivered to.
/// </para>
/// </remarks>
internal sealed class ReclaimJob
{
    /// <summary>Posted once, when the index is ready to read.</summary>
    internal const uint ReadyMessage = 3;

    private readonly IShellWindow _window;
    private readonly NodeStore _tree;
    private readonly CancellationTokenSource _cancel = new();

    private ReclaimIndex? _result;

    internal ReclaimJob(IShellWindow window, NodeStore tree, long scanId)
    {
        _window = window;
        _tree = tree;
        ScanId = scanId;
    }

    /// <summary>The scan the index describes, so a result cannot be shown against another tree.</summary>
    internal long ScanId { get; }

    internal void Start()
    {
        var token = _cancel.Token;

        _ = Task.Run(() =>
        {
            ReclaimIndex index;

            try
            {
                index = ReclaimIndex.Build(_tree, token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The rules read config.json; a file that cannot be read means no custom
                // rules, not no view. The terminal screens make the same choice.
                index = ReclaimIndex.Empty;
            }

            Volatile.Write(ref _result, index);

            if (!token.IsCancellationRequested) _window.Post(ReadyMessage);
        });
    }

    /// <summary>The finished index, or null if the pass has not finished. Read on the UI thread.</summary>
    internal ReclaimIndex? Take() => Volatile.Read(ref _result);

    internal void Cancel() => _cancel.Cancel();

    internal void Dispose() => _cancel.Dispose();
}
