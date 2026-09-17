using System.Globalization;
using PathMemo.Cli.Commands;
using PathMemo.Cli.Output;
using PathMemo.Config;
using PathMemo.Platform;
using PathMemo.Snapshots;

namespace PathMemo.Cli.Interactive;

/// <summary>
/// What you get when the exe is double-clicked: a status screen and a menu, in a window
/// that stays open.
/// </summary>
/// <remarks>
/// A placeholder for the Overview screen (README section 14.1), not the TUI itself. It
/// reads a line at a time rather than driving the terminal, so it works identically in
/// conhost, Windows Terminal and anything else. P5 replaces it with the real screen.
/// </remarks>
internal static class Launcher
{
    internal static async Task<int> RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            ClearScreen();
            PrintStatus();

            var latest = SnapshotStore.Latest();

            Console.WriteLine();
            Console.WriteLine("  [A]  audit: space no scan can see (restore points, WSL, caches, hiberfil)");
            if (latest is not null) Console.WriteLine("  [B]  browse the last scan");
            Console.WriteLine("  [S]  scan C:\\");
            Console.WriteLine("  [V]  scan all fixed volumes");
            if (latest is not null) Console.WriteLine("  [L]  largest files over 1 GB");
            Console.WriteLine("  [D]  diagnostics");
            if (!Elevation.IsElevated) Console.WriteLine("  [E]  restart as administrator");
            Console.WriteLine("  [Q]  quit");
            Console.WriteLine();
            Console.Write("  > ");

            var choice = (Console.ReadLine() ?? "q").Trim().ToLowerInvariant();

            switch (choice)
            {
                case "a":
                    AuditView.Run(ct);
                    break;

                case "b" when latest is not null:
                    Browser.Run(latest.Id);
                    break;

                case "s":
                    await RunScan([@"C:\"], ct);
                    break;

                case "v":
                    await RunScan([], ct);
                    break;

                case "l" when latest is not null:
                    TopCommand.Run(new TopOptions { MinBytes = 1L << 30, Limit = 30 });
                    Pause();
                    break;

                case "d":
                    DoctorCommand.Run();
                    Pause();
                    break;

                case "e" when !Elevation.IsElevated:
                    if (Relaunch.AsAdministrator()) return ExitCode.Ok;
                    Pause();
                    break;

                case "q" or "":
                    return ExitCode.Ok;

                default:
                    Console.WriteLine("  ?");
                    Pause();
                    break;
            }
        }

        return ExitCode.Cancelled;
    }

    private static async Task RunScan(string[] roots, CancellationToken ct)
    {
        ClearScreen();
        Console.WriteLine();
        Console.WriteLine(Elevation.IsElevated
            ? "  Scanning the $MFT. A full drive takes a few seconds."
            : "  Scanning. This takes about a minute for a full drive without administrator rights.");
        Console.WriteLine("  Ctrl+C stops it and keeps what was found so far.");
        Console.WriteLine();

        try
        {
            await ScanCommand.RunAsync(new ScanOptions { Roots = roots, Top = 12 }, ct);
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine();
            Console.WriteLine("  Cancelled.");
        }

        Pause();
    }

    private static void PrintStatus()
    {
        Console.WriteLine();
        Console.WriteLine($"  pathmemo {AppInfo.Version}   ·   disk space screener");
        Console.WriteLine();

        foreach (var volume in VolumeInfo.Enumerate(fixedOnly: true))
        {
            var bar = Meter(volume.UsedPercent, 24);
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "  {0,-3} {1}  {2,8} free of {3,-8}  {4:F0}% used",
                volume.Letter, bar,
                SizeFormat.Bytes(volume.FreeBytes),
                SizeFormat.Bytes(volume.TotalBytes),
                volume.UsedPercent));
        }

        Console.WriteLine();

        var latest = SnapshotStore.Latest();
        if (latest is null)
        {
            Console.WriteLine("  No scans yet.");
            return;
        }

        try
        {
            var snapshot = SnapshotFile.Read(latest.Path);
            var tree = snapshot.Tree;

            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "  Last scan  #{0}  ·  {1:yyyy-MM-dd HH:mm} UTC  ·  {2} files  ·  {3}",
                latest.Id, snapshot.StartedUtc,
                tree.Roots.Sum(r => tree.FileCount[r]).ToString("N0", CultureInfo.InvariantCulture),
                SizeFormat.Bytes(tree.Roots.Sum(r => tree.Allocated[r]))));

            if ((snapshot.Flags & Scanning.ScanFlags.Elevated) == 0)
                Console.WriteLine("             not elevated: some paths were unreadable");
            if ((snapshot.Flags & Scanning.ScanFlags.Partial) != 0)
                Console.WriteLine("             CANCELLED: totals are incomplete");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            Console.WriteLine($"  Last scan #{latest.Id} is unreadable: {ex.Message}");
        }
    }

    private static string Meter(double percent, int width)
    {
        var filled = Math.Clamp((int)Math.Round(percent / 100 * width), 0, width);
        return "[" + new string('#', filled) + new string('.', width - filled) + "]";
    }

    /// <summary>
    /// Clearing fails on hosts that give no console buffer (a redirected run, some
    /// terminal emulators). Scrolling instead is a fine fallback; refusing to start is not.
    /// </summary>
    internal static void ClearScreen()
    {
        try { Console.Clear(); }
        catch (IOException) { Console.WriteLine(); }
    }

    internal static void Pause()
    {
        Console.WriteLine();
        Console.Write("  press Enter to continue ");
        Console.ReadLine();
    }
}
