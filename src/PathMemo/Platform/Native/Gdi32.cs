using System.Runtime.InteropServices;

namespace PathMemo.Platform.Native;

/// <summary>
/// gdi32 imports: the drawing surface the GUI paints into (README section 24.2).
/// </summary>
/// <remarks>
/// <para>
/// GDI rather than Direct2D because everything this application draws is a rectangle, a
/// line or a run of text - a treemap and a table of bars need no gradients, no transforms
/// and no compositing. GDI is in every Windows since 1985, needs no factory, no device
/// loss handling and no extra assembly, and it renders text with the same ClearType the
/// rest of the desktop uses.
/// </para>
/// <para>
/// Everything is drawn into one <c>CreateDIBSection</c> bitmap and blitted in a single
/// <c>BitBlt</c> on <c>WM_PAINT</c>. That is the same discipline the TUI renderer follows
/// for the same reason: a window that paints its parts straight to the screen flickers
/// (README section 14.7).
/// </para>
/// </remarks>
internal static partial class Gdi32
{
    private const string Dll = "gdi32.dll";

    internal const int BiRgb = 0;
    internal const uint DibRgbColors = 0;

    internal const int TransparentBk = 1;
    internal const int OpaqueBk = 2;

    internal const uint SrcCopy = 0x00CC0020;
    internal const uint PatCopy = 0x00F00021;

    internal const int DcBrush = 18;
    internal const int DcPen = 19;
    internal const int NullBrush = 5;

    internal const int EtoOpaque = 0x0002;
    internal const int EtoClipped = 0x0004;

    internal const int PsSolid = 0;

    /// <summary>Anti-aliased but not subpixel: see the remarks on <see cref="LogFont"/>.</summary>
    internal const byte ClearTypeQuality = 5;
    internal const byte AntiAliasedQuality = 4;

    internal const byte DefaultCharset = 1;
    internal const byte VariablePitch = 2;
    internal const byte FixedPitch = 1;

    internal const int FwNormal = 400;
    internal const int FwSemibold = 600;
    internal const int FwBold = 700;

    [StructLayout(LayoutKind.Sequential)]
    internal struct BitmapInfoHeader
    {
        internal uint Size;
        internal int Width;
        internal int Height;
        internal ushort Planes;
        internal ushort BitCount;
        internal uint Compression;
        internal uint SizeImage;
        internal int XPelsPerMeter;
        internal int YPelsPerMeter;
        internal uint ClrUsed;
        internal uint ClrImportant;
    }

    /// <summary>
    /// <c>LOGFONTW</c>. The face name is a fixed 32-character buffer inside the struct, so
    /// it is written through a span rather than marshalled as a string.
    /// </summary>
    /// <remarks>
    /// <see cref="Quality"/> is <see cref="ClearTypeQuality"/>: text drawn into a DIB that is
    /// blitted opaquely keeps subpixel rendering correctly, because the background is already
    /// in the bitmap when the glyphs are drawn over it. It is the same reason the whole frame
    /// is composed in one bitmap rather than in layers.
    /// </remarks>
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct LogFont
    {
        internal int Height;
        internal int Width;
        internal int Escapement;
        internal int Orientation;
        internal int Weight;
        internal byte Italic;
        internal byte Underline;
        internal byte StrikeOut;
        internal byte CharSet;
        internal byte OutPrecision;
        internal byte ClipPrecision;
        internal byte Quality;
        internal byte PitchAndFamily;
        internal fixed char FaceName[32];

        internal void SetFace(string name)
        {
            fixed (char* p = FaceName)
            {
                var span = new Span<char>(p, 32);
                span.Clear();
                name.AsSpan(0, Math.Min(name.Length, 31)).CopyTo(span);
            }
        }
    }

