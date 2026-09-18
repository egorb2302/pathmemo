using System.Globalization;
using PathMemo.Analysis;
using PathMemo.Audit;
using PathMemo.Cli.Output;
using PathMemo.Config;
using PathMemo.Deletion;

namespace PathMemo.Cli.Commands;

internal sealed record ReclaimOptions
{
    internal long? ScanId { get; init; }

    /// <summary>The highest risk the user is willing to act on. Safe unless asked.</summary>
    internal Risk Risk { get; init; } = Risk.Safe;

    internal string? RuleId { get; init; }
    internal long MinBytes { get; init; }
    internal int Limit { get; init; } = 40;
    internal bool Json { get; init; }
    internal bool DryRun { get; init; }
    internal bool Apply { get; init; }
    internal bool Yes { get; init; }
    internal bool Force { get; init; }
    internal DeleteMode? Mode { get; init; }

    /// <summary>Print the rule set and stop; no scan is needed for that.</summary>
    internal bool ListRules { get; init; }

    internal string? Keep { get; init; }
    internal string? Disable { get; init; }
    internal string? Enable { get; init; }
}

/// <summary>
/// <c>pathmemo reclaim</c>: what is worth deleting, and why (README section 7).
/// </summary>
/// <remarks>
/// <para>
/// A report by default. <c>--apply</c> hands the paths to the same <c>rm</c> that a user
/// would type - the guard, the journal, the confirmation and the free-space check are all
/// the ones from README section 9, and there is no second deletion path with its own
/// mistakes in it.
/// </para>
/// <para>
/// The risk ceiling is <c>safe</c> unless the user raises it, and rules whose remedy is
/// another tool's command are never acted on at all: <c>git gc</c> repacks a repository
/// and deleting <c>.git\objects</c> destroys it, and no flag combination should be able to
/// confuse the two (README section 7.2).
/// </para>
/// </remarks>
internal static class ReclaimCommand
{
    internal static int Run(ReclaimOptions options, CancellationToken ct)
    {
        if (options.Keep is { } keep) return Edit(ConfigFile.AddKeep(Path.GetFullPath(keep), out var m1), m1);
        if (options.Disable is { } disable) return Edit(ConfigFile.Disable(disable, out var m2), m2);
        if (options.Enable is { } enable) return Edit(ConfigFile.Enable(enable, out var m3), m3);

        var config = AppConfig.Current;
        foreach (var warning in config.Warnings) Console.Error.WriteLine($"pathmemo: {warning}");

        var rules = RuleSet.For(config.Rules);

        if (options.ListRules)
        {
            ReclaimTable.PrintRules(rules, config.Rules.Disabled, Console.Out);
            return ExitCode.Ok;
        }

        if (options.RuleId is { } wanted && !rules.Any(r => r.Id.Equals(wanted, StringComparison.OrdinalIgnoreCase)))
        {
            Console.Error.WriteLine($"pathmemo: no rule named '{wanted}'");
            Console.Error.WriteLine("pathmemo: 'pathmemo reclaim --list' prints them all");
            return ExitCode.Usage;
        }

        if (!SnapshotLoader.TryLoad(options.ScanId, out var snapshot, out var id)) return ExitCode.NoData;

        // Opening a handle per match is the slow part, and it is worth a progress line on
        // a disk that has to seek for every one of them (README section 7.3).
        var progress = options.Json || Console.IsOutputRedirected || Console.IsErrorRedirected
            ? null
            : new Progress<string>(path => Console.Error.Write(
                $"\r  checking {PathDisplay.Shorten(path, 56),-58}"));

        var report = ReclaimPlanner.Plan(
            snapshot.Tree, id, snapshot.StartedUtc, rules,
            new ReclaimQuery
            {
                RuleId = options.RuleId,
                MinBytes = options.MinBytes,
                Progress = progress,
            },
            ct);

        if (progress is not null) Console.Error.Write("\r" + new string(' ', 70) + "\r");

        if (options.Json)
        {
            ReclaimTable.WriteJson(report, options.Risk, Console.Out);
            return report.IsEmpty ? ExitCode.NoData : ExitCode.Ok;
        }

        if (options.RuleId is { } only)
        {
            var group = report.Groups.FirstOrDefault(g => g.Rule.Id.Equals(only, StringComparison.OrdinalIgnoreCase));
            if (group is null)
            {
                Console.WriteLine();
                Console.WriteLine($"{only} matched nothing in scan {id}.");
                return ExitCode.NoData;
            }

            ReclaimTable.PrintDetail(group, report, options.Limit, Console.Out);

            if (!options.Apply && !options.DryRun) return ExitCode.Ok;
        }
        else if (!options.Apply && !options.DryRun)
        {
            ReclaimTable.Print(report, options.Risk, Console.Out);
            return report.IsEmpty ? ExitCode.NoData : ExitCode.Ok;
        }

        return Act(report, options, id, ct);
    }

