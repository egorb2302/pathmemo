using System.Runtime.InteropServices;

namespace PathMemo.Platform.Native;

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
}