    /// <summary>
    /// <c>TEXTMETRICW</c>. The four character fields are <see cref="ushort"/> rather than
    /// <see cref="char"/> for the same reason <see cref="User32.PaintStruct"/> uses
    /// <see cref="int"/> for its booleans: a blittable struct is the condition for
    /// <see cref="LibraryImportAttribute"/> to marshal it at all. Nothing here reads them.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct TextMetric
    {
        internal int Height;
        internal int Ascent;
        internal int Descent;
        internal int InternalLeading;
        internal int ExternalLeading;
        internal int AveCharWidth;
        internal int MaxCharWidth;
        internal int Weight;
        internal int Overhang;
        internal int DigitizedAspectX;
        internal int DigitizedAspectY;
        internal ushort FirstChar;
        internal ushort LastChar;
        internal ushort DefaultChar;
        internal ushort BreakChar;
        internal byte Italic;
        internal byte Underlined;
        internal byte StruckOut;
        internal byte PitchAndFamily;
        internal byte CharSet;
    }

    [LibraryImport(Dll)]
    internal static partial nint CreateCompatibleDC(nint hdc);

    [LibraryImport(Dll)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DeleteDC(nint hdc);

    [LibraryImport(Dll)]
    internal static unsafe partial nint CreateDIBSection(
        nint hdc, BitmapInfoHeader* pbmi, uint usage, out void* ppvBits, nint hSection, uint offset);

    [LibraryImport(Dll)]
    internal static partial nint SelectObject(nint hdc, nint h);

    [LibraryImport(Dll)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DeleteObject(nint ho);

    [LibraryImport(Dll)]
    internal static partial nint GetStockObject(int i);

    [LibraryImport(Dll)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool BitBlt(
        nint hdc, int x, int y, int cx, int cy, nint hdcSrc, int x1, int y1, uint rop);

    [LibraryImport(Dll)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool PatBlt(nint hdc, int x, int y, int w, int h, uint rop);

    [LibraryImport(Dll)]
    internal static partial uint SetDCBrushColor(nint hdc, uint color);

    [LibraryImport(Dll)]
    internal static partial uint SetDCPenColor(nint hdc, uint color);

    [LibraryImport(Dll)]
    internal static partial uint SetTextColor(nint hdc, uint color);

    [LibraryImport(Dll)]
    internal static partial uint SetBkColor(nint hdc, uint color);

    [LibraryImport(Dll)]
    internal static partial int SetBkMode(nint hdc, int mode);

    [LibraryImport(Dll, EntryPoint = "CreateFontIndirectW")]
    internal static partial nint CreateFontIndirect(ref LogFont lplf);

    [LibraryImport(Dll, EntryPoint = "ExtTextOutW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool ExtTextOut(
        nint hdc, int x, int y, uint options, User32.Rect* lprect,
        char* lpString, uint c, int* lpDx);

    [LibraryImport(Dll, EntryPoint = "GetTextExtentPoint32W", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool GetTextExtentPoint32(
        nint hdc, char* lpString, int c, out User32.Point psizl);

    [LibraryImport(Dll, EntryPoint = "GetTextMetricsW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetTextMetrics(nint hdc, out TextMetric lptm);

    [LibraryImport(Dll)]
    internal static partial int IntersectClipRect(nint hdc, int left, int top, int right, int bottom);

    [LibraryImport(Dll)]
    internal static partial int SelectClipRgn(nint hdc, nint hrgn);

    [LibraryImport(Dll)]
    internal static partial int SaveDC(nint hdc);

    [LibraryImport(Dll)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool RestoreDC(nint hdc, int nSavedDC);

    [LibraryImport(Dll)]
    internal static partial nint CreatePen(int style, int width, uint color);

    [LibraryImport(Dll)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool MoveToEx(nint hdc, int x, int y, nint lppt);

    [LibraryImport(Dll)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool LineTo(nint hdc, int x, int y);

    [LibraryImport(Dll)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool RoundRect(
        nint hdc, int left, int top, int right, int bottom, int width, int height);
}
