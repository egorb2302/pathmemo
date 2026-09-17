using System.Globalization;
using PathMemo.Cli.Output;
using PathMemo.Config;
using PathMemo.Platform;

namespace PathMemo.Cli.Commands;

/// <summary>
/// Environment diagnostics: what pathmemo can and cannot do on this machine,
/// and why. First command implemented because every later phase depends on
/// the facts it reports (README section 13, acceptance criteria "CLI").
/// </summary>
internal static class DoctorCommand
{
    internal static int Run()
    {
        var w = Console.Out;

        w.WriteLine($"pathmemo {AppInfo.Version}   ·   doctor");
        w.WriteLine();

        Section(w, "PROCESS");
        Field(w, "Elevated", Elevation.IsElevated ? "yes" : "no  (fast MFT scan unavailable)");
        Field(w, "Architecture", System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString());
        Field(w, "OS", Environment.OSVersion.VersionString);
        Field(w, "Long paths", AppContext.TryGetSwitch("Switch.System.IO.UseLegacyPathHandling", out var legacy) && legacy
            ? "legacy (MAX_PATH enforced)"
            : "enabled");
        Field(w, "Logical cores", Environment.ProcessorCount.ToString(CultureInfo.InvariantCulture));
        w.WriteLine();

        Section(w, "TERMINAL");
        Field(w, "Output redirected", Console.IsOutputRedirected ? "yes  (TUI disabled)" : "no");
        Field(w, "Size", TerminalSize());
        Field(w, "Output encoding", Console.OutputEncoding.WebName);
        w.WriteLine();

        Section(w, "STORAGE");
        Field(w, "Data directory", AppPaths.DataDirectory);
        Field(w, "Exists", Directory.Exists(AppPaths.DataDirectory) ? "yes" : "no  (created on first scan)");
        w.WriteLine();

        Section(w, "VOLUMES");
        var volumes = VolumeInfo.Enumerate();
        if (volumes.Count == 0)
        {
            w.WriteLine("  none detected");
        }
        else
        {
            w.WriteLine("  VOL  FS       CLUSTER      TOTAL       USED      FREE   USED%  MFT SCAN");
            w.WriteLine("  " + new string('-', 76));
            foreach (var v in volumes)
            {
                var (available, reason) = v.MftScanAvailability;
                w.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "  {0,-4} {1,-8} {2,7} {3,10} {4,10} {5,9} {6,6:F1}  {7}",
                    v.Letter,
                    v.FileSystem,
                    v.ClusterBytes == 0 ? "?" : SizeFormat.Bytes((ulong)v.ClusterBytes),
                    SizeFormat.Bytes(v.TotalBytes),
                    SizeFormat.Bytes(v.UsedBytes),
                    SizeFormat.Bytes(v.FreeBytes),
                    v.UsedPercent,
                    available ? "ready" : reason));
            }
        }
        w.WriteLine();

        if (!Elevation.IsElevated && volumes.Any(v => v.IsNtfs))
        {
            w.WriteLine("  Hint: run as administrator to enable the MFT scanner");
            w.WriteLine("        (seconds instead of minutes, plus accurate on-disk sizes).");
            w.WriteLine();
        }

        return ExitCode.Ok;
    }

    private static string TerminalSize()
    {
        try
        {
            return $"{Console.WindowWidth}x{Console.WindowHeight}"
                 + (Console.WindowWidth < 80 || Console.WindowHeight < 24 ? "  (below the 80x24 minimum)" : "");
        }
        catch (IOException)
        {
            return "unavailable (not a console)";
        }
    }

    private static void Section(TextWriter w, string name) => w.WriteLine(name);

    private static void Field(TextWriter w, string name, string value) =>
        w.WriteLine($"  {name,-18} {value}");
}
