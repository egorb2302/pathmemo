using System.Runtime.InteropServices;
using PathMemo.Gui.Render;
using PathMemo.Platform.Native;

namespace PathMemo.Gui;

internal enum MouseKind
{
    Move,
    Down,
    Up,
    DoubleClick,
    RightDown,
    Wheel,
    Leave,
}

internal readonly record struct MouseInput(
    MouseKind Kind, int X, int Y, int Wheel, bool Ctrl, bool Shift);

internal readonly record struct KeyInput(int VirtualKey, char Char, bool Ctrl, bool Shift);

/// <summary>
/// The window, the message loop, and nothing about disks (README section 24.1).
/// </summary>
/// <remarks>
/// <para>
/// Everything above this class is told about mice, keys and a rectangle to paint; everything
/// below it is Win32. The split is what lets the views be tested without a window at all
/// (README section 24.6).
/// </para>
/// <para>
/// <b>The window procedure is a function pointer, not a delegate.</b>
/// <see cref="UnmanagedCallersOnlyAttribute"/> needs no reverse marshalling stub, which keeps
/// the NativeAOT road of README section 19.4 open, and it cannot be garbage collected while
/// Windows still holds it - the classic crash of a Win32 window written in a managed language.
/// The instance behind a handle is found through a <see cref="GCHandle"/> stored in the
/// window's user data, recovered from <c>CREATESTRUCT</c> on the very first message because
/// messages arrive before <c>CreateWindowEx</c> has returned.
/// </para>
/// <para>
/// <b>Nothing may throw out of the procedure.</b> An exception crossing back into native code
/// tears the process down with no message, so everything is caught, kept, and re-thrown on the
/// thread that owns the loop once the loop has ended.
/// </para>
/// </remarks>
internal sealed class GuiWindow : IDisposable
{
    private const string ClassName = "pathmemo.window";

    private static ushort _registered;

    private readonly Canvas _canvas = new();
    private GCHandle _self;
    private FontSet? _fonts;
    private bool _tracking;

    internal nint Handle { get; private set; }

    internal uint Dpi { get; private set; } = 96;

    internal Theme Theme { get; private set; } = Theme.FromSystem();

    /// <summary>The exception a callback could not let escape, re-thrown by <see cref="Run"/>.</summary>
    private Exception? _failure;

    internal Action<IPainter, Rect>? OnPaint { get; set; }

    internal Action<MouseInput>? OnMouse { get; set; }

    internal Action<KeyInput>? OnKey { get; set; }

    /// <summary>A message posted from a worker thread (README section 24.4).</summary>
    internal Action<uint, nuint>? OnPosted { get; set; }

    internal Action? OnThemeChanged { get; set; }

    /// <summary>Asked before closing; false keeps the window open.</summary>
    internal Func<bool>? OnClosing { get; set; }

    /// <summary>The smallest useful window, in 96-dpi design pixels.</summary>
    internal (int Width, int Height) MinimumSize { get; set; } = (920, 560);

    internal bool Create(string title)
    {
        // Per-monitor v2 before the first window exists, so GetDpiForWindow tells the truth
        // and the frame is drawn at native resolution. When the call fails - an OS older than
        // 1703, or an awareness already set - Windows stretches the bitmap instead: blurry on
        // a scaled display, correct everywhere else, and not worth refusing to start over.
        User32.SetProcessDpiAwarenessContext(User32.DpiAwarenessPerMonitorV2);

        if (!Register()) return false;

        _self = GCHandle.Alloc(this);

        var style = User32.WsOverlappedWindow | User32.WsClipChildren;

        Handle = User32.CreateWindowEx(
            0, ClassName, title, style,
            unchecked((int)0x80000000), unchecked((int)0x80000000),   // CW_USEDEFAULT
            1180, 720,
            nint.Zero, nint.Zero, nint.Zero, GCHandle.ToIntPtr(_self));

        if (Handle == nint.Zero)
        {
            _self.Free();
            return false;
        }

        Dpi = Math.Max(96, User32.GetDpiForWindow(Handle));
        _fonts = new FontSet(Dpi);

        DwmApi.SetDarkTitleBar(Handle, Theme.Dark);
        LoadIcon();

        return true;
    }

    /// <summary>
    /// Puts the window on screen. Separate from <see cref="Create"/> on purpose: showing it
    /// sends the first <c>WM_PAINT</c>, and whatever is going to draw has to be listening by
    /// then. The first version showed the window inside <c>Create</c> and the result was a
    /// window that was correctly sized, correctly coloured and completely empty, because the
    /// only paint it ever received arrived before anything had subscribed.
    /// </summary>
    internal void Show()
    {
        User32.ShowWindow(Handle, User32.SwShowNormal);
        User32.UpdateWindow(Handle);
        User32.SetForegroundWindow(Handle);
    }

