using PathMemo.Cli.Output;
using PathMemo.Deletion;
using PathMemo.Storage;

namespace PathMemo.Cli.Commands;

internal sealed record PurgeOptions
{
    internal long? OpId { get; init; }
    internal bool Expired { get; init; }
    internal bool All { get; init; }
    internal bool Bin { get; init; }
    internal bool Yes { get; init; }
}

/// <summary>
/// <c>restore</c>, <c>purge</c> and <c>ops</c>: the other half of quarantine
/// (README sections 9.4, 9.7).
/// </summary>
/// <remarks>
/// Quarantine is only honest if undo and commit are both one command away. The manifest on
/// disk is what both read; the database row is an index, so a restore still works after the
/// database has been deleted (README section 11.1).
/// </remarks>
internal static class QuarantineCommands
{
    internal static int Restore(long opId)
    {
        var manifest = Quarantine.ReadManifest(opId, out var error);
        if (manifest is null)
        {
            Console.Error.WriteLine($"pathmemo: {error}");
            return ExitCode.NoData;
        }

        var (restored, failures) = DeleteEngine.Restore(manifest);

        Console.WriteLine();
        Console.WriteLine($"Restored {restored} of {manifest.Items.Count} items from op-{opId:D6}.");

        foreach (var (path, why) in failures.Take(20))
            Console.WriteLine($"  ! {PathDisplay.Shorten(path, 52)}  -  {why}");

        using var catalog = ScanCatalog.TryOpen(out var databaseError);
        if (catalog is not null)
        {
            new DeleteRepository(catalog.Database)
                .SetStatus(opId, failures.Count == 0 ? "restored" : "partial");
        }
        else if (databaseError is not null)
        {
            Console.Error.WriteLine($"pathmemo: the journal was not updated - {databaseError}");
        }

        if (failures.Count == 0 && restored == manifest.Items.Count)
        {
            // The store is empty now; the manifest goes with it, so the operation stops
            // offering a restore that would find nothing.
            foreach (var store in Quarantine.StoresOf(manifest)) TryRemove(store);
            return ExitCode.Ok;
        }

        return restored == 0 ? ExitCode.Failure : ExitCode.Partial;
    }

    internal static int Purge(PurgeOptions options, CancellationToken ct)
    {
        if (options.Bin) return EmptyBin(options);

        var engine = DeleteEngine.Create();

        using var catalog = ScanCatalog.TryOpen(out var error);
        if (catalog is null)
        {
            Console.Error.WriteLine($"pathmemo: refusing to purge without the operations journal - {error}");
            return ExitCode.Locked;
        }

        var journal = new DeleteRepository(catalog.Database);

        var manifests = Select(options, journal);
        if (manifests.Count == 0)
        {
            Console.WriteLine(options.Expired
                ? "No quarantine has reached its retention limit."
                : "Nothing in quarantine.");
            return ExitCode.Ok;
        }

        var total = manifests.Sum(m => m.Bytes);

        Console.WriteLine();
        Console.WriteLine($"Purge {manifests.Count} quarantine{(manifests.Count == 1 ? "" : "s")} · "
                        + $"{SizeFormat.Bytes(total)} freed for real, with no way back.");

        foreach (var manifest in manifests)
            Console.WriteLine($"  {Quarantine.Describe(manifest)}   created {manifest.CreatedUtc:yyyy-MM-dd}");

        if (!options.Yes && !Confirm()) return ExitCode.Cancelled;

        long freed = 0;
        var failed = 0;

        foreach (var manifest in manifests)
        {
            var (bytes, failures) = engine.Purge(manifest, ct);
            freed += bytes;
            failed += failures.Count;

            journal.SetStatus(manifest.OpId, failures.Count == 0 ? "purged" : "partial", bytes);

            foreach (var (path, why) in failures.Take(10))
                Console.WriteLine($"  ! {PathDisplay.Shorten(path, 52)}  -  {why}");
        }

        Console.WriteLine();
        Console.WriteLine($"  Predicted:  {SizeFormat.Bytes(total),-10}  Actual free space change:  {SizeFormat.Bytes(freed)}");

        return failed == 0 ? ExitCode.Ok : ExitCode.Partial;
    }

