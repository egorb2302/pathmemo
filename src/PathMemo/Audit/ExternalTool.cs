using System.Diagnostics;
using System.Text;

namespace PathMemo.Audit;

/// <summary>Captured result of a read-only system utility.</summary>
internal sealed record ToolOutput(int ExitCode, string StdOut, string StdErr, bool TimedOut)
{
    internal bool Succeeded => !TimedOut && ExitCode == 0;
}

/// <summary>
/// Runs a Windows utility for its output, and nothing else (README sections 6.3, 15.4).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Absolute path under <c>%WINDIR%\System32</c>: a <c>dism.exe</c> found through
/// <c>PATH</c> could be anyone's (threat T8).</item>
/// <item><see cref="ProcessStartInfo.ArgumentList"/>, never a joined string.</item>
/// <item>No shell, no window, a hard timeout, and the process tree is killed on it.</item>
/// <item>Output is decoded as the console's OEM code page, which is what these tools
/// write; the callers then parse numbers and structure, not English words.</item>
/// </list>
/// </remarks>
internal static class ExternalTool
{
    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    // OEM code pages (866, 850, ...) are not built into the runtime under invariant
    // globalization; the provider ships in the shared framework and costs nothing until used.
    static ExternalTool() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    internal static string System32 { get; } = Environment.GetFolderPath(Environment.SpecialFolder.System);

    internal static string PowerShell { get; } =
        Path.Combine(System32, "WindowsPowerShell", "v1.0", "powershell.exe");

    internal static ToolOutput Run(
        string exeUnderSystem32,
        IReadOnlyList<string> arguments,
        CancellationToken ct,
        TimeSpan? timeout = null)
    {
        var exe = Path.IsPathRooted(exeUnderSystem32)
            ? exeUnderSystem32
            : Path.Combine(System32, exeUnderSystem32);

        if (!exe.StartsWith(System32, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"refusing to run a tool outside System32: {exe}");

        if (!File.Exists(exe))
            return new ToolOutput(-1, "", $"{Path.GetFileName(exe)} is not present on this system", false);

        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = System32,
            StandardOutputEncoding = ConsoleEncoding(),
            StandardErrorEncoding = ConsoleEncoding(),
        };

        foreach (var argument in arguments) psi.ArgumentList.Add(argument);

        // Not inherited: a compatibility shim on our own process must not leak into DISM.
        psi.Environment.Remove("__COMPAT_LAYER");

        using var process = new Process { StartInfo = psi };
        process.Start();

        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);

        var limit = timeout ?? DefaultTimeout;
        if (!process.WaitForExit((int)limit.TotalMilliseconds))
        {
            try { process.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }

            return new ToolOutput(-1, "", $"timed out after {limit.TotalSeconds:F0} s", true);
        }

        // Second wait: WaitForExit(int) returns before the redirected streams are drained.
        process.WaitForExit();

        return new ToolOutput(process.ExitCode, stdout.Result, stderr.Result, false);
    }

    /// <summary>
    /// Runs one PowerShell expression and returns its stdout. Used for CIM queries, which
    /// give byte counts as integers regardless of the display language - the output of
    /// <c>vssadmin</c> does not (README section 6.3).
    /// </summary>
    internal static ToolOutput PowerShellCommand(string command, CancellationToken ct, TimeSpan? timeout = null) =>
        Run(PowerShell,
            ["-NoProfile", "-NonInteractive", "-NoLogo", "-ExecutionPolicy", "Bypass",
             "-OutputFormat", "Text", "-Command", command],
            ct, timeout);

    private static Encoding ConsoleEncoding()
    {
        try
        {
            return Encoding.GetEncoding((int)Platform.Native.Kernel32Extra.GetConsoleOutputCP());
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return Encoding.UTF8;
        }
    }
}
