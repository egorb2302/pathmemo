using System.Globalization;
using PathMemo.Cli.Output;
using PathMemo.Config;
using PathMemo.Deletion;
using PathMemo.Storage;

namespace PathMemo.Cli.Commands;

internal sealed record RmOptions
{
    internal IReadOnlyList<string> Paths { get; init; } = [];
    internal DeleteMode? Mode { get; init; }
    internal bool DryRun { get; init; }
    internal bool FromStdin { get; init; }
    internal bool Yes { get; init; }
    internal bool Force { get; init; }
    internal bool Json { get; init; }
    internal string? Reason { get; init; }
    internal string? ConfirmToken { get; init; }
    internal long? ScanId { get; init; }
}

/// <summary>
/// <c>pathmemo rm</c>: the only command that removes anything (README sections 9, 13.4).
/// </summary>
/// <remarks>
/// <para>
/// The shape is plan, show, confirm, act. The plan is computed before a single byte moves,
/// and it is what the confirmation, the dry run and the token all describe - so what the
/// user agreed to and what happens are the same list, checked again at the moment it runs.
/// </para>
/// <para>
/// Confirmation is never skipped by <c>--yes</c> when the operation is permanent and large:
/// a flag in a shell history answers every future question too, which is what
/// <c>--confirm-token</c> exists to avoid (README section 13.4).
/// </para>
/// </remarks>
internal static class RmCommand
{
    internal static int Run(RmOptions options, CancellationToken ct, DeleteSource source = DeleteSource.Cli)
    {
        var paths = options.FromStdin ? ReadStdin(options.Paths) : options.Paths;

        if (paths.Count == 0)
        {
            Console.Error.WriteLine("pathmemo: rm needs at least one path (or --from-stdin)");
            return ExitCode.Usage;
        }

        var engine = DeleteEngine.Create();
        foreach (var warning in engine.Config.Warnings) Console.Error.WriteLine($"pathmemo: {warning}");

        var plan = engine.Plan(new DeleteRequest
        {
            Paths = paths,
            Mode = options.Mode,
            DryRun = options.DryRun,
            Reason = options.Reason,
            ScanId = options.ScanId,
            Source = source,
        }, ct);

        if (options.Json) return Json(engine, plan, options, ct);

        DeleteReport.PrintPlan(plan, Console.Out);

        if (plan.IsEmpty)
        {
            // Everything was refused: that is the guard doing its job, and it deserves an
            // exit code a script can tell apart from "nothing matched" (README section 13.5).
            return plan.Refusals.Count > 0 ? ExitCode.Unsafe : ExitCode.NoData;
        }

        if (options.DryRun)
        {
            Record(journal => journal.LogDryRun(plan));

            Console.WriteLine();
            Console.WriteLine($"  Dry run: nothing was deleted. Confirmation token: {plan.Token}");
            Console.WriteLine($"  Repeat with --confirm-token {plan.Token} to run exactly this list.");
            return ExitCode.Ok;
        }

        var consent = Confirm(engine, plan, options);
        if (consent != ExitCode.Ok) return consent;

        using var catalog = ScanCatalog.TryOpen(out var error);
        if (catalog is null)
        {
            // No journal, no deletion. Everywhere else in pathmemo a busy database is a
            // warning; here it is a refusal, because "every operation has a journal entry"
            // is the promise that makes the rest of this safe (README section 9.7).
            Console.Error.WriteLine($"pathmemo: refusing to delete without the operations journal - {error}");
            return ExitCode.Locked;
        }

        Housekeeping.AnnounceQuarantine(plan, engine.Config);

        var progress = Console.IsOutputRedirected ? null : new Progress<string>(Tick);
        var outcome = engine.Execute(plan, new DeleteRepository(catalog.Database), progress, ct);

        if (progress is not null) Console.Write("\r" + new string(' ', 78) + "\r");

        DeleteReport.PrintOutcome(plan, outcome, Console.Out);

        return outcome.Failed == 0
            ? (plan.Refusals.Count > 0 ? ExitCode.Partial : ExitCode.Ok)
            : outcome.Succeeded == 0 ? ExitCode.Failure : ExitCode.Partial;
    }

    private static void Tick(string path)
    {
        var shown = PathDisplay.Shorten(path, 66);
        Console.Write($"\r  {shown,-70}");
    }

    /// <summary>
    /// Asks, unless the answer is already on the command line. Returns
    /// <see cref="ExitCode.Ok"/> to proceed.
    /// </summary>
    private static int Confirm(DeleteEngine engine, DeletePlan plan, RmOptions options)
    {
        var interactive = !Console.IsInputRedirected && !Console.IsOutputRedirected;

        if (engine.NeedsTypedConfirmation(plan))
        {
            var phrase = DeleteEngine.ConfirmationPhrase(plan);

            if (options.ConfirmToken is { } token)
            {
                if (string.Equals(token, plan.Token, StringComparison.OrdinalIgnoreCase)) return ExitCode.Ok;

                Console.Error.WriteLine(
                    $"pathmemo: that token does not describe this list (expected {plan.Token}); run --dry-run again");
                return ExitCode.Unsafe;
            }

            if (!interactive)
            {
                Console.Error.WriteLine(
                    "pathmemo: permanent deletion of this size needs a typed confirmation, "
                    + "which --yes does not provide (README section 9.2)");
                Console.Error.WriteLine($"pathmemo: run with --dry-run and pass the printed --confirm-token, or type '{phrase}' at a terminal");
                return ExitCode.Unsafe;
            }

            Console.WriteLine();
            Console.Write($"  This cannot be undone. Type '{phrase}' to continue: ");

            if ((Console.ReadLine() ?? "").Trim() != phrase)
            {
                Console.WriteLine("  cancelled");
                return ExitCode.Cancelled;
            }

            return ExitCode.Ok;
        }

        if (options.Yes || options.Force) return ExitCode.Ok;

        if (!interactive)
        {
            Console.Error.WriteLine("pathmemo: refusing to delete without confirmation; pass --yes (or --dry-run first)");
            return ExitCode.Cancelled;
        }

        Console.WriteLine();
        Console.Write("  Proceed? [y/N] ");

        if ((Console.ReadLine() ?? "").Trim().ToLowerInvariant() is not ("y" or "yes"))
        {
            Console.WriteLine("  cancelled");
            return ExitCode.Cancelled;
        }

        return ExitCode.Ok;
    }

