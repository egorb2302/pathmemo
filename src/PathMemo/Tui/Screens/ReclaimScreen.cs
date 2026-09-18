using System.Globalization;
using PathMemo.Analysis;
using PathMemo.Audit;
using PathMemo.Cli.Output;
using PathMemo.Config;
using PathMemo.Deletion;
using PathMemo.Tui.Dialogs;
using PathMemo.Tui.Terminal;

namespace PathMemo.Tui.Screens;

/// <summary>
/// Screen 4: what the rules recommend, grouped by rule (README sections 7, 14.1).
/// </summary>
/// <remarks>
/// <para>
/// Two levels, not a tree: rules, and the paths behind one rule. That is the shape of the
/// decision - a person agrees to "all the node_modules" or picks three of them, and never
/// to something in between that a nested view would invite.
/// </para>
/// <para>
/// Marks are per path and survive moving between the two levels, so marking inside two
/// different rules and deleting once is one operation and one journal entry
/// (README section 9.7). The delete dialog is the same one the tree uses, so the guard,
/// the mode and the undo line are described identically wherever the user arrived from.
/// </para>
/// </remarks>
internal sealed class ReclaimScreen : ITuiView
{
    private readonly HashSet<string> _marks = new(StringComparer.OrdinalIgnoreCase);

    private int _selected;
    private int _scroll;
    private int _rows = 1;

    /// <summary>Null at the rule level; a rule id while its paths are listed.</summary>
    private string? _inside;

    private Risk _ceiling = Risk.Caution;

    internal void Reset()
    {
        _marks.Clear();
        _selected = 0;
        _scroll = 0;
        _inside = null;
    }

    public void Render(Screen screen, TuiSession session)
    {
        _rows = Math.Max(1, screen.Height - 7);

        if (session.Snapshot is null)
        {
            screen.Put(2, screen.Row(2).Space(2).Add("No scan is loaded.", Style.Warning));
            Draw.Hints(screen, screen.Height - 1, ("F5", "scan"), ("1", "overview"), ("Q", "quit"));
            return;
        }

        Header(screen, session);
        Draw.Rule(screen, 1);

        if (!session.ReclaimReady)
        {
            screen.Put(2, screen.Row(2).Space()
                .Add($"Applying {RuleSet.Current().Count} rules to scan {session.ScanId}...", Style.Dim));
            Draw.Hints(screen, screen.Height - 1, ("q", "back"), ("?", "help"));
            return;
        }

        var groups = Visible(session);

        Summary(screen, session, groups);
        Draw.Rule(screen, 3);

        if (_inside is null) Rules(screen, session, groups);
        else Paths(screen, session);

        Draw.Rule(screen, screen.Height - 3);
        Footer(screen, session, groups);
    }

    private void Header(Screen screen, TuiSession session)
    {
        var right = string.Format(CultureInfo.InvariantCulture, "scan {0}{2}{1:MM-dd HH:mm}",
            session.ScanId, session.Snapshot!.StartedUtc, Glyphs.Dot);

        var line = screen.Row(0).Space().Add("pathmemo", Style.Title).Space(2)
            .Add(_inside is null ? "Reclaim" : "Reclaim " + Glyphs.Dot + " " + _inside, Style.Strong);

        line.Right(right, Style.Dim);
        screen.Put(0, line);
    }

    private void Summary(Screen screen, TuiSession session, IReadOnlyList<ReclaimGroup> groups)
    {
        var line = screen.Row(2).Space();
        var offered = groups.Where(g => g.Rule.Action == ReclaimAction.Delete).Sum(g => g.Reclaimable);

        line.Add("Reclaimable ", Style.Dim).Add(SizeFormat.Bytes(offered), Style.Strong)
            .Add("  in ", Style.Dim)
            .Add(groups.Sum(g => g.Count).ToString("N0", CultureInfo.InvariantCulture))
            .Add($" places, {groups.Count} rules", Style.Dim);

        if (_marks.Count > 0)
            line.Add(Glyphs.Dot, Style.Dim).Add($"{_marks.Count} marked", Style.Mark);

        line.Right($"up to {ReclaimNames.Of(_ceiling)}   (t)", Style.Accent);
        screen.Put(2, line);
    }