    /// <summary>Pumps messages until the window closes.</summary>
    internal void Run()
    {
        while (User32.GetMessage(out var msg, nint.Zero, 0, 0) > 0)
        {
            User32.TranslateMessage(ref msg);
            User32.DispatchMessage(ref msg);
        }

        if (_failure is not null) throw _failure;
    }

    internal void Invalidate() => User32.InvalidateRect(Handle, nint.Zero, false);

    internal void SetTitle(string title) => User32.SetWindowText(Handle, title);

    /// <summary>
    /// Wakes the window from any thread. The only safe way for background work to reach the UI.
    /// </summary>
    internal void Post(uint message, nuint argument = 0) =>
        User32.PostMessage(Handle, User32.WmAppFirst + message, argument, 0);

    internal void Close() => User32.PostMessage(Handle, User32.WmClose, 0, 0);

    private static bool Register()
    {
        if (_registered != 0) return true;

        unsafe
        {
            fixed (char* name = ClassName)
            {
                var wc = new User32.WndClassEx
                {
                    Size = (uint)sizeof(User32.WndClassEx),
                    Style = User32.CsHRedraw | User32.CsVRedraw | User32.CsDblClks,
                    WndProc = &Dispatch,
                    Cursor = User32.LoadCursor(nint.Zero, User32.IdcArrow),

                    // No background brush: the frame covers every pixel, and letting Windows
                    // erase first is a flash of white on every resize.
                    Background = nint.Zero,
                    ClassName = name,
                };

                _registered = User32.RegisterClassEx(ref wc);
            }
        }

        return _registered != 0;
    }

