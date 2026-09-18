using PathMemo.Audit;
using PathMemo.Cli.Output;
using PathMemo.Deletion;
using PathMemo.Platform;
using PathMemo.Storage;

namespace PathMemo.Cli.Commands;

/// <summary>
/// <c>pathmemo audit --apply &lt;finding&gt;</c>: the one path by which a remedy is run
/// rather than printed (README sections 6.3, 9.7).
/// </summary>
/// <remarks>
/// <para>
/// The audit itself changes nothing, ever. This command exists because "copy this command
/// and paste it into an elevated prompt" is what people actually skip - so the alternative
/// has to be at least as accountable as the manual route: it is confirmed, it is elevated
/// or it refuses, and it lands in the operations journal with what it freed.
/// </para>
/// <para>
/// A remedy that deletes paths goes through the ordinary deletion engine, guard and all
/// (README section 9.3). It removes the <b>contents</b> of the directories a finding names
/// and never the directories themselves: <c>%TEMP%</c> and a browser's cache folder are
/// expected to exist by whatever owns them.
/// </para>
/// </remarks>
internal static class AuditApplyCommand
{
    internal static int Run(string findingId, bool yes, CancellationToken ct)
    {
        var probes = AuditRunner.AllProbes()
            .Where(p => p.Id.Equals(findingId, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (probes.Count == 0)
        {
            Console.Error.WriteLine($"pathmemo: no audit probe named '{findingId}'");
            Console.Error.WriteLine("Known probes: " + string.Join(", ", AuditRunner.AllProbes().Select(p => p.Id)));
            return ExitCode.Usage;
        }

        var report = AuditRunner.Run(probes, null, ct);
        var finding = report.Findings.FirstOrDefault(f => f.Id.Equals(findingId, StringComparison.OrdinalIgnoreCase));

        if (finding is null || finding.Status != FindingStatus.Measured)
        {
            Console.Error.WriteLine($"pathmemo: '{findingId}' has nothing to apply - {Why(finding)}");
            if (finding?.Note is { } note) Console.Error.WriteLine($"pathmemo: {note}");

            return finding?.Status == FindingStatus.NeedsElevation ? ExitCode.NeedsElevation : ExitCode.NoData;
        }

        Console.WriteLine();
        Console.WriteLine($"{finding.Title}   {SizeFormat.Bytes(finding.ReclaimableBytes ?? finding.UsedBytes ?? 0)}   "
                        + finding.Risk.ToString().ToLowerInvariant());
        Console.WriteLine("  " + finding.Explanation);

        // The bin has an API of its own, and using it beats shelling out to PowerShell to
        // do the same thing one layer further away (README section 9.6).
        if (finding.Id.Equals("recyclebin", StringComparison.OrdinalIgnoreCase))
            return EmptyBin(finding, yes);

        var deletion = finding.Remedies.FirstOrDefault(r => r.Kind == RemedyKind.DeletePaths);
        if (deletion is not null) return ApplyDeletion(finding, deletion, yes, ct);

        var command = finding.Remedies.FirstOrDefault(r => r.Kind == RemedyKind.RunCommand);
        if (command is not null) return ApplyCommand(finding, command, yes, ct);

        Console.Error.WriteLine("pathmemo: this finding has no remedy pathmemo can carry out; "
                              + $"run 'pathmemo audit --id {findingId}' to see what it suggests");
        return ExitCode.NoData;
    }

    private static int EmptyBin(AuditFinding finding, bool yes)
    {
        Console.WriteLine($"  → empty the Recycle Bin on {(finding.Volume.Length > 0 ? finding.Volume : "every volume")}");

        if (!Confirm(finding, yes)) return ExitCode.Cancelled;

        var volume = finding.Volume.Length >= 2 ? finding.Volume[..2] + "\\" : "C:\\";
        var before = FreeSpace.Read([volume]);

        var error = RecycleBin.Empty(volume);

        Thread.Sleep(500);
        var freed = FreeSpace.Delta(before, FreeSpace.Read([volume])) ?? 0;

        Console.WriteLine();
        Console.WriteLine(error is null
            ? $"  Done. Free space on {volume} changed by {SizeFormat.Bytes(freed)} "
              + $"(predicted {SizeFormat.Bytes(finding.ReclaimableBytes ?? 0)})."
            : $"  {error}");

        Journal(finding, new Remedy(RemedyKind.RunCommand, $"empty the Recycle Bin on {volume}"),
            freed, error is null);

        return error is null ? ExitCode.Ok : ExitCode.Failure;
    }

    private static string Why(AuditFinding? finding) => finding?.Status switch
    {
        null => "the probe returned no result",
        FindingStatus.NeedsElevation => "it needs administrator rights to measure, let alone change",
        FindingStatus.NotApplicable => "this machine does not have it",
        FindingStatus.NoSnapshot => "it needs a scan first",
        FindingStatus.Unknown => "its size could not be determined, so neither can the effect",
        FindingStatus.Error => "the probe failed",
        _ => "nothing to do",
    };

    private static int ApplyDeletion(AuditFinding finding, Remedy remedy, bool yes, CancellationToken ct)
    {
        Console.WriteLine("  → " + remedy.Display);
        if (remedy.Caveat is { } caveat) Console.WriteLine("  ⚠ " + caveat);

        var targets = Contents(finding.Paths);
        if (targets.Count == 0)
        {
            Console.WriteLine();
            Console.WriteLine("  Nothing left to delete there.");
            return ExitCode.Ok;
        }

        return RmCommand.Run(new RmOptions
        {
            Paths = targets,
            Yes = yes,
            Reason = $"audit --apply {finding.Id}",
        }, ct, DeleteSource.Audit);
    }

    /// <summary>
    /// The children of each directory the finding names, or the file itself.
    /// </summary>
    /// <remarks>
    /// One level, not a recursive list: the engine deletes a directory whole, so handing it
    /// the top of each subtree is both shorter and exactly as complete.
    /// </remarks>
    private static List<string> Contents(IReadOnlyList<string> paths)
    {
        var targets = new List<string>();

        foreach (var path in paths)
        {
            try
            {
                if (File.Exists(path))
                {
                    targets.Add(path);
                    continue;
                }

                if (!Directory.Exists(path)) continue;

                targets.AddRange(Directory.EnumerateFileSystemEntries(path));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"pathmemo: {path} could not be listed - {ex.Message}");
            }
        }

        return targets;
    }

    private static int ApplyCommand(AuditFinding finding, Remedy remedy, bool yes, CancellationToken ct)
    {
        Console.WriteLine("  → " + remedy.Display);
        if (remedy.Caveat is { } caveat) Console.WriteLine("  ⚠ " + caveat);

        if (remedy.NeedsElevation && !Elevation.IsElevated)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("pathmemo: this remedy needs administrator rights. Restart pathmemo elevated, "
                                  + "or run the command above in an elevated prompt.");
            return ExitCode.NeedsElevation;
        }

        var parts = Split(remedy.Display);
        if (parts.Count == 0)
        {
            Console.Error.WriteLine("pathmemo: that remedy is not a command pathmemo can run");
            return ExitCode.NoData;
        }

        if (!Confirm(finding, yes)) return ExitCode.Cancelled;

        var volume = finding.Volume.Length >= 2 ? finding.Volume[..2] + "\\" : "C:\\";
        var before = FreeSpace.Read([volume]);

        // powershell.exe does not live directly in System32, and ExternalTool refuses
        // anything that resolves outside it - so the one tool with a different home is
        // named by its absolute path (README section 6.3).
        var exe = parts[0].Equals("powershell", StringComparison.OrdinalIgnoreCase)
            ? ExternalTool.PowerShell
            : parts[0] + ".exe";

        ToolOutput output;
        try
        {
            output = ExternalTool.Run(exe, parts[1..], ct, TimeSpan.FromMinutes(10));
        }
        catch (ArgumentException ex)
        {
            // ExternalTool refuses anything outside System32 (README section 6.3). A remedy
            // that names such a tool is shown, never run.
            Console.Error.WriteLine($"pathmemo: {ex.Message}");
            return ExitCode.Unsafe;
        }

        Thread.Sleep(500);
        var freed = FreeSpace.Delta(before, FreeSpace.Read([volume])) ?? 0;

        Console.WriteLine();
        Console.WriteLine(output.Succeeded
            ? $"  Done. Free space on {volume} changed by {SizeFormat.Bytes(freed)} "
              + $"(predicted {SizeFormat.Bytes(finding.ReclaimableBytes ?? 0)})."
            : $"  The command failed (exit code {output.ExitCode}).");

        var tail = (output.StdErr.Length > 0 ? output.StdErr : output.StdOut).Trim();
        if (tail.Length > 0)
            foreach (var line in tail.Split('\n').TakeLast(6)) Console.WriteLine("    " + line.TrimEnd());

        Journal(finding, remedy, freed, output.Succeeded);

        return output.Succeeded ? ExitCode.Ok : ExitCode.Failure;
    }

