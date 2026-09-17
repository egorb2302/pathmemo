using System.ComponentModel;
using System.Diagnostics;

namespace PathMemo.Platform;

/// <summary>
/// Starts a second, elevated copy of this executable for the interactive session.
/// </summary>
/// <remarks>
/// The manifest says <c>asInvoker</c> on purpose: a disk tool must not demand admin
/// rights for browsing a scan. Elevation is opted into, here, for the probes that need
/// it - and the new process gets a fresh console, so the caller can simply exit.
/// </remarks>
internal static class Relaunch
{
    internal static bool AsAdministrator()
    {
        var exe = Environment.ProcessPath;
        if (exe is null)
        {
            Console.WriteLine("  cannot locate the executable to relaunch");
            return false;
        }

        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = true,      // required for the UAC prompt
            Verb = "runas",
            WorkingDirectory = Environment.CurrentDirectory,
        };
        psi.ArgumentList.Add("--interactive");

        try
        {
            Process.Start(psi);
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)     // ERROR_CANCELLED
        {
            Console.WriteLine("  elevation was declined");
            return false;
        }
        catch (Win32Exception ex)
        {
            Console.WriteLine($"  could not relaunch: {ex.Message}");
            return false;
        }
    }
}
