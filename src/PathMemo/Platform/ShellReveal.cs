using PathMemo.Platform.Native;

namespace PathMemo.Platform;

/// <summary>
/// Shows a path in Explorer, with it selected (README section 15.2).
/// </summary>
/// <remarks>
/// <para>
/// Not <c>Process.Start("explorer.exe", "/select,\"" + path + "\"")</c>. That form is a
/// string-concatenation injection waiting to happen: <c>/select</c> needs the glued
/// command line, so <c>ArgumentList</c> cannot help, and file names containing quotes do
/// exist - WSL, Cygwin and Samba all create them through <c>\\?\</c>. A scanner that shows
/// every file on the disk will meet one eventually.
/// </para>
/// <para>
/// The shell API takes the path as data instead, reuses an open Explorer window, and
/// skips a process launch. It needs COM on an apartment-threaded thread, which is why
/// this runs on a thread of its own rather than on the render loop.
/// </para>
/// </remarks>
internal static class ShellReveal
{
    internal static bool TryReveal(string path, out string? error)
    {
        string? failure = null;

        var thread = new Thread(() => failure = RevealOnThisThread(path)) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        if (!thread.Join(TimeSpan.FromSeconds(10)))
        {
            error = "Explorer did not respond";
            return false;
        }

        error = failure;
        return failure is null;
    }

    private static unsafe string? RevealOnThisThread(string path)
    {
        var hr = Ole32.CoInitializeEx(0, Ole32.ApartmentThreaded | Ole32.DisableOle1Dde);
        if (hr < 0) return $"COM could not be initialised (0x{hr:X8})";

        nint folder = 0;
        nint item = 0;

        try
        {
            // A volume root has no parent to select it in, so it is simply opened.
            var parent = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(parent))
            {
                hr = Shell32.SHParseDisplayName(path, 0, out folder, 0, out _);
                if (hr < 0) return Explain(hr, path);

                hr = Shell32.SHOpenFolderAndSelectItems(folder, 0, null, 0);
                return hr < 0 ? Explain(hr, path) : null;
            }

            hr = Shell32.SHParseDisplayName(parent, 0, out folder, 0, out _);
            if (hr < 0) return Explain(hr, parent);

            hr = Shell32.SHParseDisplayName(path, 0, out item, 0, out _);
            if (hr < 0)
            {
                // The entry is gone since the scan; opening the folder is still the
                // useful half of the answer.
                hr = Shell32.SHOpenFolderAndSelectItems(folder, 0, null, 0);
                return hr < 0 ? Explain(hr, parent) : "it is no longer there; opened the folder instead";
            }

            var children = stackalloc nint[1];
            children[0] = item;

            hr = Shell32.SHOpenFolderAndSelectItems(folder, 1, children, 0);
            return hr < 0 ? Explain(hr, path) : null;
        }
        finally
        {
            if (item != 0) Shell32.ILFree(item);
            if (folder != 0) Shell32.ILFree(folder);
            Ole32.CoUninitialize();
        }
    }

    private static string Explain(int hr, string path) => unchecked((uint)hr) switch
    {
        0x80070002 => $"not found: {path}",
        0x80070005 => $"access denied: {path}",
        _ => $"Explorer refused the path (0x{hr:X8})",
    };
}
