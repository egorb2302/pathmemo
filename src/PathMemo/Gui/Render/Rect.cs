namespace PathMemo.Gui.Render;

/// <summary>
/// A rectangle in device pixels, with the cutting operations a layout actually performs.
/// </summary>
/// <remarks>
/// <para>
/// Left/Top/Width/Height rather than Left/Top/Right/Bottom, because every question asked of
/// it here is about size ("how many rows fit", "take 40 pixels off the top") and the
/// right/bottom form turns each of those into an arithmetic chance to be off by one.
/// </para>
/// <para>
/// A value type: a frame allocates hundreds of these and none of them outlive it. The
/// <c>Take…</c> methods return a new rectangle rather than mutating, so a view can hand the
/// same area to two children without either seeing the other's cut - the one bug a mutable
/// layout cursor guarantees.
/// </para>
/// </remarks>
internal readonly record struct Rect(int X, int Y, int Width, int Height)
{
    internal static readonly Rect Empty = new(0, 0, 0, 0);

    internal int Right => X + Width;

    internal int Bottom => Y + Height;

    internal bool IsEmpty => Width <= 0 || Height <= 0;

    internal bool Contains(int px, int py) => px >= X && px < Right && py >= Y && py < Bottom;

    /// <summary>The area shrunk by the same margin on all four sides.</summary>
    internal Rect Deflate(int by) => Deflate(by, by);

    internal Rect Deflate(int horizontal, int vertical) =>
        new(X + horizontal, Y + vertical, Width - horizontal * 2, Height - vertical * 2);

    internal Rect Inset(int left, int top, int right, int bottom) =>
        new(X + left, Y + top, Width - left - right, Height - top - bottom);

    /// <summary>The top <paramref name="height"/> pixels.</summary>
    internal Rect TakeTop(int height) => new(X, Y, Width, Math.Min(height, Height));

    /// <summary>What is left after the top <paramref name="height"/> pixels.</summary>
    internal Rect DropTop(int height) => new(X, Y + height, Width, Math.Max(0, Height - height));

    internal Rect TakeBottom(int height) =>
        new(X, Bottom - Math.Min(height, Height), Width, Math.Min(height, Height));

    internal Rect DropBottom(int height) => new(X, Y, Width, Math.Max(0, Height - height));

    internal Rect TakeLeft(int width) => new(X, Y, Math.Min(width, Width), Height);

    internal Rect DropLeft(int width) => new(X + width, Y, Math.Max(0, Width - width), Height);

    internal Rect TakeRight(int width) =>
        new(Right - Math.Min(width, Width), Y, Math.Min(width, Width), Height);

    internal Rect DropRight(int width) => new(X, Y, Math.Max(0, Width - width), Height);

    internal Rect WithHeight(int height) => new(X, Y, Width, height);

    internal Rect WithWidth(int width) => new(X, Y, width, Height);

    internal Rect AtY(int y) => new(X, y, Width, Height);

    internal Rect Offset(int dx, int dy) => new(X + dx, Y + dy, Width, Height);

    /// <summary>The part common to both, or an empty rectangle when they do not meet.</summary>
    internal Rect Intersect(Rect other)
    {
        var x = Math.Max(X, other.X);
        var y = Math.Max(Y, other.Y);
        var right = Math.Min(Right, other.Right);
        var bottom = Math.Min(Bottom, other.Bottom);

        return right <= x || bottom <= y ? Empty : new Rect(x, y, right - x, bottom - y);
    }
}