    /// <summary>
    /// Hands the selected paths to <c>rm</c>. Everything about how a deletion is confirmed,
    /// journalled and measured stays in one place (README section 9).
    /// </summary>
    private static int Act(ReclaimReport report, ReclaimOptions options, long scanId, CancellationToken ct)
    {
        var chosen = report.AtMost(options.Risk)
            .Where(g => g.Rule.Action == ReclaimAction.Delete)
            .ToList();

        Advise(report, options, chosen);

        if (options.Risk == Risk.Danger && !options.Force)
        {
            Console.Error.WriteLine("pathmemo: --risk danger needs --force as well (README section 13.1)");
            return ExitCode.Unsafe;
        }

        var paths = ReclaimPlanner.TargetsOf(chosen);
        if (paths.Count == 0)
        {
            Console.WriteLine();
            Console.WriteLine($"Nothing to delete at risk '{ReclaimNames.Of(options.Risk)}' in scan {scanId}.");
            return ExitCode.NoData;
        }

        var ids = string.Join(", ", chosen.Where(g => g.OfferedBytes > 0 || g.Matches.Any(m => m.Offered))
                                          .Select(g => g.Rule.Id));

        return RmCommand.Run(new RmOptions
        {
            Paths = paths,
            Mode = options.Mode,
            DryRun = options.DryRun,
            Yes = options.Yes,
            Force = options.Force,
            ScanId = scanId,
            Reason = $"reclaim: {ids}",
        }, ct, DeleteSource.Reclaim);
    }

    /// <summary>
    /// Says out loud what is being left out and why, before anything is deleted: a rule
    /// that needs another tool, one that needs a person, and one the user's own ceiling
    /// excludes are three different kinds of "not this time".
    /// </summary>
    private static void Advise(ReclaimReport report, ReclaimOptions options, IReadOnlyList<ReclaimGroup> chosen)
    {
        var commands = report.Groups.Where(g => g.Rule.Action == ReclaimAction.Command && g.Count > 0).ToList();
        var manual = report.Groups.Where(g => g.Rule.Action == ReclaimAction.Manual && g.Count > 0).ToList();
        var above = report.Groups.Where(g => g.Rule.Risk > options.Risk && g.Rule.Action == ReclaimAction.Delete).ToList();

        if (commands.Count == 0 && manual.Count == 0 && above.Count == 0) return;

        Console.WriteLine();

        foreach (var group in commands)
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  left alone: {group.Rule.Id} ({SizeFormat.Bytes(group.Bytes)}) - run '{group.Rule.Command}' instead"));

        foreach (var group in manual)
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  left alone: {group.Rule.Id} ({SizeFormat.Bytes(group.Bytes)}) - {group.Rule.What}"));

        if (above.Count > 0)
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  above the '{ReclaimNames.Of(options.Risk)}' ceiling: "
                + $"{string.Join(", ", above.Select(g => g.Rule.Id))} "
                + $"({SizeFormat.Bytes(above.Sum(g => g.Reclaimable))})"));

        var refused = chosen.Sum(g => g.RefusedCount);
        if (refused > 0)
            Console.WriteLine($"  {refused} path{(refused == 1 ? "" : "s")} the guard already refuses are not included");
    }

    private static int Edit(bool changed, string message)
    {
        if (!changed)
        {
            Console.Error.WriteLine($"pathmemo: {message}");
            return ExitCode.Failure;
        }

        Console.WriteLine(message);
        return ExitCode.Ok;
    }
}
