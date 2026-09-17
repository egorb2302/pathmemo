using System.Globalization;
using System.Text;
using PathMemo.Analysis;
using PathMemo.Cli.Output;
using PathMemo.Config;
using PathMemo.Platform;
using PathMemo.Snapshots;
using PathMemo.Tui.Dialogs;
using PathMemo.Tui.Terminal;

namespace PathMemo.Tui.Screens;

/// <summary>What the rows are ordered by (README section 14.3, the <c>s</c> menu).</summary>
internal enum SortKey
{
    Size,
    Name,
    Files,
    Modified,
}

/// <summary>Which rows are listed at all (the <c>t</c> key).</summary>
internal enum RowFilter
{
    All,
    Directories,
    Files,
}

/// <summary>
/// Screen 2: the tree. The main screen, and most of the value of the product
/// (README section 14.2).
/// </summary>
/// <remarks>
/// <para>
/// Only the visible rows are ever drawn, and the children of a directory are a contiguous
/// range in the node arrays rather than a collection to materialise, so a directory with
/// 200 thousand entries opens in the time it takes to sort an int array and scrolls
/// without touching the other 199 thousand rows (README sections 5.3, 20).
/// </para>
/// <para>
/// There is no state to speak of: a directory, a selection and a scroll offset. Moving up
/// rebuilds the parent's list instead of keeping a stack of them, which cannot go stale
/// when the sort order or the size mode changes underneath it - a stack of cached lists
/// is exactly where a tree view starts showing yesterday's ordering.
/// </para>
/// </remarks>
internal sealed class TreeScreen : ITuiView
{
    /// <summary>Enough matches to be useful; beyond this the answer is "refine the search".</summary>
    private const int MaxMatches = 2000;

    private int _node = NodeStore.NoNode;
    private int[] _children = [];
    private int _selected;
    private int _scroll;
    private int _rows = 1;

    private SortKey _sort = SortKey.Size;
    private bool _ascending;
    private RowFilter _filter = RowFilter.All;

    private int _builtNode = NodeStore.NoNode;
    private SizeMode _builtMode = SizeMode.Unique;
    private bool _stale = true;

    private string _query = "";
    private bool _typing;
    private int[] _matches = [];
    private int _matchIndex = -1;

    /// <summary>The node under the cursor, or -1.</summary>
    internal int Current => _selected >= 0 && _selected < _children.Length ? _children[_selected] : NodeStore.NoNode;

    internal int Directory => _node;

    /// <summary>Forgets where it was, after the snapshot underneath has been replaced.</summary>
    internal void Reset()
    {
        _node = NodeStore.NoNode;
        _children = [];
        _stale = true;
        _selected = 0;
        _scroll = 0;
        _matches = [];
        _matchIndex = -1;
        _query = "";
        _typing = false;
    }

    /// <summary>Called when the screen comes to the front, so it can pick a starting point.</summary>
    internal void Enter(TuiSession session, string? preferredVolume = null)
    {
        if (session.Snapshot is null) return;

        if (_node == NodeStore.NoNode || _node >= session.Tree.Count)
        {
            _node = Start(session, preferredVolume);
            _stale = true;
            _selected = 0;
            _scroll = 0;
            return;
        }

        if (preferredVolume is not null && Root(session, preferredVolume) is { } root && !Inside(session, root))
        {
            _node = root;
            _stale = true;
            _selected = 0;
            _scroll = 0;
        }
    }

