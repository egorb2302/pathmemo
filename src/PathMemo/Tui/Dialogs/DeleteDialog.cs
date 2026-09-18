using PathMemo.Analysis;
using PathMemo.Cli.Commands;
using PathMemo.Cli.Output;
using PathMemo.Deletion;
using PathMemo.Tui.Terminal;

namespace PathMemo.Tui.Dialogs;

/// <summary>
/// The delete dialog: what will happen, in whose words, before it happens
/// (README sections 9.2, 14.3).
/// </summary>
/// <remarks>
/// <para>
/// It shows the plan the engine computed - the same plan <c>rm --dry-run</c> prints - so
/// the screen cannot describe one thing and the operation do another. <c>Tab</c> cycles the
/// mode and every line updates with it, because "this frees no space until you purge" is
/// the single most surprising fact about deleting on Windows (README section 9.1).
/// </para>
/// <para>
/// <c>Enter</c> proceeds and <c>y</c> does not: this dialog appears where the user was
/// already pressing keys, and a confirmation that a stray keystroke can answer is not one.
/// The permanent mode over the size threshold hands off to the console, where the phrase
/// has to be typed out.
/// </para>
/// </remarks>
internal sealed class DeleteDialog : ITuiView
{
    private readonly DeleteEngine _engine;
    private readonly IReadOnlyList<string> _paths;
    private readonly DeleteSource _source;
    private DeletePlan _plan;
    private bool _listing;
    private int _scroll;

    internal DeleteDialog(DeleteEngine engine, IReadOnlyList<string> paths, DeleteMode mode,
                          DeleteSource source = DeleteSource.Tree)
    {
        _engine = engine;
        _paths = paths;
        _source = source;
        _plan = engine.Plan(new DeleteRequest { Paths = paths, Mode = mode, Source = source });
    }

    public void Render(Screen screen, TuiSession session)
    {
        if (_listing)
        {
            RenderList(screen);
            return;
        }

        var rows = new List<Draw.PanelRow>();

        if (_plan.IsEmpty)
        {
            rows.Add(new Draw.PanelRow("Nothing here can be deleted.", Style.Warning));
            rows.Add(new Draw.PanelRow(""));

            foreach (var refusal in _plan.Refusals.Take(6))
                rows.Add(new Draw.PanelRow($"{Shorten(refusal.Path)}  -  {refusal.Reason}", Style.Dim));

            Draw.Panel(screen, "Delete", rows, "Esc close");
            return;
        }

        rows.Add(new Draw.PanelRow($"Mode:   {DeleteModes.Name(_plan.Mode)}  ({Explain(_plan.Mode)})"));
        if (_plan.ModeReason is { } reason) rows.Add(new Draw.PanelRow("        " + reason, Style.Warning));

        rows.Add(new Draw.PanelRow($"Frees now:      {(_plan.FreesNow == 0 ? "0 bytes" : SizeFormat.Bytes(_plan.FreesNow))}"));

        if (_plan.Mode == DeleteMode.Quarantine)
            rows.Add(new Draw.PanelRow($"Frees on purge: {SizeFormat.Bytes(_plan.TotalBytes)}"));
        else if (_plan.Mode == DeleteMode.Recycle)
            rows.Add(new Draw.PanelRow($"Frees on empty: {SizeFormat.Bytes(_plan.TotalBytes)}"));

        rows.Add(new Draw.PanelRow("Undo:   " + _plan.Mode switch
        {
            DeleteMode.Quarantine => "pathmemo restore <op-id>   (until purged)",
            DeleteMode.Recycle => "Explorer -> Recycle Bin -> Restore",
            _ => "none",
        }, _plan.Mode == DeleteMode.Permanent ? Style.Warning : Style.Plain));

        if (_plan.Refusals.Count > 0)
        {
            rows.Add(new Draw.PanelRow(""));
            rows.Add(new Draw.PanelRow($"{_plan.Refusals.Count} refused by the guard - [L] to see why", Style.Dim));
        }

        // The rule that claims these paths, when one does: "safe - the application
        // recreates it" is the single most useful sentence at this moment (README section 7.1).
        if (_engine.RiskOf(_plan) is { RuleId: not null } judged)
        {
            rows.Add(new Draw.PanelRow(""));
            rows.Add(new Draw.PanelRow($"Rule:   {judged.RuleId} ({ReclaimNames.Of(judged.Risk)})",
                judged.Risk == Audit.Risk.Safe ? Style.Good : Style.Warning));
        }

        if (_engine.NeedsTypedConfirmation(_plan))
        {
            rows.Add(new Draw.PanelRow(""));
            rows.Add(new Draw.PanelRow(
                _engine.RiskOf(_plan).Risk == Audit.Risk.Danger
                    ? "A rule calls this dangerous: you will type the phrase in the console."
                    : $"Permanent and over {SizeFormat.Bytes(_engine.Config.Delete.RequireTypedConfirmationOverBytes)}: "
                      + "you will type the phrase in the console.", Style.Warning));
        }

        var title = $"Delete {_plan.Items.Count} item{(_plan.Items.Count == 1 ? "" : "s")} · {SizeFormat.Bytes(_plan.TotalBytes)}";
        Draw.Panel(screen, title, rows, "Tab mode   Enter proceed   L list   Esc cancel");
    }

