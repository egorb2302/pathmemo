using System.Globalization;
using PathMemo.Cli.Output;
using PathMemo.Config;
using PathMemo.Deletion;
using PathMemo.Duplicates;
using PathMemo.Storage;

namespace PathMemo.Cli.Commands;

internal sealed record DupesOptions
{
    internal long? ScanId { get; init; }
    internal long? MinBytes { get; init; }
    internal long MaxBytes { get; init; }
    internal string? Under { get; init; }
    internal IReadOnlySet<string>? Extensions { get; init; }
    internal HashKind? Algorithm { get; init; }
    internal int Limit { get; init; } = 25;

    /// <summary>One group in full, numbered as the table printed it.</summary>
    internal int? Group { get; init; }

    internal bool All { get; init; }
    internal bool PathsOnly { get; init; }
    internal bool Json { get; init; }
    internal bool DryRun { get; init; }
    internal bool Apply { get; init; }
    internal bool Yes { get; init; }
    internal bool Force { get; init; }
    internal DeleteMode? Mode { get; init; }

    /// <summary>Show the stored run instead of reading the disk again.</summary>
    internal bool Cached { get; init; }

    /// <summary>Stop after stage 0 and say what a real run would read.</summary>
    internal bool Estimate { get; init; }

    internal bool NoVerify { get; init; }
    internal bool NoCache { get; init; }
    internal bool SameVolume { get; init; }
}

/// <summary>
/// <c>pathmemo dupes</c>: files that are the same file twice (README section 8).
/// </summary>
/// <remarks>
/// <para>
/// The only command that reads file contents. Everything else in pathmemo works from
/// metadata, which is why a scan of a million files takes ten seconds and this takes as
/// long as reading the candidates - so it says how much it is about to read before it
/// starts, and it never reads a cloud placeholder at all (README sections 8.4, 8.5).
/// </para>
/// <para>
/// <c>--apply</c> hands the copies to the same <c>rm</c> a user would type, after
/// recomputing the hash of every survivor and every victim. Two independent things have to
/// agree that a file is a duplicate before it goes: the search, and the check a moment
/// before the deletion (README section 8.4).
/// </para>
/// </remarks>
internal static class DupesCommand
{
    internal static int Run(DupesOptions options, CancellationToken ct)
    {
        var config = AppConfig.Current;
        foreach (var warning in config.Warnings) Console.Error.WriteLine($"pathmemo: {warning}");

        using var catalog = ScanCatalog.TryOpen(out var error);
        if (catalog is null) Console.Error.WriteLine($"pathmemo: running without the store - {error}");

        var why = "";
        var report = options.Cached
            ? Stored(catalog, out why)
            : Search(options, config, catalog, ct);

        if (report is null)
        {
            if (options.Cached) Console.Error.WriteLine($"pathmemo: {why}");
            return ExitCode.NoData;
        }

        if (options.Json)
        {
            DupeTable.WriteJson(report, Console.Out);
            return report.IsEmpty ? ExitCode.NoData : ExitCode.Ok;
        }

        var duplicates = report.Duplicates.ToList();

        if (options.PathsOnly)
        {
            DupeTable.PrintPaths(duplicates, Console.Out);
            return duplicates.Count == 0 ? ExitCode.NoData : ExitCode.Ok;
        }

        if (options.Group is { } wanted)
        {
            if (wanted < 1 || wanted > duplicates.Count)
            {
                Console.Error.WriteLine($"pathmemo: there is no group {wanted}; the run found {duplicates.Count}");
                return ExitCode.NoData;
            }

            DupeTable.PrintGroup(duplicates[wanted - 1], wanted, options.Limit, Console.Out);
            return ExitCode.Ok;
        }

        if (options.All)
        {
            var number = 0;
            foreach (var group in report.Groups) DupeTable.PrintGroup(group, ++number, options.Limit, Console.Out);
            return report.IsEmpty ? ExitCode.NoData : ExitCode.Ok;
        }

        if (options.Estimate) return ExitCode.Ok;

        if (!options.Apply && !options.DryRun)
        {
            DupeTable.Print(report, options.Limit, Console.Out);
            return report.Declined ? ExitCode.Cancelled
                 : report.IsEmpty ? ExitCode.NoData
                 : ExitCode.Ok;
        }

        return Act(report, options, ct);
    }

