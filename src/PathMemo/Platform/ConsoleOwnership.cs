using System.Runtime.InteropServices;

namespace PathMemo.Platform;

/// <summary>
/// Tells apart "launched from a terminal" from "launched by double-clicking the exe".
/// </summary>
/// <remarks>
/// <para>
/// When Explorer starts a console application it allocates a fresh console for it, and
/// tears that console down the moment the process exits - so anything printed vanishes.
/// When a shell starts it, the process joins the shell's existing console instead.
/// </para>
/// <para>
/// <c>GetConsoleProcessList</c> distinguishes the two: it reports how many processes are
/// attached to this console. Exactly one means nobody else is there, so the window is ours
/// and disappears with us.
/// </para>
/// </remarks>
internal static partial class ConsoleOwnership
{
    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint GetConsoleProcessList(
        [Out] uint[] lpdwProcessList, uint dwProcessCount);

    private static bool? _cached;

    internal static bool OwnsTheWindow
    {
        get
        {
            if (_cached is { } value) return value;
            _cached = Detect();
            return _cached.Value;
        }
    }

    private static bool Detect()
    {
        if (Console.IsOutputRedirected || Console.IsInputRedirected) return false;

        try
        {
            var buffer = new uint[8];
            var count = GetConsoleProcessList(buffer, (uint)buffer.Length);

            // 0 means the call failed - no console at all, or an unusual host. Assume we do
            // not own the window: holding it open when nobody asked is the worse mistake.
            return count == 1;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
    }
}
