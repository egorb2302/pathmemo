using System.Diagnostics;

namespace PathMemo.Platform;

/// <summary>Why a file may or may not be opened, and what the user should be told first.</summary>
internal sealed record LaunchAssessment(bool Allowed, string Reason, bool FromInternet);

/// <summary>
/// Opening a file the scanner found (README section 15.3).
/// </summary>
/// <remarks>
/// <para>
/// <c>Process.Start(UseShellExecute = true)</c> on an arbitrary scanned path is running
/// untrusted code with one keystroke. This tool walks the whole disk, <c>Downloads</c>
/// included, where <c>invoice.pdf.exe</c> and <c>photo.scr</c> live. So executables are
/// refused outright - not warned about, refused - and everything else needs a
/// confirmation that shows the real extension.
/// </para>
/// <para>
/// The refusal is not configurable. A setting that turns it off would be the setting that
/// gets turned on by the user who most needs it (threat T11).
/// </para>
/// </remarks>
internal static class FileLaunch
{
    private static readonly HashSet<string> Executables = new(StringComparer.OrdinalIgnoreCase)
    {
        "exe", "com", "scr", "bat", "cmd", "ps1", "psm1", "vbs", "vbe", "js", "jse",
        "wsf", "wsh", "hta", "msi", "msp", "msc", "reg", "lnk", "url", "jar", "appx",
        "cpl", "pif", "inf", "sys", "dll", "chm",
    };

    internal static LaunchAssessment Assess(string path, bool isDirectory)
    {
        if (isDirectory)
            return new LaunchAssessment(false, "That is a directory. Press e to show it in Explorer.", false);

        var extension = Path.GetExtension(path).TrimStart('.');

        if (Executables.Contains(extension))
            return new LaunchAssessment(
                false,
                "Refusing to launch executable files. Press e to open the containing folder instead.",
                false);

        return new LaunchAssessment(true, extension.Length == 0 ? "no extension" : "." + extension, FromInternet(path));
    }

    /// <summary>
    /// Mark of the Web: the zone identifier stream Windows attaches to downloads. Worth
    /// saying out loud in the confirmation, because "I downloaded this" is exactly the
    /// context in which opening it is a bad idea.
    /// </summary>
    internal static bool FromInternet(string path)
    {
        try { return File.Exists(path + ":Zone.Identifier:$DATA"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }

    internal static bool TryOpen(string path, out string? error)
    {
        error = null;

        try
        {
            if (!File.Exists(path))
            {
                error = "it is no longer there";
                return false;
            }

            using var process = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            error = ex.Message;
            return false;
        }
    }
}
