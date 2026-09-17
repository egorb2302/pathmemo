namespace PathMemo.Tui.Terminal;

/// <summary>One keystroke, in the form the screens match on.</summary>
internal readonly record struct TuiKey(ConsoleKey Key, char Char, bool Ctrl, bool Shift, bool Alt)
{
    internal static readonly TuiKey None = new(0, '\0', false, false, false);

    /// <summary>A plain character key, with no modifier but Shift.</summary>
    internal bool Is(char c) => !Ctrl && !Alt && Char == c;

    internal bool Is(ConsoleKey key) => !Ctrl && !Alt && Key == key;

    internal bool IsCtrl(char c) => Ctrl && !Alt && char.ToLowerInvariant(Char) == c;

    /// <summary>Whether this is a character worth putting into a search box.</summary>
    internal bool IsPrintable => !Ctrl && !Alt && Char >= ' ' && Char != '';
}

/// <summary>
/// Reads keys without blocking the frame loop.
/// </summary>
/// <remarks>
/// <para>
/// <c>Console.ReadKey(intercept: true)</c> rather than <c>ReadConsoleInputW</c>: the BCL
/// already decodes VT sequences from Windows Terminal and virtual-key records from
/// conhost into the same <c>ConsoleKeyInfo</c>, and reimplementing that decoding is a
/// week of edge cases (README section 18.1 allows either).
/// </para>
/// <para>
/// It blocks, though, and the loop also has to notice a window resize, so it is polled:
/// <c>KeyAvailable</c> in short slices up to the caller's timeout. The cost is one syscall
/// every few milliseconds while the user is reading the screen, which is invisible next to
/// the terminal's own idle cost.
/// </para>
/// <para>
/// <c>TreatControlCAsInput</c> is deliberately left alone, so Ctrl+C stays the standard
/// cancel and reaches the process through <c>CancelKeyPress</c> (README section 14.3).
/// Stealing it is exactly the v2 mistake this design exists to avoid.
/// </para>
/// </remarks>
internal static class KeyReader
{
    private const int SliceMs = 12;

    internal static bool TryRead(int timeoutMs, out TuiKey key)
    {
        key = TuiKey.None;
        var waited = 0;

        while (true)
        {
            if (Available())
            {
                key = Translate(Console.ReadKey(intercept: true));
                return true;
            }

            if (waited >= timeoutMs) return false;

            var slice = Math.Min(SliceMs, timeoutMs - waited);
            Thread.Sleep(slice);
            waited += slice;
        }
    }

    /// <summary>Drains anything typed while a slow operation was running.</summary>
    internal static void Drain()
    {
        while (Available()) Console.ReadKey(intercept: true);
    }

    private static bool Available()
    {
        try { return Console.KeyAvailable; }
        catch (InvalidOperationException) { return false; }   // input redirected mid-run
        catch (IOException) { return false; }
    }

    private static TuiKey Translate(ConsoleKeyInfo info) => new(
        info.Key,
        info.KeyChar,
        (info.Modifiers & ConsoleModifiers.Control) != 0,
        (info.Modifiers & ConsoleModifiers.Shift) != 0,
        (info.Modifiers & ConsoleModifiers.Alt) != 0);
}