    /// <summary>Runs the five stages against the newest (or named) scan.</summary>
    private static DupeReport? Search(
        DupesOptions options, AppConfig config, ScanCatalog? catalog, CancellationToken ct)
    {
        if (!SnapshotLoader.TryLoad(options.ScanId, out var snapshot, out var id)) return null;

        var settings = config.Duplicates;
        var algorithm = options.Algorithm ?? settings.HashAlgorithm;

        var cache = options.NoCache || catalog is null
            ? null
            : new HashCache(catalog.Database, algorithm, settings.HashCacheMaxEntries);

        var interactive = !Console.IsInputRedirected && !Console.IsOutputRedirected && !options.Yes;

        var query = new DupeQuery
        {
            MinBytes = options.MinBytes ?? settings.MinSize,
            MaxBytes = options.MaxBytes,
            Under = options.Under,
            Extensions = options.Extensions,
            Algorithm = algorithm,
            CrossVolume = !options.SameVolume && settings.CrossVolume,
            SkipCloudOnly = settings.SkipCloudOnly,
            ByteForByte = !options.NoVerify && settings.ByteForByteVerify,
            PartialHashBytes = (int)settings.PartialHashBytes,
            BufferBytes = (int)settings.BufferSize,
            Cache = cache,
            Confirm = options.Estimate ? Estimate : interactive ? Notice : Announce,
            Progress = options.Json || Console.IsErrorRedirected ? null : new Progress<DupeProgress>(Tick),
        };

        var report = DuplicateFinder.Find(snapshot.Tree, id, query, ct);

        if (!Console.IsErrorRedirected) Console.Error.Write("\r" + new string(' ', 78) + "\r");

        // The run is a cache of one, so a screen can show it without reading the disk
        // again. A failure to store it is not a reason to throw the answer away.
        if (catalog is not null && !report.Declined)
        {
            try
            {
                new DupeRepository(catalog.Database).Save(report);
            }
            catch (Exception ex) when (ex is DatabaseException or Microsoft.Data.Sqlite.SqliteException)
            {
                Console.Error.WriteLine($"pathmemo: the result was not stored - {ex.Message}");
            }
        }

        return report;
    }

    /// <summary>
    /// <c>--estimate</c>: the question the notice asks, without the answer costing anything.
    /// </summary>
    /// <remarks>
    /// Stage 0 is free - it reads the snapshot, not the disk - so the size of the job is
    /// knowable before the job starts. A script deciding whether tonight is the night for a
    /// two-hundred-gigabyte read should not have to start one to find out.
    /// </remarks>
    private static DupeConsent Estimate(long bytes, int files)
    {
        Console.WriteLine();
        Console.WriteLine($"  {files.ToString("N0", CultureInfo.InvariantCulture)} files share a size "
                        + $"with something else, {SizeFormat.Bytes(bytes)} in all.");
        Console.WriteLine("  A search would read up to that much; the hash cache and the partial-hash "
                        + "stage usually cut it.");
        Console.WriteLine("  Run 'pathmemo dupes' to do it.");

        return DupeConsent.Cancel;
    }

    private static DupeReport? Stored(ScanCatalog? catalog, out string why)
    {
        why = "";

        if (catalog is null)
        {
            why = "--cached needs the database, and it could not be opened";
            return null;
        }

        var stored = new DupeRepository(catalog.Database).Latest();
        if (stored is not null) return stored;

        why = "no duplicate search has been run yet - 'pathmemo dupes' does one";
        return null;
    }