    private void Rules(Screen screen, TuiSession session, IReadOnlyList<ReclaimGroup> groups)
    {
        if (groups.Count == 0)
        {
            screen.Put(4, screen.Row(4).Space(3).Add(
                session.Reclaim.MatchCount == 0
                    ? "No rule matched anything in this scan."
                    : "Nothing at this risk level - press t to include more.", Style.Dim));
            return;
        }

        Clamp(groups.Count);

        for (var row = 0; row < _rows; row++)
        {
            var index = _scroll + row;
            if (index >= groups.Count) break;

            var group = groups[index];
            var y = 4 + row;
            var line = screen.Row(y);
            line.Highlight = index == _selected;

            line.Add(index == _selected ? Glyphs.Cursor.ToString() : " ", Style.Accent);
            line.Add(MarkedIn(group) ? Glyphs.Mark.ToString() : " ", Style.Mark);
            line.Space();

            line.Add(SizeFormat.Bytes(group.Reclaimable).PadLeft(9),
                group.Rule.Action == ReclaimAction.Delete ? Style.Plain : Style.Dim);

            line.Space(2).Add(TextWidth.Fit(group.Rule.Id, 24), Style.Strong);
            line.Space().Add(TextWidth.Fit($"{ReclaimNames.Of(group.Rule.Risk)} "
                                         + $"{ReclaimNames.Of(group.Rule.Recoverability)}", 22),
                group.Rule.Risk == Risk.Safe ? Style.Good : Style.Warning);

            line.Add(TextWidth.Fit(Note(group), Math.Max(0, line.Remaining - 8)), Style.Dim);
            line.Right(group.Count.ToString("N0", CultureInfo.InvariantCulture), Style.Dim);

            screen.Put(y, line);
        }
    }