    public void Render(Screen screen, TuiSession session)
    {
        if (session.Snapshot is null)
        {
            screen.Put(2, screen.Row(2).Space(2).Add("No scan is loaded.", Style.Warning));
            Draw.Hints(screen, screen.Height - 1, ("F5", "scan"), ("1", "overview"), ("Q", "quit"));
            return;
        }

        EnsureChildren(session);

        if (_node == NodeStore.NoNode)
        {
            screen.Put(2, screen.Row(2).Space(2).Add("This scan recorded no volumes.", Style.Warning));
            Draw.Hints(screen, screen.Height - 1, ("F5", "scan"), ("1", "overview"), ("Q", "quit"));
            return;
        }

        var tree = session.Tree;
        var total = TreeQuery.Size(tree, _node, session.Mode);
        _rows = Math.Max(1, screen.Height - 7);
        EnsureVisible();

        Header(screen, session);
        Draw.Rule(screen, 1);
        Summary(screen, session, total);
        Draw.Rule(screen, 3);

        var wide = screen.Width >= 100;
        var counts = screen.Width >= 90;
        var barWidth = wide ? 22 : 0;
        var badgeRoom = BadgeRoom(session);
        var used = 3 + 9 + 2 + 6 + 2 + (wide ? barWidth + 2 : 0) + (counts ? 10 : 0) + badgeRoom;
        var nameWidth = Math.Max(12, screen.Width - used);

        for (var row = 0; row < _rows; row++)
        {
            var y = 4 + row;
            var index = _scroll + row;
            if (index >= _children.Length) break;

            screen.Put(y, RowFor(screen, session, _children[index], index, total, barWidth, nameWidth, badgeRoom, counts));
        }

        if (_children.Length == 0)
            screen.Put(4, screen.Row(4).Space(3).Add(
                _filter == RowFilter.All ? "(empty)" : "(nothing matches this filter)", Style.Dim));

        Draw.Rule(screen, screen.Height - 3);
        Footer(screen, session);
    }

    private void Header(Screen screen, TuiSession session)
    {
        var right = string.Format(CultureInfo.InvariantCulture, "{0}  ·  scan {1}  ·  {2:MM-dd HH:mm}",
            session.ModeName, session.ScanId, session.Snapshot!.StartedUtc);

        var line = screen.Row(0).Space().Add("pathmemo", Style.Title).Space(2);
        var path = Sanitizer.Clean(session.Tree.GetPath(_node));
        line.Add(PathDisplay.Shorten(path, Math.Max(10, line.Remaining - right.Length - 2)), Style.Strong);
        line.Right(right, Style.Dim);

        screen.Put(0, line);
    }

    private void Summary(Screen screen, TuiSession session, long total)
    {
        var tree = session.Tree;
        var line = screen.Row(2).Space();

        line.Add("Total ", Style.Dim).Add(SizeFormat.Bytes(total), Style.Strong)
            .Add("  in ", Style.Dim)
            .Add(tree.FileCount[_node].ToString("N0", CultureInfo.InvariantCulture))
            .Add(" files", Style.Dim);

        if (session.Marks.Count > 0)
            line.Add("  ·  ", Style.Dim)
                .Add($"{session.Marks.Count} marked, {SizeFormat.Bytes(session.MarkedBytes())}", Style.Mark);

        if (_filter != RowFilter.All)
            line.Add("  ·  ", Style.Dim).Add(_filter == RowFilter.Directories ? "directories only" : "files only", Style.Accent);

        if (_sort != SortKey.Size || _ascending)
            line.Add("  ·  ", Style.Dim).Add($"by {_sort.ToString().ToLowerInvariant()}{(_ascending ? " asc" : "")}", Style.Accent);

        var parent = tree.Parent[_node];
        line.Right(parent == NodeStore.NoNode
            ? "volume root"
            : "../ " + PathDisplay.Shorten(Sanitizer.Clean(tree.GetPath(parent)), Math.Max(8, line.Remaining - 6)),
            Style.Dim);

        screen.Put(2, line);
    }

    private Line RowFor(Screen screen, TuiSession session, int node, int index,
                        long total, int barWidth, int nameWidth, int badgeRoom, bool counts)
    {
        var tree = session.Tree;
        var size = TreeQuery.Size(tree, node, session.Mode);
        var share = total > 0 ? size * 100.0 / total : 0;
        var isDirectory = tree.IsDirectory(node);
        var marked = session.Marks.Contains(node);

        var line = screen.Row(4 + index - _scroll);
        line.Highlight = index == _selected;

        line.Add(index == _selected ? "▸" : " ", Style.Accent);
        line.Add(marked ? "×" : " ", Style.Mark);
        line.Space();

        line.Add(SizeFormat.Bytes(size).PadLeft(9), size == 0 ? Style.Dim : Style.Plain);

        if (barWidth > 0)
        {
            line.Space(2);
            Draw.Bar(line, share, barWidth);
        }

        line.Space(2).Add(string.Format(CultureInfo.InvariantCulture, "{0,5:F1}%", share), Style.Dim);
        line.Space(2);

        var name = Sanitizer.Clean(tree.Name(node)) + (isDirectory ? Path.DirectorySeparatorChar.ToString() : "");
        line.Add(TextWidth.Fit(name, nameWidth), isDirectory ? Style.Strong : Style.Plain);

        if (badgeRoom > 0)
            line.Add(TextWidth.Fit(Draw.Badges(tree, node), badgeRoom), Style.Dim);

        if (counts && isDirectory)
            line.Right(tree.FileCount[node].ToString("N0", CultureInfo.InvariantCulture), Style.Dim);

        return line;
    }