    /// <summary>
    /// Gives the window the executable's own icon, so the taskbar and Alt+Tab show it.
    /// </summary>
    /// <remarks>
    /// The icon is a resource of the exe (README section 19.1), so it is loaded from the
    /// running image by name rather than from a file: a single-file build has no icon on disk
    /// to point at.
    /// </remarks>
    private void LoadIcon()
    {
        var module = Kernel32Extra.GetModuleHandle(null);
        if (module == nint.Zero) return;

        // "#1" is the first icon group, which is the one the SDK writes from ApplicationIcon.
        var icon = User32.LoadImage(module, "#1", User32.ImageIcon, 0, 0, User32.LrDefaultSize);
        if (icon == nint.Zero) return;

        User32.SendMessage(Handle, User32.WmSetIcon, 1, icon);   // ICON_BIG
        User32.SendMessage(Handle, User32.WmSetIcon, 0, icon);   // ICON_SMALL
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
    private static nint Dispatch(nint hwnd, uint msg, nuint wParam, nint lParam)
    {
        // The first message of a window's life carries the pointer to its managed side.
        if (msg == User32.WmNcCreate)
        {
            unsafe
            {
                var create = (User32.CreateStruct*)lParam;
                if (create is not null && create->CreateParams != nint.Zero)
                    User32.SetWindowLongPtr(hwnd, User32.GwlpUserData, create->CreateParams);
            }

            return User32.DefWindowProc(hwnd, msg, wParam, lParam);
        }

        var slot = User32.GetWindowLongPtr(hwnd, User32.GwlpUserData);
        if (slot == nint.Zero) return User32.DefWindowProc(hwnd, msg, wParam, lParam);

        var window = GCHandle.FromIntPtr(slot).Target as GuiWindow;
        if (window is null) return User32.DefWindowProc(hwnd, msg, wParam, lParam);

        try
        {
            return window.Handle_(hwnd, msg, wParam, lParam);
        }
        catch (Exception ex)
        {
            // Keep it and leave: throwing from here would take the process down without a word.
            window._failure ??= ex;
            User32.PostQuitMessage(1);
            return 0;
        }
    }

    private nint Handle_(nint hwnd, uint msg, nuint wParam, nint lParam)
    {
        switch (msg)
        {
            case User32.WmPaint:
                PaintFrame(hwnd);
                return 0;

            // The frame paints every pixel it owns, so an erase is a flicker and nothing else.
            case User32.WmEraseBkgnd:
                return 1;

            case User32.WmSize:
                Invalidate();
                return 0;

            case User32.WmGetMinMaxInfo:
                unsafe
                {
                    var info = (User32.MinMaxInfo*)lParam;
                    if (info is not null)
                    {
                        info->MinTrackSize.X = Scale(MinimumSize.Width);
                        info->MinTrackSize.Y = Scale(MinimumSize.Height);
                    }
                }

                return 0;

            case User32.WmDpiChanged:
                Dpi = Math.Max(96, (uint)User32.LoWord(unchecked((nint)wParam)));
                _fonts?.Dispose();
                _fonts = new FontSet(Dpi);
                Invalidate();
                return 0;

            case User32.WmSettingChange:
                // The one setting worth reacting to: the user switched light and dark while
                // the window was open.
                var next = Theme.FromSystem();
                if (next.Dark != Theme.Dark)
                {
                    Theme = next;
                    DwmApi.SetDarkTitleBar(hwnd, Theme.Dark);
                    OnThemeChanged?.Invoke();
                    Invalidate();
                }

                return 0;

            case User32.WmMouseMove:
                TrackLeaving(hwnd);
                Mouse(MouseKind.Move, lParam, wParam);
                return 0;

            case User32.WmMouseLeave:
                _tracking = false;
                OnMouse?.Invoke(new MouseInput(MouseKind.Leave, -1, -1, 0, false, false));
                return 0;

            case User32.WmLButtonDown:
                Mouse(MouseKind.Down, lParam, wParam);
                return 0;

            case User32.WmLButtonUp:
                Mouse(MouseKind.Up, lParam, wParam);
                return 0;

            case User32.WmLButtonDblClk:
                Mouse(MouseKind.DoubleClick, lParam, wParam);
                return 0;

            case User32.WmRButtonDown:
                Mouse(MouseKind.RightDown, lParam, wParam);
                return 0;

            case User32.WmMouseWheel:
                // Wheel coordinates are on the screen, not the client area, unlike every
                // other mouse message.
                OnMouse?.Invoke(new MouseInput(
                    MouseKind.Wheel, -1, -1, User32.WheelDelta(wParam),
                    User32.KeyDown(User32.VkControl), User32.KeyDown(User32.VkShift)));
                return 0;

            case User32.WmKeyDown:
            case User32.WmSysKeyDown:
                OnKey?.Invoke(new KeyInput(
                    (int)wParam, '\0',
                    User32.KeyDown(User32.VkControl), User32.KeyDown(User32.VkShift)));
                return 0;

            case User32.WmChar:
                var ch = (char)wParam;

                // Control characters already arrived as WM_KEYDOWN; sending them twice makes
                // Ctrl+F both a shortcut and a letter typed into the search field.
                if (!char.IsControl(ch))
                    OnKey?.Invoke(new KeyInput(0, ch,
                        User32.KeyDown(User32.VkControl), User32.KeyDown(User32.VkShift)));

                return 0;

            case User32.WmClose:
                if (OnClosing is { } ask && !ask()) return 0;
                User32.DestroyWindow(hwnd);
                return 0;

            case User32.WmDestroy:
                User32.PostQuitMessage(0);
                return 0;
        }

        if (msg >= User32.WmAppFirst)
        {
            OnPosted?.Invoke(msg - User32.WmAppFirst, wParam);
            return 0;
        }

        return User32.DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private void Mouse(MouseKind kind, nint lParam, nuint wParam) =>
        OnMouse?.Invoke(new MouseInput(
            kind, User32.LoWord(lParam), User32.HiWord(lParam), 0,
            (wParam & User32.MkControl) != 0, (wParam & User32.MkShift) != 0));

    /// <summary>
    /// Asks for one <c>WM_MOUSELEAVE</c>, so a hover highlight does not stay lit after the
    /// pointer has gone. The request is consumed when it fires and has to be renewed.
    /// </summary>
    private void TrackLeaving(nint hwnd)
    {
        if (_tracking) return;

        var track = new User32.TrackMouseEventArgs
        {
            Size = (uint)Marshal.SizeOf<User32.TrackMouseEventArgs>(),
            Flags = User32.TmeLeave,
            TrackHwnd = hwnd,
        };

        _tracking = User32.TrackMouseEvent(ref track);
    }

    private void PaintFrame(nint hwnd)
    {
        var dc = User32.BeginPaint(hwnd, out var ps);

        try
        {
            if (!User32.GetClientRect(hwnd, out var client)) return;

            var width = client.Right - client.Left;
            var height = client.Bottom - client.Top;

            if (_fonts is null || !_canvas.Resize(width, height)) return;

            var painter = new GdiPainter(_canvas.Dc, _fonts);
            var area = new Rect(0, 0, width, height);

            painter.Fill(area, Theme.Background);
            OnPaint?.Invoke(painter, area);

            _canvas.Blit(dc);
        }
        finally
        {
            User32.EndPaint(hwnd, ref ps);
        }
    }

    private int Scale(int at96) => (int)Math.Round(at96 * Dpi / 96.0);

    public void Dispose()
    {
        _canvas.Dispose();
        _fonts?.Dispose();
        if (_self.IsAllocated) _self.Free();
    }
}
