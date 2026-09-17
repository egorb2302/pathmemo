using System.Text;

namespace PathMemo.Tui.Terminal;

/// <summary>What a run of text means, which decides its colour.</summary>
/// <remarks>
/// Meanings rather than colours, so that <c>NO_COLOR</c> and a monochrome terminal are one
/// switch in <see cref="Screen"/> instead of a decision at every call site.
/// </remarks>
internal enum Style
{
    Plain,
    Dim,
    Strong,
    Title,
    Accent,
    Bar,
    BarEmpty,
    Good,
    Warning,
    Alert,
    Mark,
}

/// <summary>
/// One row under construction. Clips to the screen width as it is filled, so no caller
/// can push the frame out of alignment.
/// </summary>
internal sealed class Line
{
    private readonly StringBuilder _ansi = new(256);
    private readonly StringBuilder _plain = new(128);
    private readonly bool _color;
    private int _columns;
    private int _limit;

    internal Line(bool color) => _color = color;

    /// <summary>Draws the whole row inverted - how the cursor row is shown.</summary>
    internal bool Highlight { get; set; }

    internal int Columns => _columns;

    internal int Remaining => Math.Max(0, _limit - _columns);

    internal Line Reset(int limit)
    {
        _ansi.Clear();
        _plain.Clear();
        _columns = 0;
        _limit = limit;
        Highlight = false;
        return this;
    }

    internal Line Add(string text, Style style = Style.Plain)
    {
        if (text.Length == 0 || Remaining == 0) return this;

        var fitted = TextWidth.Of(text) <= Remaining ? text : TextWidth.Truncate(text, Remaining);
        if (fitted.Length == 0) return this;

        Emit(fitted, style);
        return this;
    }

    internal Line Add(char c, int count, Style style = Style.Plain)
    {
        count = Math.Min(count, Remaining);
        if (count <= 0) return this;

        Emit(new string(c, count), style);
        return this;
    }

    internal Line Space(int count = 1) => Add(' ', count);

    /// <summary>Pads with spaces until the next run starts at <paramref name="column"/>.</summary>
    internal Line PadTo(int column) => column > _columns ? Space(column - _columns) : this;

    /// <summary>Places text so that it ends at the right edge.</summary>
    internal Line Right(string text, Style style = Style.Plain)
    {
        var width = TextWidth.Of(text);
        if (width > Remaining) return Add(text, style);

        return Space(Remaining - width).Add(text, style);
    }

    /// <summary>The row as plain text, for tests and for the clipboard.</summary>
    internal string PlainText => _plain.ToString();

    internal string Build()
    {
        if (!Highlight) return _ansi.ToString();

        // The inverted row has to cover the full width, or the cursor line ends in a
        // ragged block that moves as names change length.
        var pad = Remaining;
        return "[7m" + _ansi + new string(' ', pad) + "[0m";
    }

    private void Emit(string text, Style style)
    {
        _plain.Append(text);
        _columns += TextWidth.Of(text);

        if (!_color || style == Style.Plain)
        {
            _ansi.Append(text);
            return;
        }

        _ansi.Append(Code(style)).Append(text).Append("[0m");

        // A highlighted row resets to normal video at every run boundary, so it has to be
        // turned back on for the rest of the row.
        if (Highlight) _ansi.Append("[7m");
    }

    private static string Code(Style style) => style switch
    {
        Style.Dim => "[90m",
        Style.Strong => "[1m",
        Style.Title => "[1;36m",
        Style.Accent => "[36m",
        Style.Bar => "[36m",
        Style.BarEmpty => "[90m",
        Style.Good => "[32m",
        Style.Warning => "[33m",
        Style.Alert => "[31m",
        Style.Mark => "[1;33m",
        _ => "",
    };
}

/// <summary>
/// The frame buffer: rows are composed here and only the rows that changed are written
/// to the terminal (README section 18.1).
/// </summary>
/// <remarks>
/// <para>
/// Redrawing everything on every keystroke is what makes a terminal UI flicker and what
/// makes it unusable over SSH. Comparing against the previous frame and emitting only the
/// rows that differ turns "move the cursor down one row" into two short writes, which is
/// how the 16 ms frame budget is met without any cleverness elsewhere.
/// </para>
/// <para>
/// Row granularity, not cell granularity: a cell-diffing renderer needs an attribute
/// buffer and run coalescing for a gain that no one can see at 24 rows.
/// </para>
/// </remarks>
internal sealed class Screen(TextWriter output, bool color)
{
    private readonly StringBuilder _frame = new(8192);
    private string[] _front = [];
    private string[] _back = [];
    private string[] _plain = [];
    private Line[] _lines = [];

    internal int Width { get; private set; }

    internal int Height { get; private set; }

    internal bool Color { get; } = color;

    internal void Resize(int width, int height)
    {
        if (width == Width && height == Height) return;

        Width = Math.Max(1, width);
        Height = Math.Max(1, height);

        _front = new string[Height];
        _back = new string[Height];
        _plain = new string[Height];
        _lines = new Line[Height];

        for (var y = 0; y < Height; y++)
        {
            _front[y] = "￿";        // impossible content, so the first flush writes every row
            _back[y] = "";
            _plain[y] = "";
            _lines[y] = new Line(Color);
        }
    }

    /// <summary>Starts a frame. Rows not written this frame come out blank.</summary>
    internal void Begin()
    {
        for (var y = 0; y < Height; y++)
        {
            _back[y] = "";
            _plain[y] = "";
        }
    }

    /// <summary>A row to fill. Reused between frames; valid until the next call for that row.</summary>
    internal Line Row(int y) => _lines[y].Reset(Width);

    internal void Put(int y, Line line)
    {
        if (y < 0 || y >= Height) return;
        _back[y] = line.Build();
        _plain[y] = line.PlainText;
    }

    /// <summary>Forces the next flush to rewrite every row, after a resize or a suspend.</summary>
    internal void Invalidate()
    {
        for (var y = 0; y < Height; y++) _front[y] = "￿";
    }

    internal void Flush()
    {
        _frame.Clear();

        for (var y = 0; y < Height; y++)
        {
            if (_back[y] == _front[y]) continue;

            // Erase first, then write. The other order loses the last character of any
            // row that fills the width: writing the final column leaves the cursor there
            // in the pending-wrap state, and an erase-to-end-of-line then wipes the cell
            // that was just written. Measured in conhost; Windows Terminal does the same.
            _frame.Append("[").Append(y + 1).Append(";1H")
                  .Append("[K")
                  .Append(_back[y]);

            _front[y] = _back[y];
        }

        if (_frame.Length == 0) return;

        // Park the cursor out of the way; it is hidden, but a terminal that ignores the
        // hide request should not leave it blinking in the middle of a file name.
        _frame.Append("[").Append(Height).Append(";1H");

        output.Write(_frame.ToString());
        output.Flush();
    }

    /// <summary>Plain text of a row as last composed. Tests read frames through this.</summary>
    internal string TextAt(int y) => y >= 0 && y < Height ? _plain[y] : "";
}