    private void Footer(Screen screen, TuiSession session)
    {
        var y = screen.Height - 2;

        if (_typing)
        {
            screen.Put(y, screen.Row(y).Space().Add("/", Style.Accent).Add(_query).Add("█", Style.Accent));
        }
        else
        {
            var line = screen.Row(y).Space().Add(session.Message, session.MessageStyle);

            if (_children.Length > 0)
                line.Right($"{_selected + 1} of {_children.Length:N0}", Style.Dim);

            screen.Put(y, line);
        }

        Draw.Hints(screen, screen.Height - 1,
            ("j/k", "move"), ("l", "in"), ("h", "out"), ("y", "copy"), ("e", "explorer"),
            ("x", "mark"), ("/", "search"), ("s", "sort"), ("?", "help"));
    }

    /// <summary>
    /// Width reserved for badges, decided per frame from the rows on screen: a tree with
    /// no links or sparse files should give every column to the names.
    /// </summary>
    private int BadgeRoom(TuiSession session)
    {
        var room = 0;
        for (var row = 0; row < _rows; row++)
        {
            var index = _scroll + row;
            if (index >= _children.Length) break;

            room = Math.Max(room, Draw.Badges(session.Tree, _children[index]).Length);
        }

        return room == 0 ? 0 : Math.Min(room + 2, 18);
    }

    public bool HandleKey(in TuiKey key, TuiSession session)
    {
        if (session.Snapshot is null) return false;
        if (_typing) return Typing(key, session);

        EnsureChildren(session);
        var tree = session.Tree;

        if (key.Is('j') || key.Is(ConsoleKey.DownArrow)) return Move(1);
        if (key.Is('k') || key.Is(ConsoleKey.UpArrow)) return Move(-1);
        if (key.IsCtrl('d') || key.Is(ConsoleKey.PageDown)) return Move(_rows);
        if (key.IsCtrl('u') || key.Is(ConsoleKey.PageUp)) return Move(-_rows);
        if (key.Is('g') || key.Is(ConsoleKey.Home)) return Move(int.MinValue / 2);
        if (key.Is('G') || key.Is(ConsoleKey.End)) return Move(int.MaxValue / 2);

        if (key.Is('l') || key.Is(ConsoleKey.RightArrow) || key.Is(ConsoleKey.Enter)) return Descend(session);
        if (key.Is('h') || key.Is(ConsoleKey.LeftArrow) || key.Is(ConsoleKey.Backspace)) return Ascend(session);

        if (key.Is('~'))
        {
            var root = RootOf(session, _node);
            if (root == _node) { session.Say("already at the volume root"); return true; }

            GoTo(session, root, NodeStore.NoNode);
            return true;
        }

        if (key.Is('m')) { session.CycleMode(); _stale = true; return true; }
        if (key.Is('t')) return CycleFilter(session);

        if (key.Is('s'))
        {
            session.Modal = new SortMenu(_sort, _ascending, (sort, ascending) =>
            {
                _sort = sort;
                _ascending = ascending;
                _stale = true;
            });
            return true;
        }

        if (key.Is('/'))
        {
            _typing = true;
            _query = "";
            return true;
        }

        if (key.Is('n')) return NextMatch(session, 1);
        if (key.Is('N')) return NextMatch(session, -1);

        if (key.Is('i')) return Details(session);

        if (key.Is('x') || key.Is(' ')) return Mark(session);
        if (key.Is('a')) return MarkAll(session);
        if (key.Is('X'))
        {
            session.Marks.Clear();
            session.Say("marks cleared");
            return true;
        }

        if (key.Is('y')) return CopyPath(session);
        if (key.Is('Y')) return CopyDetails(session);
        if (key.Is('e')) return Reveal(session);
        if (key.Is('o')) return Open(session);

        if (key.Is('d'))
        {
            session.Warn(session.Marks.Count > 0
                ? $"deleting the {session.Marks.Count} marked entries arrives with P6 (quarantine, journal, dry-run)"
                : "deletion arrives with P6; until then nothing here can remove a file");
            return true;
        }

        if (key.Is('K'))
        {
            session.Warn("the keep-list arrives with P7, together with the reclaim rules");
            return true;
        }

        if (key.Is('r'))
        {
            session.Say("nothing to recompute here - r belongs to the duplicate and audit screens");
            return true;
        }

        return false;
    }

