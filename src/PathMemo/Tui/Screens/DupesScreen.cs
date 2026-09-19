using System.Globalization;
using PathMemo.Cli.Commands;
using PathMemo.Cli.Output;
using PathMemo.Config;
using PathMemo.Deletion;
using PathMemo.Duplicates;
using PathMemo.Storage;
using PathMemo.Tui.Dialogs;
using PathMemo.Text;
using PathMemo.Tui.Terminal;

namespace PathMemo.Tui.Screens;

/// <summary>
/// Screen 5: groups of identical files, and which copy survives
/// (README sections 8, 14.1).
/// </summary>
/// <remarks>
/// <para>
/// The only screen that shows something the snapshot does not contain, so it shows the
/// <b>last run</b> rather than starting one. A search reads every candidate file - minutes,
/// not milliseconds - and a screen that began doing that because somebody pressed <c>5</c>
/// would be indefensible. <c>r</c> starts one, in the ordinary console where the read
/// notice of README section 8.5 and its progress line belong (README section 14.6).
/// </para>
/// <para>
/// A mark here means "delete this copy", as it does on every other screen, and the screen
/// refuses to mark the last unmarked file of a group. That is README section 8.4's rule -
/// at least one file always survives - said in this screen's vocabulary; the core enforces
/// it again in <see cref="Keeper.Survives"/> before anything is handed to a deletion.
/// </para>
/// </remarks>
internal sealed class DupesScreen : ITuiView
{
    private readonly HashSet<string> _marks = new(StringComparer.OrdinalIgnoreCase);

    private DupeReport? _report;
    private bool _loaded;
    private bool _hardlinks;

    private int _selected;
    private int _scroll;
    private int _rows = 1;

    /// <summary>Null at the group level; an index into <see cref="Visible"/> while inside one.</summary>
    private int? _inside;

    internal void Reset()
    {
        _marks.Clear();
        _report = null;
        _loaded = false;
        _selected = 0;
        _scroll = 0;
        _inside = null;
    }

    /// <summary>Puts a run in front of the screen without the database, for tests.</summary>
    internal void Load(DupeReport report)
    {
        _report = report;
        _loaded = true;
        _marks.Clear();

        foreach (var group in report.Duplicates)
            foreach (var victim in group.Victims)
                _marks.Add(victim.Path);
    }

    public void Render(Screen screen, TuiSession session)
    {
        _rows = Math.Max(1, screen.Height - 7);

        Ensure(session);
        Header(screen);
        Draw.Rule(screen, 1);

        if (_report is null || _report.Groups.Count == 0)
        {
            Nothing(screen);
            Draw.Rule(screen, screen.Height - 3);
            Footer(screen, session, 0);
            return;
        }

        var groups = Visible();

        Summary(screen, groups);
        Draw.Rule(screen, 3);

        if (_inside is { } index && index < groups.Count) Files(screen, groups[index]);
        else Groups(screen, groups);

        Draw.Rule(screen, screen.Height - 3);
        Footer(screen, session, _inside is { } i && i < groups.Count ? groups[i].Count : groups.Count);
    }

    /// <summary>Reads the stored run once. A missing database is a message, not a crash.</summary>
    private void Ensure(TuiSession session)
    {
        if (_loaded) return;
        _loaded = true;

        try
        {
            using var catalog = ScanCatalog.TryOpen(out _);
            if (catalog is null) return;

            var stored = new DupeRepository(catalog.Database).Latest();
            if (stored is not null) Load(stored);
        }
        catch (Exception ex) when (ex is DatabaseException or Microsoft.Data.Sqlite.SqliteException)
        {
            session.Warn($"the stored duplicate run could not be read: {ex.Message}");
        }
    }

    private void Header(Screen screen)
    {
        var line = screen.Row(0).Space().Add("pathmemo", Style.Title).Space(2)
            .Add("Duplicates", Style.Strong);

        if (_report is { } report && report.Groups.Count > 0)
            line.Right(string.Format(CultureInfo.InvariantCulture, "{0}{2}{1:MM-dd HH:mm}",
                HashKinds.Name(report.Algorithm), report.RanAtUtc, Glyphs.Dot), Style.Dim);

        screen.Put(0, line);
    }

