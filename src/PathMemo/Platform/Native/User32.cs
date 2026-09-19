using System.Runtime.InteropServices;

namespace PathMemo.Platform.Native;

/// <summary>
/// user32 imports: the clipboard (README section 15.1) and, from P11, the window, the
/// message loop and the input that the GUI is built from (README section 24).
/// </summary>
/// <remarks>
/// Source-generated (<see cref="LibraryImportAttribute"/>) rather than <c>DllImport</c>, so
/// the NativeAOT road of README section 19.4 stays open. That is also why the window
/// procedure is an <see cref="System.Runtime.InteropServices.UnmanagedCallersOnlyAttribute"/>
/// function pointer rather than a marshalled delegate: a delegate needs a runtime-generated
/// reverse stub and a lifetime somebody has to own, and getting that wrong is a crash inside
/// a callback from the OS.
/// </remarks>
internal static partial class User32
{
    private const string Dll = "user32.dll";

    internal const uint CfUnicodeText = 13;

    [LibraryImport(Dll, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool OpenClipboard(nint hWndNewOwner);

    [LibraryImport(Dll, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseClipboard();

    [LibraryImport(Dll, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool EmptyClipboard();

    [LibraryImport(Dll, SetLastError = true)]
    internal static partial nint SetClipboardData(uint uFormat, nint hMem);

    // ---------------------------------------------------------------- window messages

    internal const uint WmDestroy = 0x0002;
    internal const uint WmSize = 0x0005;
    internal const uint WmSetFocus = 0x0007;
    internal const uint WmKillFocus = 0x0008;
    internal const uint WmPaint = 0x000F;
    internal const uint WmClose = 0x0010;
    internal const uint WmQuit = 0x0012;
    internal const uint WmEraseBkgnd = 0x0014;
    internal const uint WmSettingChange = 0x001A;
    internal const uint WmSetCursor = 0x0020;
    internal const uint WmGetMinMaxInfo = 0x0024;
    internal const uint WmNcCreate = 0x0081;
    internal const uint WmKeyDown = 0x0100;
    internal const uint WmChar = 0x0102;
    internal const uint WmSysKeyDown = 0x0104;
    internal const uint WmTimer = 0x0113;
    internal const uint WmMouseMove = 0x0200;
    internal const uint WmLButtonDown = 0x0201;
    internal const uint WmLButtonUp = 0x0202;
    internal const uint WmLButtonDblClk = 0x0203;
    internal const uint WmRButtonDown = 0x0204;
    internal const uint WmRButtonUp = 0x0205;
    internal const uint WmMouseWheel = 0x020A;
    internal const uint WmMouseLeave = 0x02A3;
    internal const uint WmDpiChanged = 0x02E0;

    /// <summary>
    /// The first message id Windows leaves to the application. Work finished on a background
    /// thread is announced by posting one of these, never by touching the window from that
    /// thread (README section 24.4).
    /// </summary>
    internal const uint WmAppFirst = 0x8000;

    internal const int SwHide = 0;
    internal const int SwShow = 5;
    internal const int SwShowNormal = 1;
    internal const int SwMaximize = 3;

    internal const uint WsOverlappedWindow = 0x00CF0000;
    internal const uint WsVisible = 0x10000000;
    internal const uint WsChild = 0x40000000;
    internal const uint WsClipChildren = 0x02000000;

    internal const uint CsHRedraw = 0x0002;
    internal const uint CsVRedraw = 0x0001;
    internal const uint CsDblClks = 0x0008;

    internal const int GwlpUserData = -21;

    internal const int IdcArrow = 32512;
    internal const int IdcHand = 32649;
    internal const int IdcSizeWe = 32644;
    internal const int IdcIbeam = 32513;

    internal const uint ImageIcon = 1;
    internal const uint LrDefaultSize = 0x00000040;
    internal const uint LrLoadFromFile = 0x00000010;
    internal const uint WmSetIcon = 0x0080;

    internal const uint TmeLeave = 0x00000002;

    internal const uint MkLButton = 0x0001;
    internal const uint MkControl = 0x0008;
    internal const uint MkShift = 0x0004;

    internal const int VkBack = 0x08;
    internal const int VkReturn = 0x0D;
    internal const int VkShift = 0x10;
    internal const int VkControl = 0x11;
    internal const int VkEscape = 0x1B;
    internal const int VkSpace = 0x20;
    internal const int VkPrior = 0x21;
    internal const int VkNext = 0x22;
    internal const int VkEnd = 0x23;
    internal const int VkHome = 0x24;
    internal const int VkLeft = 0x25;
    internal const int VkUp = 0x26;
    internal const int VkRight = 0x27;
    internal const int VkDown = 0x28;
    internal const int VkDelete = 0x2E;
    internal const int VkF5 = 0x74;

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Point
    {
        internal int X;
        internal int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Msg
    {
        internal nint Hwnd;
        internal uint Message;
        internal nuint WParam;
        internal nint LParam;
        internal uint Time;
        internal Point Pt;
        internal uint Private;
    }

    /// <summary>
    /// <c>PAINTSTRUCT</c>. The three <c>BOOL</c>s are <see cref="int"/> rather than
    /// <see cref="bool"/> because <see cref="LibraryImportAttribute"/> marshals only blittable
    /// types, and a managed <c>bool</c> is not one - its width is a marshalling decision.
    /// Win32's <c>BOOL</c> has always been a 32-bit int, so this is the honest declaration.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct PaintStruct
    {
        internal nint Hdc;
        internal int Erase;
        internal Rect Paint;
        internal int Restore;
        internal int IncUpdate;
        internal unsafe fixed byte Reserved[32];
    }

    /// <summary>
    /// <c>WNDCLASSEXW</c>. <see cref="WndProc"/> is a plain function pointer: see the
    /// remarks on <see cref="User32"/> for why it is not a delegate.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct WndClassEx
    {
        internal uint Size;
        internal uint Style;
        internal delegate* unmanaged[Stdcall]<nint, uint, nuint, nint, nint> WndProc;
        internal int ClsExtra;
        internal int WndExtra;
        internal nint Instance;
        internal nint Icon;
        internal nint Cursor;
        internal nint Background;
        internal char* MenuName;
        internal char* ClassName;
        internal nint IconSm;
    }

    /// <summary>
    /// <c>CREATESTRUCTW</c>, read on <c>WM_NCCREATE</c> to recover the handle to the managed
    /// window object. The first messages arrive before <c>CreateWindowEx</c> has returned, so
    /// there is no other moment at which the association could be made.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct CreateStruct
    {
        internal nint CreateParams;
        internal nint Instance;
        internal nint Menu;
        internal nint Parent;
        internal int Cy;
        internal int Cx;
        internal int Y;
        internal int X;
        internal int Style;
        internal nint Name;
        internal nint Class;
        internal uint ExStyle;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MinMaxInfo
    {
        internal Point Reserved;
        internal Point MaxSize;
        internal Point MaxPosition;
        internal Point MinTrackSize;
        internal Point MaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct TrackMouseEventArgs
    {
        internal uint Size;
        internal uint Flags;
        internal nint TrackHwnd;
        internal uint HoverTime;
    }

    [LibraryImport(Dll, EntryPoint = "RegisterClassExW", SetLastError = true)]
    internal static partial ushort RegisterClassEx(ref WndClassEx lpwcx);

    [LibraryImport(Dll, EntryPoint = "CreateWindowExW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint CreateWindowEx(
        uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight,
        nint hWndParent, nint hMenu, nint hInstance, nint lpParam);

    [LibraryImport(Dll, EntryPoint = "DefWindowProcW")]
    internal static partial nint DefWindowProc(nint hWnd, uint msg, nuint wParam, nint lParam);

    [LibraryImport(Dll, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DestroyWindow(nint hWnd);

    [LibraryImport(Dll)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ShowWindow(nint hWnd, int nCmdShow);

    [LibraryImport(Dll)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool UpdateWindow(nint hWnd);

    [LibraryImport(Dll)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetForegroundWindow(nint hWnd);

    [LibraryImport(Dll, EntryPoint = "GetMessageW")]
    internal static partial int GetMessage(out Msg lpMsg, nint hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [LibraryImport(Dll)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool TranslateMessage(ref Msg lpMsg);

    [LibraryImport(Dll, EntryPoint = "DispatchMessageW")]
    internal static partial nint DispatchMessage(ref Msg lpMsg);

    [LibraryImport(Dll)]
    internal static partial void PostQuitMessage(int nExitCode);

    [LibraryImport(Dll, EntryPoint = "PostMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool PostMessage(nint hWnd, uint msg, nuint wParam, nint lParam);

    [LibraryImport(Dll)]
    internal static partial nint BeginPaint(nint hWnd, out PaintStruct lpPaint);

    [LibraryImport(Dll)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool EndPaint(nint hWnd, ref PaintStruct lpPaint);

    [LibraryImport(Dll)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool InvalidateRect(nint hWnd, nint lpRect, [MarshalAs(UnmanagedType.Bool)] bool bErase);

    [LibraryImport(Dll)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetClientRect(nint hWnd, out Rect lpRect);

    [LibraryImport(Dll, EntryPoint = "SetWindowTextW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetWindowText(nint hWnd, string lpString);

    [LibraryImport(Dll, EntryPoint = "LoadCursorW", SetLastError = true)]
    internal static partial nint LoadCursor(nint hInstance, nint lpCursorName);

    [LibraryImport(Dll)]
    internal static partial nint SetCursor(nint hCursor);

    [LibraryImport(Dll, EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    internal static partial nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong);

    [LibraryImport(Dll, EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    internal static partial nint GetWindowLongPtr(nint hWnd, int nIndex);

    [LibraryImport(Dll)]
    internal static partial uint GetDpiForWindow(nint hWnd);

    [LibraryImport(Dll, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetProcessDpiAwarenessContext(nint value);

    [LibraryImport(Dll)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool TrackMouseEvent(ref TrackMouseEventArgs lpEventTrack);

    [LibraryImport(Dll)]
    internal static partial nint SetCapture(nint hWnd);

    [LibraryImport(Dll)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ReleaseCapture();

    [LibraryImport(Dll)]
    internal static partial short GetKeyState(int nVirtKey);

    [LibraryImport(Dll)]
    internal static partial int GetSystemMetrics(int nIndex);

    [LibraryImport(Dll, EntryPoint = "LoadImageW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint LoadImage(
        nint hInst, string name, uint type, int cx, int cy, uint load);

    [LibraryImport(Dll, EntryPoint = "SendMessageW")]
    internal static partial nint SendMessage(nint hWnd, uint msg, nuint wParam, nint lParam);

    /// <summary>
    /// <c>DrawTextW</c>: the one call that lays out, clips and ellipsises a run of text.
    /// </summary>
    /// <remarks>
    /// Worth an import of its own rather than hand-rolled truncation, because it brings two
    /// things this application specifically needs. <see cref="DtPathEllipsis"/> is exactly the
    /// middle truncation README section 14.4 asks for, measured in pixels by the same engine
    /// that draws. And <see cref="DtNoPrefix"/> stops <c>&amp;</c> in a file name from being
    /// read as a menu accelerator - without it a file called <c>a&amp;b.txt</c> draws as
    /// <c>ab</c> with an underline, which is a name the user cannot recognise and a display
    /// bug of precisely the kind section 14.4 exists to prevent.
    /// </remarks>
    [LibraryImport(Dll, EntryPoint = "DrawTextW", StringMarshalling = StringMarshalling.Utf16)]
    internal static unsafe partial int DrawText(
        nint hdc, char* lpchText, int cchText, ref Rect lprc, uint format);

    internal const uint DtLeft = 0x00000000;
    internal const uint DtCenter = 0x00000001;
    internal const uint DtRight = 0x00000002;
    internal const uint DtVCenter = 0x00000004;
    internal const uint DtSingleLine = 0x00000020;
    internal const uint DtNoClip = 0x00000100;
    internal const uint DtCalcRect = 0x00000400;
    internal const uint DtNoPrefix = 0x00000800;
    internal const uint DtPathEllipsis = 0x00004000;
    internal const uint DtEndEllipsis = 0x00008000;

    /// <summary>
    /// Per-monitor-v2. Passed as a bare handle value because the constant is what the API
    /// takes: -4 is <c>DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2</c>.
    /// </summary>
    internal static readonly nint DpiAwarenessPerMonitorV2 = -4;

    /// <summary>
    /// A <c>WM_MOUSEWHEEL</c> delta, in notches. Positive is away from the user, and the
    /// high word is signed - reading it unsigned scrolls the wrong way on every other notch.
    /// </summary>
    internal static int WheelDelta(nuint wParam) => (short)(((ulong)wParam >> 16) & 0xFFFF) / 120;

    internal static int LoWord(nint value) => (short)((ulong)value & 0xFFFF);

    internal static int HiWord(nint value) => (short)(((ulong)value >> 16) & 0xFFFF);

    internal static bool KeyDown(int vk) => (GetKeyState(vk) & 0x8000) != 0;
}