    private bool Typing(in TuiKey key, TuiSession session)
    {
        if (key.Is(ConsoleKey.Escape))
        {
            _typing = false;
            session.Clear();
            return true;
        }

        if (key.Is(ConsoleKey.Enter))
        {
            _typing = false;
            Search(session);
            return true;
        }

        if (key.Is(ConsoleKey.Backspace))
        {
            if (_query.Length > 0) _query = _query[..^1];
            return true;
        }

        if (key.IsPrintable && _query.Length < 120) _query += key.Char;
        return true;
    }

    private bool Move(int delta)
    {
        if (_children.Length == 0) return true;

        _selected = Math.Clamp(_selected + delta, 0, _children.Length - 1);
        EnsureVisible();
        return true;
    }

    private bool Descend(TuiSession session)
    {
        var node = Current;
        if (node == NodeStore.NoNode) return true;

        if (!session.Tree.IsDirectory(node)) return Details(session);

        if (session.Tree.ChildCount[node] == 0)
        {
            session.Say("that directory is empty");
            return true;
        }

        GoTo(session, node, NodeStore.NoNode);
        session.Clear();
        return true;
    }

    private bool Ascend(TuiSession session)
    {
        var parent = session.Tree.Parent[_node];
        if (parent == NodeStore.NoNode)
        {
            session.Say("already at the volume root");
            return true;
        }

        GoTo(session, parent, _node);
        session.Clear();
        return true;
    }

    /// <summary>Moves to a directory and selects <paramref name="select"/> inside it.</summary>
    private void GoTo(TuiSession session, int directory, int select)
    {
        _node = directory;
        _stale = true;
        _selected = 0;
        _scroll = 0;
        EnsureChildren(session);

        if (select == NodeStore.NoNode) return;

        var at = Array.IndexOf(_children, select);
        if (at < 0) return;

        _selected = at;
        EnsureVisible();
    }

    private bool CycleFilter(TuiSession session)
    {
        var keep = Current;

        _filter = _filter switch
        {
            RowFilter.All => RowFilter.Directories,
            RowFilter.Directories => RowFilter.Files,
            _ => RowFilter.All,
        };

        _stale = true;
        EnsureChildren(session);

        var at = keep == NodeStore.NoNode ? -1 : Array.IndexOf(_children, keep);
        _selected = at >= 0 ? at : 0;
        EnsureVisible();

        session.Say(_filter switch
        {
            RowFilter.Directories => "directories only",
            RowFilter.Files => "files only",
            _ => "directories and files",
        });

        return true;
    }

    private bool Details(TuiSession session)
    {
        var node = Current;
        if (node == NodeStore.NoNode) return true;

        session.Modal = new DetailsDialog(node);
        return true;
    }

    private bool Mark(TuiSession session)
    {
        var node = Current;
        if (node == NodeStore.NoNode) return true;

        if (!session.Marks.Add(node)) session.Marks.Remove(node);
        Move(1);
        return true;
    }

