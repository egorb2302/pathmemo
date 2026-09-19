using System.Runtime.InteropServices;

namespace PathMemo.Platform.Native;

/// <summary>
/// dwmapi: the one call that makes a dark window look dark all the way to its title bar.
/// </summary>
/// <remarks>
/// Without <c>DWMWA_USE_IMMERSIVE_DARK_MODE</c> a dark application keeps a white caption,
/// which is the single most obvious way a window announces that nobody thought about it.
/// The attribute is unsupported before Windows 10 20H1 and the call simply fails there,
/// which is why the result is ignored: an old build gets a light caption over a dark client
/// area, and that is the correct outcome rather than something to work around.
/// </remarks>
internal static partial class DwmApi
{
    private const string Dll = "dwmapi.dll";

    internal const uint UseImmersiveDarkMode = 20;

    /// <summary>The attribute number before Windows 10 20H1 (build 19041) renamed it.</summary>
    internal const uint UseImmersiveDarkModeBefore20H1 = 19;

    [LibraryImport(Dll)]
    internal static partial int DwmSetWindowAttribute(
        nint hwnd, uint attribute, ref int pvAttribute, uint cbAttribute);

    /// <summary>Asks for a dark or light caption, and says nothing when the OS declines.</summary>
    internal static void SetDarkTitleBar(nint hwnd, bool dark)
    {
        var value = dark ? 1 : 0;

        if (DwmSetWindowAttribute(hwnd, UseImmersiveDarkMode, ref value, sizeof(int)) != 0)
            DwmSetWindowAttribute(hwnd, UseImmersiveDarkModeBefore20H1, ref value, sizeof(int));
    }
}
