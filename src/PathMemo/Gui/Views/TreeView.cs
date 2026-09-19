using System.Globalization;
using PathMemo.Analysis;
using PathMemo.Cli.Output;
using PathMemo.Gui.Controls;
using PathMemo.Gui.Render;
using PathMemo.Platform.Native;
using PathMemo.Snapshots;

namespace PathMemo.Gui.Views;

/// <summary>
/// The main screen: a directory, its children by size, and the way down (README section 14.2).
/// </summary>
/// <remarks>
/// <para>
/// The same shape as the TUI's tree screen, for the same reasons, and with the same state:
/// the directory, the cursor and the scroll offset, and nothing else. There is no stack of
/// visited levels - going up rebuilds the parent's list and puts the cursor on the node we
/// came out of. A per-level cache would have to be invalidated on every change of sort, size
/// mode and filter, which is precisely where a tree starts showing yesterday's order
/// (README section 14.7).
/// </para>
/// <para>
/// Bars and percentages are relative to the current directory, not to the volume: inside a
/// folder of similar-sized things, a bar against the whole disk is a row of empty tracks that
/// distinguishes nothing.
/// </para>
/// </remarks>
internal sealed class TreeView
{
    private readonly RowList _rows = new();

    private SnapshotContents? _snapshot;
    private long _scanId;
    private int _node = NodeStore.NoNode;
    private int[] _children = [];
    private long _total;

    /// <summary>The node the cursor was on in the directory we came out of.</summary>
    private int _cameFrom = NodeStore.NoNode;

    /// <summary>The blocks the last frame laid out, so a click can be resolved against them.</summary>
    private List<Treemap.Block> _blocks = [];

    /// <summary>Whether the map panel is shown. The table alone is a complete answer.</summary>
    internal bool ShowMap { get; set; } = true;

    internal SizeMode Mode { get; set; } = SizeMode.Unique;

    internal bool HasSnapshot => _snapshot is not null;

    internal NodeStore Tree => _snapshot?.Tree ?? NodeStore.Empty;

    internal SnapshotContents? Snapshot => _snapshot;

    /// <summary>The node under the cursor, or <see cref="NodeStore.NoNode"/>.</summary>
    internal int Current => _children.Length == 0 ? NodeStore.NoNode : _children[_rows.Selected];

    internal void Load(SnapshotContents snapshot, long scanId, string? preferredRoot)
    {
        _snapshot = snapshot;
        _scanId = scanId;

        var roots = snapshot.Tree.Roots;
        if (roots.Length == 0) return;

        var start = roots[0];

        if (preferredRoot is { Length: > 0 } letter)
        {
            foreach (var root in roots)
            {
                if (!snapshot.Tree.Name(root).StartsWith(letter, StringComparison.OrdinalIgnoreCase)) continue;

                start = root;
                break;
            }
        }

        Go(start);
    }

    /// <summary>Rebuilds the listing for a directory and puts the cursor where it belongs.</summary>
    private void Go(int node)
    {
        if (_snapshot is null) return;

        _node = node;
        _children = TreeQuery.ChildrenBySize(_snapshot.Tree, node, Mode);

        _total = 0;
        foreach (var child in _children) _total += TreeQuery.Size(_snapshot.Tree, child, Mode);

        _rows.Count = _children.Length;
        _rows.Reset();

        // Coming up out of a directory, the cursor goes back onto it rather than to the top:
        // the row the user was just looking at is the one they are looking for.
        if (_cameFrom != NodeStore.NoNode)
        {
            var index = Array.IndexOf(_children, _cameFrom);
            if (index >= 0) _rows.Select(index);

            _cameFrom = NodeStore.NoNode;
        }
    }

    internal void Rebuild() => Go(_node);

    /// <summary>
    /// Drops the loaded snapshot, so the next visit reads the newest one.
    /// </summary>
    /// <remarks>
    /// Called after a scan. Keeping the old tree and its node indices would be worse than
    /// stale: the marks and the cursor are indices into an array that a new snapshot replaces
    /// with a different one, and an index pointing into a different tree is the worst kind of
    /// bug for a list something might be deleted from (README section 14.7).
    /// </remarks>
    internal void Forget()
    {
        _snapshot = null;
        _scanId = 0;
        _node = NodeStore.NoNode;
        _children = [];
        _total = 0;
        _cameFrom = NodeStore.NoNode;
        _rows.Count = 0;
        _rows.Reset();
    }

