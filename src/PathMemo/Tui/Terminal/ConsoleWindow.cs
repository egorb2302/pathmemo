using PathMemo.Config;
using PathMemo.Platform.Native;

namespace PathMemo.Tui.Terminal;

/// <summary>
/// The console window itself: its title, its size, its input mode and its font.
/// </summary>
/// <remarks>
/// <para>
/// Everything here exists for the double-click case (README section 2.1). A window that
/// Explorer created for us has nobody to inherit sensible settings from: the title is the
/// full path of the exe, quick-edit is on, and the size is whatever the user's console
/// defaults say - which may be the 80x25 of a profile created in 2009.
/// </para>
/// <para>
/// Quick-edit is the one that actually breaks things. With it on, a click anywhere in the
/// window starts a text selection and the next write to the console <b>blocks until the
/// selection is cleared</b>: the screen freezes and the user has no idea why. The mode is
/// turned off while the screens are up and restored on the way out - a shell that launched
/// us keeps the setting it had.
/// </para>
/// </remarks>
internal static class ConsoleWindow
{
    /// <summary>Roomy enough for the bar and the file counts, small enough to fit anywhere.</summary>
    private const int PreferredWidth = 110;
    private const int PreferredHeight = 32;

    private static uint _originalInputMode;
    private static bool _inputModeChanged;
    private static string? _originalTitle;

    /// <summary>
    /// Makes the window fit to be drawn in. Every step is optional: a host that refuses
    /// any of it still gets a working screen, just a less comfortable one.
    /// </summary>
    internal static void Prepare(bool ownsWindow)
    {
        Glyphs.Ascii = !SupportsBoxDrawing();

        SetTitle();
        DisableQuickEdit();

        // Only when the window is ours to shape. Resizing a window the user's shell lives
        // in - and with it their scrollback buffer - would be rude and surprising.
        if (ownsWindow) Enlarge();
    }

    internal static void Restore()
    {
        if (_inputModeChanged)
        {
            _inputModeChanged = false;
            Apply(Kernel32Extra.StdInputHandle, _originalInputMode);
        }

        if (_originalTitle is not null)
        {
            try { Console.Title = _originalTitle; }
            catch (Exception ex) when (ex is IOException or PlatformNotSupportedException) { /* nothing to restore */ }

            _originalTitle = null;
        }
    }

    private static void SetTitle()
    {
        try
        {
            // Reading the title fails on some hosts; that only costs the restore.
            try { _originalTitle = Console.Title; } catch (Exception ex) when (ex is IOException or PlatformNotSupportedException) { }

            Console.Title = AppPaths.IsRedirected
                ? $"pathmemo {AppInfo.Version} - {AppPaths.DataDirectory}"
                : $"pathmemo {AppInfo.Version}";
        }
        catch (Exception ex) when (ex is IOException or PlatformNotSupportedException)
        {
            // A host without a title bar. Nothing else depends on this.
        }
    }

    private static void DisableQuickEdit()
    {
        try
        {
            var handle = Kernel32Extra.GetStdHandle(Kernel32Extra.StdInputHandle);
            if (handle == 0 || handle == -1) return;
            if (!Kernel32Extra.GetConsoleMode(handle, out var mode)) return;

            _originalInputMode = mode;

            // The extended-flags bit has to be set in the same call, or the quick-edit bit
            // is ignored outright.
            var wanted = (mode | Kernel32Extra.EnableExtendedFlags) & ~Kernel32Extra.EnableQuickEditMode;
            if (wanted == mode) return;

            if (Kernel32Extra.SetConsoleMode(handle, wanted)) _inputModeChanged = true;
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            // Not a console host we know. The screens still draw.
        }
    }

    private static void Apply(int stdHandle, uint mode)
    {
        try
        {
            var handle = Kernel32Extra.GetStdHandle(stdHandle);
            if (handle != 0 && handle != -1) Kernel32Extra.SetConsoleMode(handle, mode);
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            // Nothing to restore on a host without these entry points.
        }
    }

    /// <summary>
    /// Grows a too-small window towards something the tree is comfortable in. Never
    /// shrinks: a user who made their window large meant it.
    /// </summary>
    private static void Enlarge()
    {
        try
        {
            var width = Math.Min(PreferredWidth, Console.LargestWindowWidth);
            var height = Math.Min(PreferredHeight, Console.LargestWindowHeight);

            if (width <= Console.WindowWidth && height <= Console.WindowHeight) return;

            width = Math.Max(width, Console.WindowWidth);
            height = Math.Max(height, Console.WindowHeight);

            // The buffer has to be at least as large as the window, and it has to be grown
            // first - the other order is rejected.
            Console.SetBufferSize(Math.Max(width, Console.BufferWidth), Math.Max(height, Console.BufferHeight));
            Console.SetWindowSize(width, height);
        }
        catch (Exception ex) when (ex is IOException or ArgumentOutOfRangeException or PlatformNotSupportedException)
        {
            // Windows Terminal ignores buffer resizes, and a maximised window refuses them.
            // Either way the screens adapt to whatever size they get (README section 14.5).
        }
    }

    /// <summary>
    /// Whether the console font can draw the box-drawing and block characters the frames
    /// are made of. Raster fonts cannot, and a double-clicked exe inherits whatever the
    /// user's console profile says.
    /// </summary>
    private static unsafe bool SupportsBoxDrawing()
    {
        if (Environment.GetEnvironmentVariable("PATHMEMO_ASCII") == "1") return false;

        try
        {
            var handle = Kernel32Extra.GetStdHandle(Kernel32Extra.StdOutputHandle);
            if (handle == 0 || handle == -1) return true;

            var font = new Kernel32Extra.ConsoleFontInfoEx { Size = (uint)sizeof(Kernel32Extra.ConsoleFontInfoEx) };
            if (!Kernel32Extra.GetCurrentConsoleFontEx(handle, false, &font)) return true;

            // Windows Terminal answers with a TrueType font like anything else; only the
            // legacy raster font reports without the bit set.
            return (font.FontFamily & Kernel32Extra.TrueTypeFontFamily) != 0;
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            // Unknown host: assume it can, because assuming it cannot would make every
            // modern terminal draw hashes for no reason.
            return true;
        }
    }
}