    private static bool Confirm(AuditFinding finding, bool yes)
    {
        if (yes && finding.Risk == Risk.Safe) return true;

        var interactive = !Console.IsInputRedirected && !Console.IsOutputRedirected;

        if (!interactive)
        {
            Console.Error.WriteLine(finding.Risk == Risk.Safe
                ? "pathmemo: --apply needs --yes when it cannot ask"
                : $"pathmemo: applying a {finding.Risk.ToString().ToLowerInvariant()} remedy needs a typed confirmation at a terminal");
            return false;
        }

        Console.WriteLine();

        // Risk above Safe means a capability is lost - restore points, hibernation, the
        // ability to roll back an update. That is worth typing for (README section 9.2).
        if (finding.Risk != Risk.Safe)
        {
            var phrase = $"apply {finding.Id}";
            Console.Write($"  This cannot be undone. Type '{phrase}' to continue: ");

            if ((Console.ReadLine() ?? "").Trim() == phrase) return true;

            Console.WriteLine("  cancelled");
            return false;
        }

        Console.Write("  Run it? [y/N] ");
        if ((Console.ReadLine() ?? "").Trim().ToLowerInvariant() is "y" or "yes") return true;

        Console.WriteLine("  cancelled");
        return false;
    }

    /// <summary>
    /// Records the run. A command that changes the system belongs in the same journal as a
    /// deletion, which is exactly why <c>--apply</c> waited for P6 (README section 6.4).
    /// </summary>
    private static void Journal(AuditFinding finding, Remedy remedy, long freed, bool succeeded)
    {
        using var catalog = ScanCatalog.TryOpen(out var error);
        if (catalog is null)
        {
            Console.Error.WriteLine($"pathmemo: the operation was not journalled - {error}");
            return;
        }

        try
        {
            var journal = new DeleteRepository(catalog.Database);
            var plan = new DeletePlan
            {
                Items = [],
                Refusals = [],
                Mode = DeleteMode.Permanent,
                Source = DeleteSource.Audit,
                Reason = $"{finding.Id}: {remedy.Display}",
            };

            var opId = journal.Begin(plan, DateTime.UtcNow, null);

            journal.Complete(new OpOutcome
            {
                OpId = opId,
                Mode = DeleteMode.Permanent,
                Items = [],
                PredictedBytes = finding.ReclaimableBytes ?? 0,
                ActualFreedBytes = freed,
            }, DateTime.UtcNow);

            if (!succeeded) journal.SetStatus(opId, "failed");
        }
        catch (Exception ex) when (ex is DatabaseException or Microsoft.Data.Sqlite.SqliteException)
        {
            Console.Error.WriteLine($"pathmemo: the operation was not journalled - {ex.Message}");
        }
    }

    /// <summary>
    /// Splits a remedy into a program and its arguments, honouring double quotes. These
    /// strings come from pathmemo's own probes, not from user input, so this is a reader
    /// rather than a shell - there is no expansion of any kind.
    /// </summary>
    private static List<string> Split(string command)
    {
        var parts = new List<string>();
        var current = new System.Text.StringBuilder();
        var quoted = false;

        foreach (var c in command)
        {
            switch (c)
            {
                case '"': quoted = !quoted; break;
                case ' ' or '\t' when !quoted:
                    if (current.Length > 0) parts.Add(current.ToString());
                    current.Clear();
                    break;
                default: current.Append(c); break;
            }
        }

        if (current.Length > 0) parts.Add(current.ToString());

        // The probes write "vssadmin delete shadows ..."; ExternalTool adds System32 and
        // the .exe, and refuses anything that resolves outside it.
        if (parts.Count > 0 && parts[0].EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            parts[0] = parts[0][..^4];

        return parts;
    }
}