    internal void Paint(IPainter p, Theme theme, HitMap hits, Rect area, Hit hover)
    {
        if (_snapshot is null)
        {
            var middle = new Rect(area.X, area.Y + area.Height / 2 - p.LineHeight, area.Width, p.LineHeight);
            p.Text(middle, "No snapshot to browse. Run a scan first.", theme.Dim, FontRole.Body, Align.Centre);
            return;
        }

        var pad = p.Scale(16);
        var body = area.Deflate(pad, p.Scale(10));

        var crumbs = body.TakeTop(p.LineHeight + p.Scale(8));
        Breadcrumb(p, theme, hits, crumbs, hover);

        var head = body.DropTop(crumbs.Height + p.Scale(4)).TakeTop(p.Height(FontRole.Small) + p.Scale(6));
        Header(p, theme, head);

        var below = body.DropTop(crumbs.Height + p.Scale(4) + head.Height);

        if (_children.Length == 0)
        {
            p.Text(below.TakeTop(p.LineHeight * 2), "This directory is empty in the snapshot.",
                theme.Dim, FontRole.Body, Align.Centre);
            return;
        }

        // The map is a panel beside the table, not instead of it, and it is the first thing to
        // go when the window is narrow: the table answers every question the map does, and the
        // map at 200 pixels wide answers none.
        var map = Rect.Empty;

        if (ShowMap && body.Width > p.Scale(940))
        {
            map = below.TakeRight((int)(below.Width * 0.38));
            below = below.DropRight(map.Width + p.Scale(16));

            // The header line has to be re-cut over the table alone, or its columns describe a
            // width the rows no longer have.
            Header(p, theme, head.WithWidth(below.Width));
        }

        _rows.Count = _children.Length;
        _rows.Paint(p, theme, hits, below, (painter, line, index, selected, _) =>
            Row(painter, theme, line, index, selected), hover);

        if (!map.IsEmpty) Map(p, theme, hits, map, hover);
    }

    /// <summary>
    /// The treemap panel: the same children, as area instead of as rows.
    /// </summary>
    /// <remarks>
    /// It shows what a sorted table cannot - that one child is most of the directory, or that
    /// no child is. The table answers "how big is this one"; the map answers "where did it all
    /// go" without reading a single number, which is the question people open a disk tool with.
    /// </remarks>
    private void Map(IPainter p, Theme theme, HitMap hits, Rect area, Hit hover)
    {
        var tree = _snapshot!.Tree;

        var head = area.TakeTop(p.Height(FontRole.Small) + p.Scale(6));
        p.Text(head, "map", theme.Dim, FontRole.Small);
        p.Fill(head.TakeBottom(1), theme.Border);

        var canvas = area.DropTop(head.Height + p.Scale(6));
        p.Fill(canvas, theme.Background.Mix(theme.Text, 0.04));

        var sizes = new long[_children.Length];
        for (var i = 0; i < _children.Length; i++) sizes[i] = TreeQuery.Size(tree, _children[i], Mode);

        _blocks = Treemap.Layout(sizes, canvas.Deflate(p.Scale(4)));

        for (var i = 0; i < _blocks.Count; i++)
        {
            var block = _blocks[i];
            var node = _children[block.Index];
            var directory = tree.IsDirectory(node);

            var fill = directory
                ? theme.Bar
                : theme.Category(FileCategories.ByExtension(Path.GetExtension(tree.Name(node).AsSpan())));

            var selected = block.Index == _rows.Selected;
            var hovered = hover == new Hit(HitKind.Block, i);

            if (selected) fill = fill.Mix(theme.Text, 0.35);
            else if (hovered) fill = fill.Mix(theme.Text, 0.20);

            // A one-pixel gap rather than a border: at this size an outline is a third of the
            // block, and the gap already separates them.
            p.Fill(block.Area.DropRight(1).DropBottom(1), fill);

            // Only a block that can actually hold a word gets one.
            if (block.Area.Width > p.Scale(54) && block.Area.Height > p.Height(FontRole.Small) + p.Scale(4))
            {
                p.Text(block.Area.Deflate(p.Scale(4), p.Scale(2)), tree.Name(node),
                    theme.Dark ? theme.Text : Colour.Rgb(0xFFFFFF), FontRole.Small);
            }

            hits.Add(block.Area, HitKind.Block, i);
        }
    }