    private void RenderList(Screen screen)
    {
        var rows = new List<Draw.PanelRow>();
        var height = Math.Max(4, screen.Height - 8);

        foreach (var item in _plan.Items.OrderByDescending(i => i.Bytes).Skip(_scroll).Take(height))
            rows.Add(new Draw.PanelRow($"{SizeFormat.Bytes(item.Bytes),9}  {Shorten(item.DisplayPath)}"));

        foreach (var refusal in _plan.Refusals.Skip(Math.Max(0, _scroll - _plan.Items.Count)).Take(height - rows.Count))
            rows.Add(new Draw.PanelRow($"  refused  {Shorten(refusal.Path)}  -  {refusal.Reason}", Style.Dim));

        Draw.Panel(screen, "Items", rows, "j/k scroll   Esc back");
    }

    public bool HandleKey(in TuiKey key, TuiSession session)
    {
        if (_listing)
        {
            if (key.Is('j') || key.Is(ConsoleKey.DownArrow)) _scroll++;
            else if (key.Is('k') || key.Is(ConsoleKey.UpArrow)) _scroll = Math.Max(0, _scroll - 1);
            else _listing = false;

            _scroll = Math.Clamp(_scroll, 0, Math.Max(0, _plan.Items.Count + _plan.Refusals.Count - 1));
            return true;
        }

        if (key.Is(ConsoleKey.Escape) || key.Is('q'))
        {
            session.Modal = null;
            session.Say("cancelled");
            return true;
        }

        if (key.Is('l') || key.Is('L'))
        {
            if (!_plan.IsEmpty || _plan.Refusals.Count > 0) _listing = true;
            return true;
        }

        if (key.Is(ConsoleKey.Tab))
        {
            // Recomputed rather than patched: the mode changes which items are even
            // possible, and the numbers on screen must come from the plan that would run.
            var next = _plan.Mode switch
            {
                DeleteMode.Quarantine => DeleteMode.Permanent,
                DeleteMode.Permanent => DeleteMode.Recycle,
                _ => DeleteMode.Quarantine,
            };

            _plan = _engine.Plan(new DeleteRequest { Paths = _paths, Mode = next, Source = _source });
            return true;
        }

        if (key.Is(ConsoleKey.Enter) && !_plan.IsEmpty)
        {
            var plan = _plan;
            session.Modal = null;

            // Out to the ordinary console: the confirmation, the progress line and the
            // free-space report all belong to a terminal that scrolls, and nothing writes
            // to stdout while the screens are up (README section 14.6).
            session.Suspend(() => RmCommand.Run(new RmOptions
            {
                Paths = [.. plan.Items.Select(i => i.DisplayPath)],
                Mode = plan.Mode,
                Reason = _source == DeleteSource.Reclaim ? "from the reclaim screen" : "from the tree screen",
            }, CancellationToken.None, _source));

            return true;
        }

        return true;
    }

    private static string Shorten(string path) => PathDisplay.Shorten(Sanitizer.Clean(path), 56);

    private static string Explain(DeleteMode mode) => mode switch
    {
        DeleteMode.Quarantine => "moved aside, freed after purge",
        DeleteMode.Recycle => "into the Recycle Bin, frees nothing now",
        _ => "unlinked now, nothing brings it back",
    };
}