    private static int Json(DeleteEngine engine, DeletePlan plan, RmOptions options, CancellationToken ct)
    {
        if (options.DryRun || plan.IsEmpty)
        {
            if (!plan.IsEmpty) Record(journal => journal.LogDryRun(plan));

            DeleteReport.WritePlanJson(plan, Console.Out);
            return plan.IsEmpty && plan.Refusals.Count > 0 ? ExitCode.Unsafe : ExitCode.Ok;
        }

        // Without --dry-run, JSON output still needs consent; a machine gives it with a
        // token or --yes, never by being a machine.
        var consent = Confirm(engine, plan, options with { Json = false });
        if (consent != ExitCode.Ok) return consent;

        using var catalog = ScanCatalog.TryOpen(out var error);
        if (catalog is null)
        {
            Console.Error.WriteLine($"pathmemo: refusing to delete without the operations journal - {error}");
            return ExitCode.Locked;
        }

        var outcome = engine.Execute(plan, new DeleteRepository(catalog.Database), null, ct);
        DeleteReport.WriteOutcomeJson(plan, outcome, Console.Out);

        return outcome.Failed == 0 ? ExitCode.Ok : outcome.Succeeded == 0 ? ExitCode.Failure : ExitCode.Partial;
    }

    /// <summary>Writes to the journal when it is available, and says so when it is not.</summary>
    private static void Record(Action<DeleteRepository> work)
    {
        using var catalog = ScanCatalog.TryOpen(out var error);
        if (catalog is null)
        {
            Console.Error.WriteLine($"pathmemo: the dry run was not recorded - {error}");
            return;
        }

        try
        {
            work(new DeleteRepository(catalog.Database));
        }
        catch (Exception ex) when (ex is DatabaseException or Microsoft.Data.Sqlite.SqliteException)
        {
            Console.Error.WriteLine($"pathmemo: the dry run was not recorded - {ex.Message}");
        }
    }

    /// <summary>
    /// Reads paths from standard input, one per line, so <c>top --paths-only</c> can be
    /// piped straight in (README section 21).
    /// </summary>
    private static IReadOnlyList<string> ReadStdin(IReadOnlyList<string> also)
    {
        var paths = new List<string>(also);

        while (Console.ReadLine() is { } line)
        {
            var trimmed = line.Trim().Trim('"');
            if (trimmed.Length > 0) paths.Add(trimmed);
        }

        return paths;
    }
}

/// <summary>
/// Automatic purge of expired quarantines, and the announcement that it happens at all
/// (README section 9.4).
/// </summary>
/// <remarks>
/// Announced the first time a quarantine is created rather than buried in the
/// documentation: a tool that deletes something for real seven days later must say so on
/// the day the user chooses it, not on the day it happens.
/// </remarks>
internal static class Housekeeping
{
    internal static void AnnounceQuarantine(DeletePlan plan, AppConfig config)
    {
        if (plan.Mode != DeleteMode.Quarantine || !config.Delete.AutoPurgeExpired) return;
        if (Quarantine.List().Count > 0) return;      // not the first one; they have been told

        var days = config.Delete.QuarantineRetentionDays.ToString(CultureInfo.InvariantCulture);

        Console.WriteLine();
        Console.WriteLine($"  Note: quarantined items are deleted for real after {days} days "
                        + "(delete.quarantineRetentionDays).");
        Console.WriteLine("  Until then 'pathmemo restore <op-id>' puts them back, and "
                        + "'pathmemo purge' frees the space now.");
    }

    /// <summary>
    /// Purges what has outlived its retention. Runs before the commands that report on the
    /// quarantine, so the numbers they print are true.
    /// </summary>
    internal static void PurgeExpired(DeleteRepository journal, DeleteEngine engine, TextWriter w)
    {
        if (!engine.Config.Delete.AutoPurgeExpired) return;

        foreach (var row in journal.Expired(DateTime.UtcNow))
        {
            var manifest = Quarantine.ReadManifest(row.Id, out _);
            if (manifest is null)
            {
                journal.SetStatus(row.Id, "purged");
                continue;
            }

            var (freed, failures) = engine.Purge(manifest);
            journal.SetStatus(row.Id, failures.Count == 0 ? "purged" : "partial", freed);

            w.WriteLine($"  Quarantine op-{row.Id:D6} reached its retention limit and was purged "
                      + $"({SizeFormat.Bytes(freed)} reclaimed).");
        }
    }
}