    /// <summary>
    /// The path, each segment clickable. This is how a GUI goes up: <c>h</c> in a terminal is
    /// a key nobody discovers, a path with clickable parts is the same operation in plain sight.
    /// </summary>
    private void Breadcrumb(IPainter p, Theme theme, HitMap hits, Rect area, Hit hover)
    {
        var tree = _snapshot!.Tree;

        // Root first, so the segments read left to right the way the path does.
        var chain = new List<int>(8);
        for (var node = _node; node != NodeStore.NoNode; node = tree.Parent[node]) chain.Add(node);
        chain.Reverse();

        var left = area;
        var gap = p.Scale(6);

        for (var i = 0; i < chain.Count; i++)
        {
            var last = i == chain.Count - 1;
            var name = tree.Name(chain[i]);
            var width = p.Measure(name, last ? FontRole.Bold : FontRole.Body) + p.Scale(8);

            if (width > left.Width) break;

            var segment = left.TakeLeft(width);
            var hovered = hover == new Hit(HitKind.Crumb, i);

            if (hovered && !last) p.Fill(segment, theme.Hover);

            p.Text(segment, name, last ? theme.Text : theme.Accent,
                last ? FontRole.Bold : FontRole.Body, Align.Centre);

            if (!last) hits.Add(segment, HitKind.Crumb, i);

            left = left.DropLeft(width);

            if (last || left.Width < p.Scale(20)) continue;

            var separator = left.TakeLeft(gap + p.Scale(4));
            p.Text(separator, "›", theme.Dim, FontRole.Body, Align.Centre);
            left = left.DropLeft(separator.Width);
        }

        // What the listing totals, which is not the same as the directory's own size when a
        // child could not be read.
        p.Text(area, $"{SizeFormat.Bytes(_total)}  ·  {_children.Length:N0} items  ·  {ModeName}",
            theme.Dim, FontRole.Small, Align.Right);
    }

    private void Header(IPainter p, Theme theme, Rect area)
    {
        var columns = Columns(p, area);

        p.Text(columns.Size, "size", theme.Dim, FontRole.Small, Align.Right);
        p.Text(columns.Share, "share", theme.Dim, FontRole.Small, Align.Right);
        p.Text(columns.Name, "name", theme.Dim, FontRole.Small);
        p.Text(columns.Count, "files", theme.Dim, FontRole.Small, Align.Right);

        p.Fill(area.TakeBottom(1), theme.Border);
    }

    /// <summary>
    /// Where the columns sit. One function, used by the header and by every row, because two
    /// copies of this arithmetic is a header that lines up with nothing.
    /// </summary>
    private (Rect Size, Rect Bar, Rect Share, Rect Name, Rect Count) Columns(IPainter p, Rect line)
    {
        var size = line.TakeLeft(p.Scale(74));
        var bar = line.DropLeft(size.Width + p.Scale(10)).TakeLeft(p.Scale(120));
        var share = line.DropLeft(size.Width + bar.Width + p.Scale(18)).TakeLeft(p.Scale(48));

        var used = size.Width + bar.Width + share.Width + p.Scale(28);

        // The file count is dropped before the name is: a name that cannot be read makes the
        // row useless, a missing count makes it merely shorter.
        var counts = line.Width - used > p.Scale(320);
        var count = counts ? line.TakeRight(p.Scale(70)) : Rect.Empty;

        var name = line.DropLeft(used).DropRight(counts ? count.Width + p.Scale(10) : 0);

        return (size, bar, share, name, count);
    }

    private void Row(IPainter p, Theme theme, Rect line, int index, bool selected)
    {
        var tree = _snapshot!.Tree;
        var node = _children[index];
        var size = TreeQuery.Size(tree, node, Mode);
        var share = _total > 0 ? size / (double)_total : 0;
        var directory = tree.IsDirectory(node);

        var inner = line.Deflate(p.Scale(4), 0);
        var columns = Columns(p, inner);

        p.Text(columns.Size, SizeFormat.Bytes(size), size == 0 ? theme.Dim : theme.Text,
            FontRole.Body, Align.Right);

        // Colour says what the bytes are, which a size column cannot: 40 GB of media is a
        // different decision from 40 GB of cache (README section 6).
        var name = tree.Name(node);
        var category = directory
            ? theme.Bar
            : theme.Category(FileCategories.ByExtension(Path.GetExtension(name.AsSpan())));

        Draw.Bar(p, theme, columns.Bar.Deflate(0, (columns.Bar.Height - p.Scale(8)) / 2), share, category);

        p.Text(columns.Share, (share * 100).ToString("0.0", CultureInfo.InvariantCulture) + "%",
            theme.Dim, FontRole.Body, Align.Right);

        // A trailing separator is how a directory is told from a file at a glance, and it
        // survives a palette, a screenshot and a colour-blind reader.
        var label = directory ? name + Path.DirectorySeparatorChar : name;
        var badges = Badges(tree, node);
        var room = columns.Name;

        if (badges.Length > 0 && room.Width > p.Scale(160))
        {
            var width = Draw.Badge(p, theme, line, room.Right, badges, BadgeTint(theme, tree, node));
            room = room.DropRight(width + p.Scale(8));
        }

        p.Text(room, label, directory ? theme.Text : theme.Text.Mix(theme.Dim, 0.25),
            directory ? FontRole.Bold : FontRole.Body);

        if (!columns.Count.IsEmpty && directory)
            p.Text(columns.Count, tree.FileCount[node].ToString("N0", CultureInfo.InvariantCulture),
                theme.Dim, FontRole.Body, Align.Right);

        if (selected) p.Fill(line.TakeLeft(p.Scale(3)), theme.Accent);
    }

