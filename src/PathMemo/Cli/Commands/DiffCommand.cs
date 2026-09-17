using System.Globalization;
using System.Text.Json;
using PathMemo.Analysis;
using PathMemo.Cli.Output;
using PathMemo.Scanning;
using PathMemo.Snapshots;
using PathMemo.Storage;

namespace PathMemo.Cli.Commands;

/// <summary>
/// <c>pathmemo diff &lt;a&gt; &lt;b&gt;</c>: what changed between two scans (README section 10.2).
/// </summary>
/// <remarks>
/// The point of keeping history at all. "The disk is full" is a state; "Docker grew by
/// 9.8 GB since Monday" is an answer.
/// </remarks>
internal static class DiffCommand
{
    internal static int Run(DiffOptions options)
    {
        using var catalog = ScanCatalog.TryOpen(out var error);
        if (catalog is not null) catalog.Reconcile();
        else if (error is not null) Console.Error.WriteLine($"pathmemo: history unavailable - {error}");

        if (!Resolve(catalog, options, out var beforeId, out var afterId)) return ExitCode.NoData;

        var clock = System.Diagnostics.Stopwatch.StartNew();

        if (!TryLoad(catalog, beforeId, out var before)) return ExitCode.NoData;
        if (!TryLoad(catalog, afterId, out var after)) return ExitCode.NoData;

        var loaded = clock.Elapsed;

        // Chronological order regardless of the order on the command line: a diff that
        // silently reports growth as shrinkage is worse than no diff.
        if (before!.StartedUtc > after!.StartedUtc)
        {
            (before, after) = (after, before);
            (beforeId, afterId) = (afterId, beforeId);
        }

        var diff = SnapshotDiff.Compare(beforeId, before, afterId, after, options.SizeMode,
            options.MinBytes > 0 ? options.MinBytes : SnapshotDiff.MinimumInteresting);

        if (Environment.GetEnvironmentVariable("PATHMEMO_DIAG") == "1")
        {
            Console.Error.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "  diag: loaded two snapshots ({0:N0} + {1:N0} nodes) in {2:F0} ms, compared in {3:F0} ms",
                before.Tree.Count, after.Tree.Count,
                loaded.TotalMilliseconds, (clock.Elapsed - loaded).TotalMilliseconds));
        }

        if (options.Json)
        {
            WriteJson(diff, options);
            return ExitCode.Ok;
        }

        Print(diff, before, after, options, Console.Out);
        return ExitCode.Ok;
    }

    /// <summary>
    /// Fills in what the user left out: no ids means the two newest scans, one id means
    /// that scan against the newest.
    /// </summary>
    private static bool Resolve(ScanCatalog? catalog, DiffOptions options, out long beforeId, out long afterId)
    {
        beforeId = options.BeforeId ?? 0;
        afterId = options.AfterId ?? 0;

        if (options.BeforeId is not null && options.AfterId is not null) return true;

        var available = catalog is not null
            ? catalog.Scans.List(limit: 200).Where(r => r.SnapshotAvailable).Select(r => r.Id).ToList()
            : SnapshotStore.List().Select(s => s.Id).ToList();

        var newest = options.AfterId ?? available.FirstOrDefault();
        afterId = newest;
        if (options.BeforeId is null) beforeId = available.FirstOrDefault(id => id != newest);

        if (beforeId == 0 || afterId == 0 || beforeId == afterId)
        {
            Console.Error.WriteLine("pathmemo: diff needs two stored scans - run 'pathmemo history' to see what there is");
            return false;
        }

        return true;
    }

    private static bool TryLoad(ScanCatalog? catalog, long id, out SnapshotContents? contents)
    {
        contents = null;

        var row = catalog?.Scans.Find(id);
        if (row is not null && !row.SnapshotAvailable)
        {
            Console.Error.WriteLine($"pathmemo: scan {id} no longer has a snapshot - retention keeps its totals, not its tree");
            return false;
        }

        var path = SnapshotStore.PathFor(id);
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"pathmemo: no snapshot for scan {id}");
            return false;
        }

        try
        {
            contents = SnapshotFile.Read(path);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            Console.Error.WriteLine($"pathmemo: cannot read the snapshot of scan {id}: {ex.Message}");
            return false;
        }

        // README section 10.2: a cancelled scan has no business being a baseline. Its
        // totals are missing whole subtrees, which a diff would report as deletions.
        if ((contents.Flags & ScanFlags.Partial) != 0)
        {
            Console.Error.WriteLine($"pathmemo: scan {id} was cancelled, so its totals are incomplete and cannot be diffed");
            contents = null;
            return false;
        }

        return true;
    }

    internal static void Print(SnapshotDiffResult diff, SnapshotContents before, SnapshotContents after,
                               DiffOptions options, TextWriter w)
    {
        var width = ConsoleWidth();

        w.WriteLine();
        w.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "scan {0} -> {1}   ·   {2:yyyy-MM-dd HH:mm} -> {3:yyyy-MM-dd HH:mm} UTC",
            diff.BeforeId, diff.AfterId, diff.BeforeUtc, diff.AfterUtc));

        foreach (var warning in Incomparability(diff, before, after))
        {
            w.WriteLine();
            w.WriteLine("  ! " + warning);
        }

        foreach (var note in diff.Notes) w.WriteLine("  ! " + note);

        foreach (var volume in diff.Volumes)
        {
            w.WriteLine();
            w.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "{0}   {1}   ·   {2} -> {3}   ·   free {4} -> {5}",
                volume.Letter, Signed(volume.Delta),
                SizeFormat.Bytes(volume.Before), SizeFormat.Bytes(volume.After),
                SizeFormat.Bytes(volume.FreeBefore), SizeFormat.Bytes(volume.FreeAfter)));

            Section(w, "GREW", volume.Grew, volume.GrewBytes, options.Limit, width);
            Section(w, "SHRANK", volume.Shrank, volume.ShrankBytes, options.Limit, width);

            // GREW plus SHRANK plus this is the volume's change, exactly. Without the
            // line the reader would add the two columns, get a different number, and be
            // right to stop trusting the tool (README principle P1).
            if (Math.Abs(volume.Unexplained) >= 1L << 20)
            {
                w.WriteLine();
                w.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "  UNEXPLAINED    {0,12}   spread over changes below the threshold",
                    Signed(volume.Unexplained)));
            }

            w.WriteLine();
            w.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "  APPEARED       {0,12}   ({1})", Signed(volume.Appeared.Bytes), Paths(volume.Appeared.Paths)));
            w.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "  DISAPPEARED    {0,12}   ({1})", Signed(-volume.Disappeared.Bytes), Paths(volume.Disappeared.Paths)));

            // Say what the counts cover. They are collected where the report descended,
            // so a new small file in an otherwise quiet directory is not in them - and a
            // number whose scope is unstated is the thing that makes a tool untrusted.
            if (volume.Appeared.Paths > 0 || volume.Disappeared.Paths > 0)
                w.WriteLine("    counted where the change was above the threshold, not across the whole volume");

            if (volume.Appeared.LargestFile is { } newest)
            {
                w.WriteLine();
                w.WriteLine("  Largest single new file");
                w.WriteLine($"    {SizeFormat.Bytes(volume.Appeared.LargestFileBytes),9}  " +
                            PathDisplay.Shorten(newest, Math.Max(40, width - 16)));
            }

            if (volume.Grew.Count == 0 && volume.Shrank.Count == 0)
            {
                w.WriteLine();
                w.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "  Nothing moved by more than {0} on this volume.",
                    SizeFormat.Bytes(Math.Max(options.MinBytes > 0 ? options.MinBytes : SnapshotDiff.MinimumInteresting, 0))));
            }
        }

        if (diff.Volumes.Count == 0)
        {
            w.WriteLine();
            w.WriteLine("  The two scans have no volume in common.");
        }

        w.WriteLine();
    }

    private static void Section(TextWriter w, string title, IReadOnlyList<DiffRow> rows, long total, int limit, int width)
    {
        w.WriteLine();
        w.WriteLine(string.Format(CultureInfo.InvariantCulture, "  {0,-11}    {1,12}", title, Signed(total)));

        if (rows.Count == 0)
        {
            w.WriteLine("    (nothing above the threshold)");
            return;
        }

        var pathWidth = Math.Max(30, width - 42);

        foreach (var row in rows.Take(limit))
        {
            var detail = row switch
            {
                { Kind: DiffRowKind.Residual } => "other entries here",
                { Before: null } => "new",
                { After: null } => "gone",
                _ => $"{SizeFormat.Bytes(row.Before!.Value)} -> {SizeFormat.Bytes(row.After!.Value)}",
            };

            w.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "    {0,-" + pathWidth.ToString(CultureInfo.InvariantCulture) + "}  {1,10}   {2}",
                PathDisplay.Shorten(row.Path, pathWidth), Signed(row.Delta), detail));
        }

        if (rows.Count > limit)
            w.WriteLine($"    ... {rows.Count - limit} more");
    }

    /// <summary>
    /// Reasons the two scans are not directly comparable. A walk scan against an MFT scan
    /// differs by what the scanners can see, which would otherwise read as real change
    /// (README sections 3.2, 4.1).
    /// </summary>
    internal static IReadOnlyList<string> Incomparability(SnapshotDiffResult diff, SnapshotContents before, SnapshotContents after)
    {
        var warnings = new List<string>();

        if (before.Scanner != after.Scanner)
        {
            warnings.Add($"scan {diff.BeforeId} used the {Name(before.Scanner)} scanner and scan {diff.AfterId} the "
                       + $"{Name(after.Scanner)} one: part of what follows is a difference in what the scanners see, not on disk");
        }

        if ((before.Flags & ScanFlags.Elevated) != (after.Flags & ScanFlags.Elevated))
            warnings.Add("one of the two scans ran without administrator rights, so it could not read every directory");

        if ((before.Flags & ScanFlags.PartialHardlinkResolution) != (after.Flags & ScanFlags.PartialHardlinkResolution))
            warnings.Add("hard links were resolved differently in the two scans; the component store will look changed when it is not");

        if ((before.Flags & ScanFlags.NoAdsAccounting) != (after.Flags & ScanFlags.NoAdsAccounting))
            warnings.Add("one of the two scans did not count alternate data streams");

        return warnings;
    }

    private static string Name(ScannerKind kind) => kind switch
    {
        ScannerKind.Mft => "MFT",
        ScannerKind.Incremental => "incremental",
        _ => "walk",
    };

    private static string Paths(int count) =>
        count == 1 ? "1 path" : count.ToString("N0", CultureInfo.InvariantCulture) + " paths";

    private static string Signed(long bytes) =>
        (bytes >= 0 ? "+" : "-") + SizeFormat.Bytes(Math.Abs(bytes));

    private static void WriteJson(SnapshotDiffResult diff, DiffOptions options)
    {
        using var stream = Console.OpenStandardOutput();
        using var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });

        json.WriteStartObject();
        json.WriteNumber("schemaVersion", 1);
        json.WriteNumber("beforeScanId", diff.BeforeId);
        json.WriteNumber("afterScanId", diff.AfterId);
        json.WriteString("beforeStartedUtc", Iso.Of(diff.BeforeUtc));
        json.WriteString("afterStartedUtc", Iso.Of(diff.AfterUtc));
        json.WriteString("sizeMode", diff.Volumes.Count == 0 ? "unique" : options.SizeMode.ToString().ToLowerInvariant());

        json.WriteStartArray("notes");
        foreach (var note in diff.Notes) json.WriteStringValue(note);
        json.WriteEndArray();

        json.WriteStartArray("volumes");
        foreach (var volume in diff.Volumes)
        {
            json.WriteStartObject();
            json.WriteString("volume", volume.Letter);
            json.WriteNumber("beforeBytes", volume.Before);
            json.WriteNumber("afterBytes", volume.After);
            json.WriteNumber("deltaBytes", volume.Delta);
            json.WriteNumber("freeBeforeBytes", volume.FreeBefore);
            json.WriteNumber("freeAfterBytes", volume.FreeAfter);
            json.WriteNumber("grewBytes", volume.GrewBytes);
            json.WriteNumber("shrankBytes", volume.ShrankBytes);
            json.WriteNumber("unexplainedBytes", volume.Unexplained);

            WriteRows(json, "grew", volume.Grew, options.Limit);
            WriteRows(json, "shrank", volume.Shrank, options.Limit);

            WriteSummary(json, "appeared", volume.Appeared, positive: true);
            WriteSummary(json, "disappeared", volume.Disappeared, positive: false);

            json.WriteEndObject();
        }
        json.WriteEndArray();

        json.WriteEndObject();
        json.Flush();
        stream.Write("\n"u8);
    }

    private static void WriteRows(Utf8JsonWriter json, string name, IReadOnlyList<DiffRow> rows, int limit)
    {
        json.WriteStartArray(name);
        foreach (var row in rows.Take(limit))
        {
            json.WriteStartObject();
            json.WriteString("path", row.Path);
            json.WriteString("kind", row.Kind.ToString().ToLowerInvariant());
            if (row.Before is { } b) json.WriteNumber("beforeBytes", b); else json.WriteNull("beforeBytes");
            if (row.After is { } a) json.WriteNumber("afterBytes", a); else json.WriteNull("afterBytes");
            json.WriteNumber("deltaBytes", row.Delta);
            json.WriteEndObject();
        }
        json.WriteEndArray();
    }

    private static void WriteSummary(Utf8JsonWriter json, string name, ChangeSummary summary, bool positive)
    {
        json.WriteStartObject(name);
        json.WriteNumber("paths", summary.Paths);
        json.WriteNumber("deltaBytes", positive ? summary.Bytes : -summary.Bytes);
        if (summary.LargestFile is { } largest)
        {
            json.WriteString("largestFile", largest);
            json.WriteNumber("largestFileBytes", summary.LargestFileBytes);
        }
        else
        {
            json.WriteNull("largestFile");
        }
        json.WriteEndObject();
    }

    private static int ConsoleWidth()
    {
        try { return Console.IsOutputRedirected ? 120 : Console.WindowWidth; }
        catch (IOException) { return 120; }
    }
}

internal sealed record DiffOptions
{
    internal long? BeforeId { get; init; }
    internal long? AfterId { get; init; }

    /// <summary>Rows shown per section.</summary>
    internal int Limit { get; init; } = 15;

    /// <summary>Overrides the 64 MB floor of the interest threshold.</summary>
    internal long MinBytes { get; init; }

    internal SizeMode SizeMode { get; init; } = SizeMode.Unique;

    internal bool Json { get; init; }
}