    private void Paths(Screen screen, TuiSession session)
    {
        var matches = MatchesOf(session, _inside!);
        if (matches.Count == 0) return;

        Clamp(matches.Count);

        for (var row = 0; row < _rows; row++)
        {
            var index = _scroll + row;
            if (index >= matches.Count) break;

            var match = matches[index];
            var y = 4 + row;
            var line = screen.Row(y);
            line.Highlight = index == _selected;

            line.Add(index == _selected ? Glyphs.Cursor.ToString() : " ", Style.Accent);
            line.Add(_marks.Contains(match.Path) ? Glyphs.Mark.ToString() : " ", Style.Mark);
            line.Space();

            line.Add(SizeFormat.Bytes(match.Reclaimable).PadLeft(9));
            line.Space(2).Add(match.ModifiedUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), Style.Dim);
            line.Space(2);

            line.Add(TextWidth.Fit(
                PathDisplay.Shorten(Sanitizer.Clean(match.Path), Math.Max(12, line.Remaining)), line.Remaining));

            screen.Put(y, line);
        }
    }

    private void Footer(Screen screen, TuiSession session, IReadOnlyList<ReclaimGroup> groups)
    {
        var y = screen.Height - 2;
        var line = screen.Row(y).Space().Add(session.Message, session.MessageStyle);

        var count = _inside is null ? groups.Count : MatchesOf(session, _inside).Count;
        if (count > 0) line.Right($"{_selected + 1} of {count:N0}", Style.Dim);

        screen.Put(y, line);

        Draw.Hints(screen, screen.Height - 1,
            ("j/k", "move"), ("l", _inside is null ? "paths" : "details"), ("h", "back"),
            ("x", "mark"), ("d", "delete"), ("K", "keep"), ("t", "risk"), ("?", "help"));
    }

    public bool HandleKey(in TuiKey key, TuiSession session)
    {
        if (session.Snapshot is null || !session.ReclaimReady) return false;

        var groups = Visible(session);
        var count = _inside is null ? groups.Count : MatchesOf(session, _inside).Count;

        if (key.Is('j') || key.Is(ConsoleKey.DownArrow)) return Move(1, count);
        if (key.Is('k') || key.Is(ConsoleKey.UpArrow)) return Move(-1, count);
        if (key.IsCtrl('d') || key.Is(ConsoleKey.PageDown)) return Move(_rows, count);
        if (key.IsCtrl('u') || key.Is(ConsoleKey.PageUp)) return Move(-_rows, count);
        if (key.Is('g') || key.Is(ConsoleKey.Home)) return Move(int.MinValue / 2, count);
        if (key.Is('G') || key.Is(ConsoleKey.End)) return Move(int.MaxValue / 2, count);

        if (key.Is('l') || key.Is(ConsoleKey.RightArrow) || key.Is(ConsoleKey.Enter)) return Descend(session, groups);
        if (key.Is('h') || key.Is(ConsoleKey.LeftArrow) || key.Is(ConsoleKey.Backspace)) return Ascend(session);

        if (key.Is('t')) return CycleCeiling(session);
        if (key.Is('x') || key.Is(' ')) return Mark(session, groups);
        if (key.Is('X')) { _marks.Clear(); session.Say("marks cleared"); return true; }
        if (key.Is('a')) return MarkAll(session, groups);

        if (key.Is('y')) return Copy(session, groups);
        if (key.Is('K')) return Keep(session, groups);
        if (key.Is('d')) return Delete(session, groups, null);
        if (key.Is('D')) return Delete(session, groups, DeleteMode.Permanent);

        if (key.Is('r'))
        {
            session.Say("the rules are recomputed when a scan is loaded - F5 to rescan");
            return true;
        }

        return false;
    }

    /// <summary>The groups the current risk ceiling allows, largest first.</summary>
    private IReadOnlyList<ReclaimGroup> Visible(TuiSession session) =>
        [.. session.Reclaim.Groups.Where(g => g.Rule.Risk <= _ceiling)];

    private static IReadOnlyList<ReclaimMatch> MatchesOf(TuiSession session, string ruleId) =>
        session.Reclaim.Groups
            .FirstOrDefault(g => g.Rule.Id.Equals(ruleId, StringComparison.OrdinalIgnoreCase))
            ?.Matches ?? [];

    private bool Descend(TuiSession session, IReadOnlyList<ReclaimGroup> groups)
    {
        if (_inside is not null)
        {
            // At the path level, going further in means the tree: the file is a real place
            // on the disk and the tree is where places are looked at.
            var matches = MatchesOf(session, _inside);
            if (_selected >= matches.Count) return true;

            session.Say(matches[_selected].Path);
            return true;
        }

        if (groups.Count == 0 || _selected >= groups.Count) return true;

        _inside = groups[_selected].Rule.Id;
        _selected = 0;
        _scroll = 0;
        session.Clear();
        return true;
    }

    private bool Ascend(TuiSession session)
    {
        if (_inside is null)
        {
            session.Active = ViewKind.Overview;
            return true;
        }

        _inside = null;
        _selected = 0;
        _scroll = 0;
        session.Clear();
        return true;
    }

    private bool CycleCeiling(TuiSession session)
    {
        _ceiling = _ceiling switch
        {
            Risk.Safe => Risk.Caution,
            Risk.Caution => Risk.Danger,
            _ => Risk.Safe,
        };

        _inside = null;
        _selected = 0;
        _scroll = 0;

        session.Say(_ceiling switch
        {
            Risk.Safe => "safe only: the application recreates it, nothing is lost",
            Risk.Caution => "up to caution: a capability or a way back may be lost",
            _ => "everything, including what may break an application",
        });

        return true;
    }

    private bool Mark(TuiSession session, IReadOnlyList<ReclaimGroup> groups)
    {
        foreach (var path in Selected(session, groups))
        {
            if (!_marks.Add(path)) _marks.Remove(path);
        }

        Move(1, _inside is null ? groups.Count : MatchesOf(session, _inside).Count);
        return true;
    }

    private bool MarkAll(TuiSession session, IReadOnlyList<ReclaimGroup> groups)
    {
        var added = 0;

        if (_inside is null)
        {
            foreach (var group in groups.Where(g => g.Rule.Action == ReclaimAction.Delete))
                foreach (var match in group.Matches)
                    if (_marks.Add(match.Path)) added++;
        }
        else
        {
            foreach (var match in MatchesOf(session, _inside))
                if (_marks.Add(match.Path)) added++;
        }

        session.Say($"{added} marked, {_marks.Count} in total");
        return true;
    }

    /// <summary>The paths the cursor stands for: one path, or a whole rule's worth.</summary>
    private IReadOnlyList<string> Selected(TuiSession session, IReadOnlyList<ReclaimGroup> groups)
    {
        if (_inside is { } rule)
        {
            var matches = MatchesOf(session, rule);
            return _selected < matches.Count ? [matches[_selected].Path] : [];
        }

        if (_selected >= groups.Count) return [];

        var group = groups[_selected];
        return group.Rule.Action == ReclaimAction.Delete
            ? [.. group.Matches.SelectMany(ReclaimPlanner.TargetsOf)]
            : [];
    }

    private bool Copy(TuiSession session, IReadOnlyList<ReclaimGroup> groups)
    {
        var paths = Selected(session, groups);
        if (paths.Count == 0) return true;

        if (Platform.Clipboard.TrySetText(string.Join(Environment.NewLine, paths), out var error))
            session.Say($"{paths.Count} path{(paths.Count == 1 ? "" : "s")} copied");
        else
            session.Warn($"clipboard: {error}");

        return true;
    }

    /// <summary>
    /// <c>K</c>: never recommend this again (README sections 7.4, 14.3). At the rule level
    /// it disables the rule; at a path it adds that path to <c>protect.keep</c>.
    /// </summary>
    private bool Keep(TuiSession session, IReadOnlyList<ReclaimGroup> groups)
    {
        if (_inside is { } rule)
        {
            var matches = MatchesOf(session, rule);
            if (_selected >= matches.Count) return true;

            var path = matches[_selected].Path;
            if (ConfigFile.AddKeep(path, out var message)) session.Say(message + " - F5 to reapply");
            else session.Warn(message);

            return true;
        }

        if (_selected >= groups.Count) return true;

        var id = groups[_selected].Rule.Id;
        if (ConfigFile.Disable(id, out var why)) session.Say(why + " - F5 to reapply");
        else session.Warn(why);

        return true;
    }

    private bool Delete(TuiSession session, IReadOnlyList<ReclaimGroup> groups, DeleteMode? mode)
    {
        var paths = _marks.Count > 0 ? [.. _marks] : Selected(session, groups);

        if (paths.Count == 0)
        {
            session.Warn(_inside is null && _selected < groups.Count
                ? $"{groups[_selected].Rule.Id} is not deleted by pathmemo - {Note(groups[_selected])}"
                : "nothing selected");
            return true;
        }

        var engine = DeleteEngine.Create();
        session.Modal = new DeleteDialog(engine, paths, mode ?? engine.Config.Delete.DefaultMode,
            DeleteSource.Reclaim);

        return true;
    }

    private bool MarkedIn(ReclaimGroup group) => group.Matches.Any(m => _marks.Contains(m.Path));

    private static string Note(ReclaimGroup group) => group.Rule.Action switch
    {
        ReclaimAction.Command => "use " + group.Rule.Command,
        ReclaimAction.Manual => "decide by hand",
        _ => group.Rule.What,
    };

    private bool Move(int delta, int count)
    {
        if (count == 0) return true;

        _selected = Math.Clamp(_selected + delta, 0, count - 1);
        EnsureVisible(count);
        return true;
    }

    private void Clamp(int count)
    {
        _selected = Math.Clamp(_selected, 0, Math.Max(0, count - 1));
        EnsureVisible(count);
    }

    private void EnsureVisible(int count)
    {
        if (_selected < _scroll) _scroll = _selected;
        if (_selected >= _scroll + _rows) _scroll = _selected - _rows + 1;
        _scroll = Math.Clamp(_scroll, 0, Math.Max(0, count - 1));
    }
}