    private static string Badges(NodeStore tree, int node)
    {
        var flags = tree.Flags[node];
        if (flags is NodeFlags.None or NodeFlags.Directory) return "";

        if ((flags & NodeFlags.Reparse) != 0) return "reparse";
        if ((flags & NodeFlags.HardlinkAlias) != 0) return "link";
        if ((flags & NodeFlags.CloudOnly) != 0) return "cloud";
        if ((flags & NodeFlags.Sparse) != 0) return "sparse";
        if ((flags & NodeFlags.SelfData) != 0) return "self";
        if ((flags & NodeFlags.Incomplete) != 0) return "partial";

        return "";
    }

    private static Colour BadgeTint(Theme theme, NodeStore tree, int node)
    {
        var flags = tree.Flags[node];

        // "self" is the one badge that is a refusal rather than a description: this is our own
        // data and the guard will not delete it (README section 9.2).
        if ((flags & NodeFlags.SelfData) != 0) return theme.Good;
        if ((flags & NodeFlags.Incomplete) != 0) return theme.Warning;

        return theme.Dim;
    }

    internal string ModeName => Mode switch
    {
        SizeMode.Unique => "unique",
        SizeMode.Allocated => "allocated",
        _ => "logical",
    };

    internal void CycleMode()
    {
        Mode = Mode switch
        {
            SizeMode.Unique => SizeMode.Allocated,
            SizeMode.Allocated => SizeMode.Logical,
            _ => SizeMode.Unique,
        };

        // The order depends on the mode, so the listing is not merely relabelled.
        Rebuild();
    }

    /// <summary>Descends into the row under the cursor, if it is a directory with children.</summary>
    internal bool Enter()
    {
        var node = Current;
        if (node == NodeStore.NoNode || _snapshot is null) return false;
        if (!_snapshot.Tree.IsDirectory(node) || _snapshot.Tree.ChildCount[node] == 0) return false;

        Go(node);
        return true;
    }

    internal bool Up()
    {
        if (_snapshot is null || _node == NodeStore.NoNode) return false;

        var parent = _snapshot.Tree.Parent[_node];
        if (parent == NodeStore.NoNode) return false;

        _cameFrom = _node;
        Go(parent);

        return true;
    }

    /// <summary>Jumps to the <paramref name="index"/>-th segment of the current path.</summary>
    internal bool Crumb(int index)
    {
        if (_snapshot is null) return false;

        var chain = new List<int>(8);
        for (var node = _node; node != NodeStore.NoNode; node = _snapshot.Tree.Parent[node]) chain.Add(node);
        chain.Reverse();

        if (index < 0 || index >= chain.Count || chain[index] == _node) return false;

        _cameFrom = _node;
        Go(chain[index]);

        return true;
    }

    internal bool Click(int row, bool doubleClick)
    {
        if (row < 0 || row >= _children.Length) return false;

        _rows.Select(row);
        return !doubleClick || Enter();
    }

    /// <summary>A click on the map, which is a click on the row the block stands for.</summary>
    internal bool ClickBlock(int block, bool doubleClick)
    {
        if (block < 0 || block >= _blocks.Count) return false;

        return Click(_blocks[block].Index, doubleClick);
    }

    /// <summary>The row a hovered block belongs to, for highlighting it in the table.</summary>
    internal int RowOfBlock(int block) =>
        block >= 0 && block < _blocks.Count ? _blocks[block].Index : -1;

    internal bool Wheel(int notches) => _rows.Wheel(notches);

    internal bool Key(KeyInput key)
    {
        if (_rows.Key(key)) return true;

        switch (key.VirtualKey)
        {
            case User32.VkReturn:
            case User32.VkRight:
                return Enter();

            case User32.VkLeft:
            case User32.VkBack:
                return Up();
        }

        return false;
    }

    /// <summary>The path under the cursor, in full, for the clipboard and Explorer.</summary>
    internal string? SelectedPath =>
        Current == NodeStore.NoNode ? null : _snapshot!.Tree.GetPath(Current);

    internal long ScanId => _scanId;
}