    private static List<QuarantineManifest> Select(PurgeOptions options, DeleteRepository journal)
    {
        if (options.OpId is { } id)
        {
            var one = Quarantine.ReadManifest(id, out var error);
            if (one is null)
            {
                Console.Error.WriteLine($"pathmemo: {error}");
                return [];
            }

            return [one];
        }

        if (options.Expired)
        {
            var due = journal.Expired(DateTime.UtcNow).Select(row => row.Id).ToHashSet();
            return [.. Quarantine.List().Where(m => due.Contains(m.OpId)
                || (m.PurgeAfterUtc is { } when_ && when_ <= DateTime.UtcNow))];
        }

        return [.. Quarantine.List()];
    }

    private static bool Confirm()
    {
        if (Console.IsInputRedirected || Console.IsOutputRedirected)
        {
            Console.Error.WriteLine("pathmemo: purge needs --yes when it cannot ask");
            return false;
        }

        Console.Write("  Proceed? [y/N] ");
        if ((Console.ReadLine() ?? "").Trim().ToLowerInvariant() is "y" or "yes") return true;

        Console.WriteLine("  cancelled");
        return false;
    }

    /// <summary>
    /// Empties the Recycle Bin, the one operation allowed inside it (README section 9.3).
    /// Not journalled per item: the shell does not report them, and a journal entry that
    /// says "some unknown number of files" would be worse than none.
    /// </summary>
    private static int EmptyBin(PurgeOptions options)
    {
        var volumes = Platform.VolumeInfo.Enumerate(fixedOnly: true);
        var states = volumes.ToDictionary(v => v.Root, v => RecycleBin.Query(v.Root), StringComparer.OrdinalIgnoreCase);
        var total = states.Values.Sum(s => s.UsedBytes);

        Console.WriteLine();
        Console.WriteLine($"Empty the Recycle Bin on {states.Count} volume{(states.Count == 1 ? "" : "s")} · "
                        + $"{SizeFormat.Bytes(total)}");

        foreach (var (root, state) in states.Where(s => s.Value.UsedBytes > 0))
            Console.WriteLine($"  {root}  {SizeFormat.Bytes(state.UsedBytes),9}  {state.ItemCount} items");

        if (total == 0)
        {
            Console.WriteLine("  Already empty.");
            return ExitCode.Ok;
        }

        if (!options.Yes && !Confirm()) return ExitCode.Cancelled;

        var failures = 0;
        foreach (var (root, _) in states)
        {
            var error = RecycleBin.Empty(root);
            if (error is null) continue;

            Console.Error.WriteLine($"pathmemo: {root} - {error}");
            failures++;
        }

        return failures == 0 ? ExitCode.Ok : ExitCode.Partial;
    }

    internal static int Ops(long? opId, int limit)
    {
        using var catalog = ScanCatalog.TryOpen(out var error);
        if (catalog is null)
        {
            Console.Error.WriteLine($"pathmemo: {error}");
            return ExitCode.Locked;
        }

        var journal = new DeleteRepository(catalog.Database);

        if (opId is { } id)
        {
            var row = journal.Find(id);
            if (row is null)
            {
                Console.Error.WriteLine($"pathmemo: no operation with id {id}");
                return ExitCode.NoData;
            }

            DeleteReport.PrintOpDetail(row, journal.Items(id), Console.Out);
            return ExitCode.Ok;
        }

        DeleteReport.PrintOps(journal.List(limit), Console.Out);

        var held = Quarantine.List();
        if (held.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"Quarantine: {SizeFormat.Bytes(held.Sum(m => m.Bytes))} in "
                            + $"{held.Count} operation{(held.Count == 1 ? "" : "s")} - purge to reclaim.");
        }

        return ExitCode.Ok;
    }

    private static void TryRemove(string directory)
    {
        try
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The quarantine is empty either way; a leftover directory is not worth a failure.
        }
    }
}