    private void Nothing(Screen screen)
    {
        screen.Put(2, screen.Row(2).Space().Add(
            _report is null
                ? "No duplicate search has been run yet."
                : "The last search found no duplicates.", Style.Dim));

        screen.Put(4, screen.Row(4).Space().Add(
            "Press r to run one. It reads every candidate file, so it takes minutes, not "
            + "milliseconds,", Style.Dim));

        screen.Put(5, screen.Row(5).Space().Add(
            "and it says how much it is about to read before it starts.", Style.Dim));
    }

    private void Summary(Screen screen, IReadOnlyList<DupeGroup> groups)
    {
        var report = _report!;
        var recoverable = report.Duplicates.Sum(g => g.Wasted);

        var line = screen.Row(2).Space();
        line.Add("Recoverable ", Style.Dim).Add(SizeFormat.Bytes(recoverable), Style.Strong)
            .Add("  in ", Style.Dim)
            .Add(report.Duplicates.Count().ToString("N0", CultureInfo.InvariantCulture))
            .Add(" groups, ", Style.Dim)
            .Add(report.Duplicates.Sum(g => g.Count).ToString("N0", CultureInfo.InvariantCulture))
            .Add(" files", Style.Dim);

        if (_marks.Count > 0)
            line.Add(Glyphs.Dot, Style.Dim).Add($"{_marks.Count} marked", Style.Mark);

        var sets = report.Hardlinks.Count();
        if (sets > 0)
            line.Right(_hardlinks ? $"with {sets} hard-link sets   (t)" : $"{sets} hard-link sets hidden   (t)",
                Style.Accent);

        screen.Put(2, line);
    }