    /// <summary>
    /// The notice of README section 8.5, and the only place the tool mentions Defender.
    /// </summary>
    /// <remarks>
    /// It says what it is about to do and offers a smaller version of it. Changing the
    /// user's security settings is out of scope by design (README section 2); printing the
    /// command they could run themselves is not.
    /// </remarks>
    private static DupeConsent Notice(long bytes, int files)
    {
        if (bytes < 1L << 30) return DupeConsent.Continue;

        Console.WriteLine();
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"  Hashing will read {SizeFormat.Bytes(bytes)} from disk, across {files:N0} files."));
        Console.WriteLine("  Real-time antivirus scanning may slow this down 2-5x and use significant CPU.");
        Console.WriteLine();
        Console.WriteLine("  You can exclude pathmemo from Defender yourself (run as administrator):");
        Console.WriteLine("    Add-MpPreference -ExclusionProcess pathmemo.exe");
        Console.WriteLine("  pathmemo will not change your security settings.");
        Console.WriteLine();
        Console.Write("  [Enter] continue   [s] skip files over 1 GB   [Esc] cancel  ");

        var key = Console.ReadKey(intercept: true);
        Console.WriteLine();

        return key.Key switch
        {
            ConsoleKey.Escape or ConsoleKey.Q => DupeConsent.Cancel,
            ConsoleKey.S => DupeConsent.SkipLarge,
            _ => DupeConsent.Continue,
        };
    }

    /// <summary>The same notice where nobody can answer it: said once, then done anyway.</summary>
    private static DupeConsent Announce(long bytes, int files)
    {
        if (bytes >= 1L << 30)
            Console.Error.WriteLine($"pathmemo: hashing will read {SizeFormat.Bytes(bytes)} from "
                                  + $"{files.ToString("N0", CultureInfo.InvariantCulture)} files "
                                  + $"(README section 8.5)");

        return DupeConsent.Continue;
    }

    private static void Tick(DupeProgress progress)
    {
        var shown = PathDisplay.Shorten(progress.Path, 46);

        Console.Error.Write(string.Create(CultureInfo.InvariantCulture,
            $"\r  {progress.Stage,-9} {progress.Done,7:N0}/{progress.Total,-7:N0} "
            + $"{SizeFormat.Bytes(progress.BytesRead),9}  {shown,-48}"));
    }

    /// <summary>
    /// Hands the extra copies to <c>rm</c>, once every one of them has been proved to be a
    /// copy all over again (README section 8.4).
    /// </summary>
    private static int Act(DupeReport report, DupesOptions options, CancellationToken ct)
    {
        var groups = report.Duplicates.ToList();
        var victims = groups.SelectMany(g => g.Victims).Select(f => f.Path).ToList();

        if (victims.Count == 0)
        {
            Console.WriteLine();
            Console.WriteLine($"Nothing to delete: no duplicate group in scan {report.ScanId} has a spare copy.");
            return ExitCode.NoData;
        }

        var marked = victims.ToHashSet(StringComparer.OrdinalIgnoreCase);

        // The invariant, asserted rather than assumed: the list above is built by excluding
        // the keeper, and this is what catches the day that stops being true.
        if (!Keeper.Survives(groups, marked, out var complaint))
        {
            Console.Error.WriteLine($"pathmemo: {complaint}");
            return ExitCode.Unsafe;
        }

        var progress = Console.IsErrorRedirected
            ? null
            : new Progress<string>(path => Console.Error.Write($"\r  re-checking {PathDisplay.Shorten(path, 56),-58}"));

        var query = new DupeQuery { Algorithm = report.Algorithm };

        if (!DuplicateFinder.Reverify(groups, marked, query, progress, out var mismatch))
        {
            if (progress is not null) Console.Error.Write("\r" + new string(' ', 78) + "\r");

            Console.Error.WriteLine($"pathmemo: {mismatch}");
            Console.Error.WriteLine("pathmemo: nothing was deleted - the whole operation is cancelled "
                                  + "(README section 8.4)");
            return ExitCode.Unsafe;
        }

        if (progress is not null) Console.Error.Write("\r" + new string(' ', 78) + "\r");

        var copies = victims.Count.ToString("N0", CultureInfo.InvariantCulture);
        var count = groups.Count(g => g.Victims.Any());
        var touched = count.ToString("N0", CultureInfo.InvariantCulture);

        Console.WriteLine();
        Console.WriteLine($"  {copies} extra cop{(victims.Count == 1 ? "y" : "ies")} in "
                        + $"{touched} group{(count == 1 ? "" : "s")}, re-checked against their survivors.");

        return RmCommand.Run(new RmOptions
        {
            Paths = victims,
            Mode = options.Mode,
            DryRun = options.DryRun,
            Yes = options.Yes,
            Force = options.Force,
            ScanId = report.ScanId > 0 ? report.ScanId : null,
            Reason = $"dupes: {groups.Count} groups, {HashKinds.Name(report.Algorithm)}",
        }, ct, DeleteSource.Dupes);
    }
}