    private bool MarkAll(TuiSession session)
    {
        var added = 0;
        for (var row = 0; row < _rows; row++)
        {
            var index = _scroll + row;
            if (index >= _children.Length) break;
            if (session.Marks.Add(_children[index])) added++;
        }

        session.Say($"{added} marked in view, {session.Marks.Count} in total");
        return true;
    }

    private bool CopyPath(TuiSession session)
    {
        var node = Current;
        if (node == NodeStore.NoNode) return true;

        // The real path, not the sanitised one: what goes on the clipboard has to work
        // when it is pasted into a shell (README section 14.4).
        var path = session.Tree.GetPath(node);

        if (Clipboard.TrySetText(path, out var error)) session.Say($"copied {PathDisplay.Shorten(path, 60)}");
        else session.Warn($"clipboard: {error}");

        return true;
    }

    private bool CopyDetails(TuiSession session)
    {
        var node = Current;
        if (node == NodeStore.NoNode) return true;

        var text = string.Join(Environment.NewLine, DetailsDialog.Describe(session, node));
        if (Clipboard.TrySetText(text, out var error)) session.Say("details copied");
        else session.Warn($"clipboard: {error}");

        return true;
    }

    private bool Reveal(TuiSession session)
    {
        var node = Current;
        if (node == NodeStore.NoNode) return true;

        var path = session.Tree.GetPath(node);
        if (ShellReveal.TryReveal(path, out var error)) session.Say(error ?? "shown in Explorer");
        else session.Warn($"Explorer: {error}");

        return true;
    }

    private bool Open(TuiSession session)
    {
        var node = Current;
        if (node == NodeStore.NoNode) return true;

        var tree = session.Tree;
        var path = tree.GetPath(node);
        var assessment = FileLaunch.Assess(path, tree.IsDirectory(node));

        if (!assessment.Allowed)
        {
            session.Warn(assessment.Reason);
            return true;
        }

        var lines = new List<string>
        {
            Sanitizer.Clean(PathDisplay.Shorten(path, 68)),
            "",
            $"type {assessment.Reason}, {SizeFormat.Bytes(tree.Logical[node])}",
        };

        if (assessment.FromInternet) lines.Add("downloaded from the internet (mark of the web)");
        if (Sanitizer.NeedsCleaning(tree.Name(node)))
            lines.Add("the real name contains hidden characters");

        session.Modal = new ConfirmDialog("Open file", lines, "Open it with its default application?", () =>
        {
            if (FileLaunch.TryOpen(path, out var error)) session.Say("opened");
            else session.Warn($"could not open it: {error}");
        });

        return true;
    }

    private void Search(TuiSession session)
    {
        if (_query.Length == 0)
        {
            session.Clear();
            return;
        }

        var tree = session.Tree;
        var matches = new List<int>(64);

        // Decoded into a stack buffer rather than a string per node: this runs over every
        // node in the snapshot, and 1.6 million short-lived strings would cost more in
        // collections than the search itself (README section 17.3).
        Span<char> buffer = stackalloc char[1024];

        for (var i = 0; i < tree.Count; i++)
        {
            var utf8 = tree.NameUtf8(i);
            if (utf8.Length > buffer.Length) continue;

            var length = Encoding.UTF8.GetChars(utf8, buffer);
            if (!((ReadOnlySpan<char>)buffer[..length]).Contains(_query, StringComparison.OrdinalIgnoreCase)) continue;

            matches.Add(i);
            if (matches.Count >= MaxMatches) break;
        }

        matches.Sort((a, b) => TreeQuery.Size(tree, b, session.Mode).CompareTo(TreeQuery.Size(tree, a, session.Mode)));

        _matches = [.. matches];
        _matchIndex = -1;

        if (_matches.Length == 0)
        {
            session.Warn($"nothing matching \"{_query}\" in scan {session.ScanId}");
            return;
        }

        NextMatch(session, 1);
    }