    private void Groups(Screen screen, IReadOnlyList<DupeGroup> groups)
    {
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
            line.Add(group.Files.Any(f => _marks.Contains(f.Path)) ? Glyphs.Mark.ToString() : " ", Style.Mark);
            line.Space();

            line.Add(SizeFormat.Bytes(group.Bytes).PadLeft(9));
            line.Space().Add($"x{group.Count,-3}", Style.Dim);
            line.Space().Add(SizeFormat.Bytes(group.Wasted).PadLeft(9),
                group.Kind == DupeKind.HardlinkSet ? Style.Dim : Style.Good);

            line.Space(2);

            if (group.Kind == DupeKind.HardlinkSet) line.Add("link  ", Style.Warning);

            line.Add(TextWidth.Fit(
                PathDisplay.Shorten(Sanitizer.Clean(group.Keeper.Path), Math.Max(12, line.Remaining)),
                line.Remaining));

            screen.Put(y, line);
        }
    }

    private void Files(Screen screen, DupeGroup group)
    {
        Clamp(group.Count);

        for (var row = 0; row < _rows; row++)
        {
            var index = _scroll + row;
            if (index >= group.Count) break;

            var file = group.Files[index];
            var y = 4 + row;
            var line = screen.Row(y);
            line.Highlight = index == _selected;

            line.Add(index == _selected ? Glyphs.Cursor.ToString() : " ", Style.Accent);
            line.Add(_marks.Contains(file.Path) ? Glyphs.Mark.ToString() : " ", Style.Mark);
            line.Space();

            line.Add(_marks.Contains(file.Path) ? "delete" : file.Protected ? "kept  " : "keep  ",
                _marks.Contains(file.Path) ? Style.Warning : Style.Good);

            line.Space(2).Add(file.ModifiedUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), Style.Dim);
            line.Space(2);

            line.Add(TextWidth.Fit(
                PathDisplay.Shorten(Sanitizer.Clean(file.Path), Math.Max(12, line.Remaining)), line.Remaining));

            screen.Put(y, line);
        }
    }

    private void Footer(Screen screen, TuiSession session, int count)
    {
        var y = screen.Height - 2;
        var line = screen.Row(y).Space().Add(session.Message, session.MessageStyle);

        if (count > 0) line.Right($"{_selected + 1} of {count:N0}", Style.Dim);
        screen.Put(y, line);

        Draw.Hints(screen, screen.Height - 1,
            ("j/k", "move"), ("l", _inside is null ? "files" : "path"), ("h", "back"),
            ("x", "keep/delete"), ("d", "delete"), ("K", "keep"), ("r", "search"), ("?", "help"));
    }

    public bool HandleKey(in TuiKey key, TuiSession session)
    {
        if (key.Is('r')) return Search(session);

        if (_report is null || _report.Groups.Count == 0) return false;

        var groups = Visible();
        var count = _inside is { } index && index < groups.Count ? groups[index].Count : groups.Count;

        if (key.Is('j') || key.Is(ConsoleKey.DownArrow)) return Move(1, count);
        if (key.Is('k') || key.Is(ConsoleKey.UpArrow)) return Move(-1, count);
        if (key.IsCtrl('d') || key.Is(ConsoleKey.PageDown)) return Move(_rows, count);
        if (key.IsCtrl('u') || key.Is(ConsoleKey.PageUp)) return Move(-_rows, count);
        if (key.Is('g') || key.Is(ConsoleKey.Home)) return Move(int.MinValue / 2, count);
        if (key.Is('G') || key.Is(ConsoleKey.End)) return Move(int.MaxValue / 2, count);

        if (key.Is('l') || key.Is(ConsoleKey.RightArrow) || key.Is(ConsoleKey.Enter)) return Descend(session, groups);
        if (key.Is('h') || key.Is(ConsoleKey.LeftArrow) || key.Is(ConsoleKey.Backspace)) return Ascend(session);

        if (key.Is('t')) return ToggleHardlinks(session);
        if (key.Is('x') || key.Is(' ')) return Mark(session, groups);
        if (key.Is('X')) { _marks.Clear(); session.Say("marks cleared - nothing would be deleted"); return true; }
        if (key.Is('a')) return MarkAll(session, groups);
        if (key.Is('y')) return Copy(session, groups);
        if (key.Is('K')) return Keep(session, groups);
        if (key.Is('d')) return Delete(session, groups, null);
        if (key.Is('D')) return Delete(session, groups, DeleteMode.Permanent);

        return false;
    }

    private IReadOnlyList<DupeGroup> Visible() =>
        _report is null
            ? []
            : [.. _hardlinks ? _report.Groups : _report.Duplicates];

    /// <summary>
    /// <c>r</c>: a real search, in the ordinary console. The screens come back afterwards
    /// with whatever it stored (README section 14.6).
    /// </summary>
    private bool Search(TuiSession session)
    {
        session.Suspend(() =>
        {
            Console.WriteLine();
            Console.WriteLine("Searching for duplicates. This reads the candidate files themselves.");
            Console.WriteLine("Ctrl+C stops it and keeps the groups found so far.");

            DupesCommand.Run(new DupesOptions(), CancellationToken.None);
        });

        return true;
    }

    private bool Descend(TuiSession session, IReadOnlyList<DupeGroup> groups)
    {
        if (_inside is { } index)
        {
            if (index >= groups.Count || _selected >= groups[index].Count) return true;

            session.Say(groups[index].Files[_selected].Path);
            return true;
        }

        if (_selected >= groups.Count) return true;

        _inside = _selected;
        _selected = 0;
        _scroll = 0;
        session.Clear();
        return true;
    }

    private bool Ascend(TuiSession session)
    {
        if (_inside is not { } index)
        {
            session.Active = ViewKind.Overview;
            return true;
        }

        _inside = null;
        _selected = index;
        _scroll = 0;
        session.Clear();
        return true;
    }

    private bool ToggleHardlinks(TuiSession session)
    {
        _hardlinks = !_hardlinks;
        _inside = null;
        _selected = 0;
        _scroll = 0;

        session.Say(_hardlinks
            ? "hard-link sets included - deleting a name in one frees nothing (README section 3.2)"
            : "duplicates only");

        return true;
    }

    /// <summary>
    /// <c>x</c>: at a file, mark it for deletion; at a group, everything but its keeper.
    /// The last unmarked file of a group cannot be marked (README section 8.4).
    /// </summary>
    private bool Mark(TuiSession session, IReadOnlyList<DupeGroup> groups)
    {
        if (_inside is { } index)
        {
            if (index >= groups.Count) return true;

            var group = groups[index];
            if (_selected >= group.Count) return true;

            var file = group.Files[_selected];

            if (_marks.Contains(file.Path))
            {
                _marks.Remove(file.Path);
                Move(1, group.Count);
                return true;
            }

            if (group.Kind == DupeKind.HardlinkSet)
            {
                session.Warn("these are names for one file - deleting one frees nothing");
                return true;
            }

            if (file.Protected)
            {
                session.Warn("this path is on the keep list");
                return true;
            }

            if (file.LinkCount > 1)
            {
                session.Warn($"this file has {file.LinkCount} names - deleting one frees nothing");
                return true;
            }

            if (group.Files.Count(f => !_marks.Contains(f.Path)) <= 1)
            {
                session.Warn("something has to survive this group - unmark another copy first");
                return true;
            }

            _marks.Add(file.Path);
            Move(1, group.Count);
            return true;
        }

        if (_selected >= groups.Count) return true;

        var selected = groups[_selected];
        var already = selected.Files.Any(f => _marks.Contains(f.Path));

        foreach (var victim in selected.Victims)
        {
            if (already) _marks.Remove(victim.Path);
            else _marks.Add(victim.Path);
        }

        Move(1, groups.Count);
        return true;
    }

    private bool MarkAll(TuiSession session, IReadOnlyList<DupeGroup> groups)
    {
        var added = 0;

        foreach (var group in groups)
            foreach (var victim in group.Victims)
                if (_marks.Add(victim.Path)) added++;

        session.Say($"{added} marked, {_marks.Count} in total");
        return true;
    }

    private bool Copy(TuiSession session, IReadOnlyList<DupeGroup> groups)
    {
        var paths = Selected(groups);
        if (paths.Count == 0) return true;

        if (Platform.Clipboard.TrySetText(string.Join(Environment.NewLine, paths), out var error))
            session.Say($"{paths.Count} path{(paths.Count == 1 ? "" : "s")} copied");
        else
            session.Warn($"clipboard: {error}");

        return true;
    }

    private bool Keep(TuiSession session, IReadOnlyList<DupeGroup> groups)
    {
        var paths = Selected(groups);
        if (paths.Count != 1)
        {
            session.Warn("K adds one path to protect.keep - press l to choose a file first");
            return true;
        }

        if (ConfigFile.AddKeep(paths[0], out var message))
        {
            _marks.Remove(paths[0]);
            session.Say(message + " - it will not be offered again");
        }
        else
        {
            session.Warn(message);
        }

        return true;
    }

    /// <summary>The paths the cursor stands for: one file, or a group's keeper.</summary>
    private IReadOnlyList<string> Selected(IReadOnlyList<DupeGroup> groups)
    {
        if (_inside is { } index)
        {
            if (index >= groups.Count) return [];

            var group = groups[index];
            return _selected < group.Count ? [group.Files[_selected].Path] : [];
        }

        return _selected < groups.Count ? [groups[_selected].Keeper.Path] : [];
    }

    /// <summary>
    /// <c>d</c>: the marked copies, after every one of them has been proved a copy again.
    /// </summary>
    /// <remarks>
    /// The re-check is README section 8.4's, and it happens before the dialog rather than
    /// inside it: a confirmation that offers to delete a file whose contents have changed
    /// since the search is a confirmation of the wrong thing.
    /// </remarks>
    private bool Delete(TuiSession session, IReadOnlyList<DupeGroup> groups, DeleteMode? mode)
    {
        if (_marks.Count == 0)
        {
            session.Warn("nothing is marked - x marks the copies to delete");
            return true;
        }

        if (!Keeper.Survives(_report!.Groups, _marks, out var complaint))
        {
            session.Warn(complaint);
            return true;
        }

        session.Say($"re-checking {_marks.Count} copies against their survivors...");

        if (!DuplicateFinder.Reverify(_report.Groups, _marks, new DupeQuery { Algorithm = _report.Algorithm },
                                      null, out var mismatch))
        {
            session.Warn(mismatch + " - press r to search again");
            return true;
        }

        var engine = DeleteEngine.Create();
        session.Modal = new DeleteDialog(engine, [.. _marks], mode ?? engine.Config.Delete.DefaultMode,
            DeleteSource.Dupes);

        return true;
    }

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
