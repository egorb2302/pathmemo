using System.Globalization;
using PathMemo.Platform;

namespace PathMemo.Cli.Commands;

internal enum ScheduleAction
{
    Status,
    Register,
    Off,
}

internal sealed record ScheduleOptions
{
    internal ScheduleAction Action { get; init; } = ScheduleAction.Status;
    internal ScheduleKind Kind { get; init; } = ScheduleKind.Weekly;
    internal TimeOnly Time { get; init; } = new(3, 0);
    internal DayOfWeek Day { get; init; } = DayOfWeek.Monday;
}

/// <summary>
/// Registers, reports and removes the scheduled scan (README section 10.1).
/// </summary>
/// <remarks>
/// Without this the history is decoration. Nobody runs a disk scan by hand twice a week, so
/// "what grew since Monday" has no data to answer from unless something scans on a timer -
/// and a scan that costs a third of a second because it reads the change journal is one
/// that can run on a timer without anyone noticing (README section 4.5).
/// </remarks>
internal static class ScheduleCommand
{
    internal static int Run(ScheduleOptions options, CancellationToken ct)
    {
        return options.Action switch
        {
            ScheduleAction.Off => Off(ct),
            ScheduleAction.Register => Register(options, ct),
            _ => Status(ct),
        };
    }

    private static int Status(CancellationToken ct)
    {
        var info = ScheduledScan.Query(ct);

        if (info is null)
        {
            Console.WriteLine("No scheduled scan is registered.");
            Console.WriteLine();
            Console.WriteLine("  pathmemo schedule --weekly --time 03:00     every Monday at 03:00");
            Console.WriteLine("  pathmemo schedule --daily  --time 03:00     every day at 03:00");
            return ExitCode.Ok;
        }

        Console.WriteLine($"Scheduled task \"{ScheduledScan.TaskName}\"");
        Console.WriteLine($"  Runs:        {info.Describe()}, only when the computer is idle and on AC power");
        Console.WriteLine($"  Command:     {info.Command} {info.Arguments}".TrimEnd());

        if (ScheduledScan.AccountName(info.User) is { Length: > 0 } user)
            Console.WriteLine($"  As:          {user}{(info.Elevated ? "  (highest available token)" : "")}");

        if (!info.Enabled) Console.WriteLine("  State:       disabled in the Task Scheduler");

        if (!info.Elevated)
        {
            Console.WriteLine();
            Console.WriteLine("  The scan will run unelevated, so it uses the directory walk and cannot read");
            Console.WriteLine("  the change journal. Re-register from an administrator prompt for the fast path.");
        }

        Console.WriteLine();
        Console.WriteLine("  Remove with: pathmemo schedule --off");
        return ExitCode.Ok;
    }

    private static int Register(ScheduleOptions options, CancellationToken ct)
    {
        if (ScheduledScan.Executable is null)
        {
            Console.Error.WriteLine(
                "pathmemo: a scheduled task can only be registered from pathmemo.exe, " +
                "not from a run under the dotnet host");
            return ExitCode.Failure;
        }

        if (ScheduledScan.Register(options.Kind, options.Time, options.Day, ct) is { } error)
        {
            Console.Error.WriteLine($"pathmemo: the task could not be registered - {error}");
            return ExitCode.Failure;
        }

        var when = options.Kind == ScheduleKind.Weekly
            ? string.Create(CultureInfo.InvariantCulture, $"every {options.Day} at {options.Time:HH\\:mm}")
            : string.Create(CultureInfo.InvariantCulture, $"every day at {options.Time:HH\\:mm}");

        Console.WriteLine($"Registered scheduled task \"{ScheduledScan.TaskName}\".");
        Console.WriteLine($"  Runs: {when}, only when the computer is idle and on AC power.");
        Console.WriteLine($"  Command: pathmemo {string.Join(' ', ScheduledScan.ScanArguments)}");
        Console.WriteLine("  Remove with: pathmemo schedule --off");

        if (!Elevation.IsElevated)
        {
            Console.WriteLine();
            Console.WriteLine("  Registered in your own account and without administrator rights, so the");
            Console.WriteLine("  scheduled scan will walk the disk instead of reading the MFT and the change");
            Console.WriteLine("  journal. Registering the same command from an administrator prompt replaces");
            Console.WriteLine("  the task with one that asks for the highest available token.");
        }

        return ExitCode.Ok;
    }

    private static int Off(CancellationToken ct)
    {
        var existed = ScheduledScan.Query(ct) is not null;

        if (ScheduledScan.Remove(ct) is { } error)
        {
            Console.Error.WriteLine($"pathmemo: the task could not be removed - {error}");
            return ExitCode.Failure;
        }

        // Nothing to remove is the state that was asked for, so it is a success with a
        // different sentence, not a failure.
        Console.WriteLine(existed
            ? $"Removed scheduled task \"{ScheduledScan.TaskName}\"."
            : "No scheduled scan was registered.");

        return ExitCode.Ok;
    }
}
