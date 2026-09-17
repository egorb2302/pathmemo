using System.Runtime.InteropServices;
using System.Text;
using PathMemo.Platform.Native;

namespace PathMemo.Platform;

/// <summary>
/// Puts text on the clipboard (README section 15.1).
/// </summary>
/// <remarks>
/// Forty lines of P/Invoke on a dedicated STA thread, rather than
/// <c>System.Windows.Forms.Clipboard</c> (+10 MB, needs a message pump) or <c>clip.exe</c>
/// (an extra process with a fussy UTF-16 pipe). OSC 52 for SSH sessions is P10.
/// </remarks>
internal static class Clipboard
{
    internal static bool TrySetText(string text, out string? error)
    {
        string? failure = null;

        var thread = new Thread(() => failure = SetOnThisThread(text)) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        if (!thread.Join(TimeSpan.FromSeconds(3)))
        {
            error = "the clipboard did not respond (another application is holding it)";
            return false;
        }

        error = failure;
        return failure is null;
    }

    private static string? SetOnThisThread(string text)
    {
        // The clipboard is held briefly by whichever app last used it; a few retries is
        // what every clipboard library does.
        var opened = false;
        for (var attempt = 0; attempt < 10 && !opened; attempt++)
        {
            opened = User32.OpenClipboard(0);
            if (!opened) Thread.Sleep(20);
        }

        if (!opened) return "could not open the clipboard";

        try
        {
            if (!User32.EmptyClipboard()) return "could not clear the clipboard";

            var bytes = (text.Length + 1) * 2;
            var handle = Kernel32Extra.GlobalAlloc(Kernel32Extra.GmemMoveable, (nuint)bytes);
            if (handle == 0) return "out of memory";

            var target = Kernel32Extra.GlobalLock(handle);
            if (target == 0)
            {
                Kernel32Extra.GlobalFree(handle);
                return "could not lock clipboard memory";
            }

            unsafe
            {
                var span = new Span<byte>((void*)target, bytes);
                Encoding.Unicode.GetBytes(text, span);
                span[^2] = 0;
                span[^1] = 0;
            }

            Kernel32Extra.GlobalUnlock(handle);

            // On success the system owns the memory; on failure it is still ours to free.
            if (User32.SetClipboardData(User32.CfUnicodeText, handle) == 0)
            {
                Kernel32Extra.GlobalFree(handle);
                return $"SetClipboardData failed (error {Marshal.GetLastWin32Error()})";
            }

            return null;
        }
        finally
        {
            User32.CloseClipboard();
        }
    }
}