    private bool NextMatch(TuiSession session, int direction)
    {
        if (_matches.Length == 0)
        {
            session.Say(_query.Length == 0 ? "press / to search" : "no matches");
            return true;
        }

        _matchIndex = ((_matchIndex + direction) % _matches.Length + _matches.Length) % _matches.Length;
        var node = _matches[_matchIndex];

        // A match hidden by the filter would look like a broken jump, so the filter gives
        // way - the user asked for that entry by name.
        if ((_filter == RowFilter.Directories && !session.Tree.IsDirectory(node))
            || (_filter == RowFilter.Files && session.Tree.IsDirectory(node)))
        {
            _filter = RowFilter.All;
            _stale = true;
        }

        var parent = session.Tree.Parent[node];
        GoTo(session, parent == NodeStore.NoNode ? node : parent, node);

        session.Say(string.Format(CultureInfo.InvariantCulture, "match {0} of {1}{2}  ·  {3}",
            _matchIndex + 1, _matches.Length,
            _matches.Length == MaxMatches ? "+" : "",
            SizeFormat.Bytes(TreeQuery.Size(session.Tree, node, session.Mode))));

        return true;
    }

    private void EnsureChildren(TuiSession session)
    {
        if (!_stale && _builtNode == _node && _builtMode == session.Mode) return;

        var tree = session.Tree;
        if (tree.Roots.Length == 0)
        {
            _children = [];
            _node = NodeStore.NoNode;
            _stale = false;
            return;
        }

        if (_node == NodeStore.NoNode || _node >= tree.Count) _node = Start(session, null);

        var range = tree.Children(_node);
        var first = range.Start.Value;
        var end = range.End.Value;

        var kept = new List<int>(Math.Max(0, end - first));
        for (var i = first; i < end; i++)
        {
            if (_filter == RowFilter.Directories && !tree.IsDirectory(i)) continue;
            if (_filter == RowFilter.Files && tree.IsDirectory(i)) continue;
            kept.Add(i);
        }

        var children = kept.ToArray();
        Array.Sort(children, Comparer(session));

        _children = children;
        _builtNode = _node;
        _builtMode = session.Mode;
        _stale = false;

        _selected = Math.Clamp(_selected, 0, Math.Max(0, _children.Length - 1));
        EnsureVisible();
    }

    private Comparison<int> Comparer(TuiSession session)
    {
        var tree = session.Tree;
        var mode = session.Mode;
        var sign = _ascending ? -1 : 1;

        return _sort switch
        {
            SortKey.Name => (a, b) => sign * -string.Compare(tree.Name(a), tree.Name(b), StringComparison.OrdinalIgnoreCase),
            SortKey.Files => (a, b) => sign * tree.FileCount[b].CompareTo(tree.FileCount[a]),
            SortKey.Modified => (a, b) => sign * tree.Mtime[b].CompareTo(tree.Mtime[a]),
            _ => (a, b) => sign * TreeQuery.Size(tree, b, mode).CompareTo(TreeQuery.Size(tree, a, mode)),
        };
    }

    private void EnsureVisible()
    {
        if (_selected < _scroll) _scroll = _selected;
        if (_selected >= _scroll + _rows) _scroll = _selected - _rows + 1;
        _scroll = Math.Clamp(_scroll, 0, Math.Max(0, _children.Length - 1));
    }

    private static int Start(TuiSession session, string? preferredVolume)
    {
        if (preferredVolume is not null && Root(session, preferredVolume) is { } root) return root;

        var tree = session.Tree;
        var best = tree.Roots[0];
        foreach (var candidate in tree.Roots)
            if (tree.Allocated[candidate] > tree.Allocated[best]) best = candidate;

        return best;
    }

    private static int? Root(TuiSession session, string letter)
    {
        var tree = session.Tree;
        foreach (var root in tree.Roots)
            if (tree.Name(root).StartsWith(letter, StringComparison.OrdinalIgnoreCase)) return root;

        return null;
    }

    private static int RootOf(TuiSession session, int node)
    {
        var tree = session.Tree;
        var current = node;
        while (tree.Parent[current] != NodeStore.NoNode) current = tree.Parent[current];
        return current;
    }

    private bool Inside(TuiSession session, int root) => RootOf(session, _node) == root;

    /// <summary>The volume root of the current directory, as a path - what F5 rescans.</summary>
    internal string CurrentRootPath(TuiSession session) =>
        session.Snapshot is null ? AppPaths.DataDirectory : session.Tree.Name(RootOf(session, _node));
}
