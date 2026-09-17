using System.Globalization;
using System.Text.Json;
using PathMemo.Cli.Output;
using PathMemo.Config;
using PathMemo.Scanning;
using PathMemo.Storage;

namespace PathMemo.Cli.Commands;

/// <summary>
/// <c>pathmemo history</c>: the stored scans (README sections 10, 11).
/// </summary>
/// <remarks>
/// Reads the <c>scans</c> table, not the snapshots directory. The rows outlive the trees
/// they came from: a scan whose snapshot retention deleted is still a real data point for
/// "how full was this disk in March", and it is listed as such rather than vanishing.
/// </remarks>
internal static class HistoryCommand
{
    internal static int Run(HistoryOptions options)
    {
        using var catalog = ScanCatalog.TryOpen(out var error);
        if (catalog is null)
        {
            Console.Error.WriteLine($"pathmemo: cannot read the history database - {error}");
            return ExitCode.Locked;
        }

        // The snapshots directory is the fact and the table is the index, so reconcile
        // before reading: a snapshot copied in by hand shows up, one deleted by hand
        // stops pretending to be openable.
        catalog.Reconcile();

        var rows = catalog.Scans.List(options.Limit, options.Since);
        if (rows.Count == 0)
        {
            if (options.Json)
            {
                WriteJson(catalog, [], options);
                return ExitCode.Ok;
            }

            Console.Error.WriteLine(catalog.Scans.Count() > 0
                ? "pathmemo: no scans match that filter"
                : "pathmemo: no scans yet - run 'pathmemo scan' first");
            return ExitCode.NoData;
        }

        if (options.Json)
        {
            WriteJson(catalog, rows, options);
            return ExitCode.Ok;
        }

        var w = Console.Out;
        w.WriteLine();
        w.WriteLine("    ID  STARTED (UTC)      SCANNER    STATUS         FILES       SIZE   SNAPSHOT  FLAGS");
        w.WriteLine("  " + new string('-', 94));

        long snapshotBytes = 0;

        foreach (var row in rows)
        {
            if (row.SnapshotBytes is { } bytes) snapshotBytes += bytes;

            w.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "  {0,4}  {1:yyyy-MM-dd HH:mm}   {2,-9}  {3,-10} {4,10}  {5,9}  {6,9}  {7}",
                row.Id,
                row.StartedUtc,
                ScanRepository.Name(row.Scanner),
                row.Status,
                row.TotalFiles.ToString("N0", CultureInfo.InvariantCulture),
                SizeFormat.Bytes(row.AllocatedBytes),
                Snapshot(row),
                Describe(row.Flags)));

            if (row.Note is { Length: > 0 } note && note != "imported from the snapshot file")
                w.WriteLine($"        note: {note}");
        }

        w.WriteLine("  " + new string('-', 94));

        var database = new FileInfo(AppPaths.DatabasePath);
        w.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "  {0} listed  ·  {1} of snapshots  ·  database {2}",
            rows.Count == 1 ? "1 scan" : rows.Count.ToString("N0", CultureInfo.InvariantCulture) + " scans",
            SizeFormat.Bytes(snapshotBytes),
            database.Exists ? SizeFormat.Bytes(database.Length) : "absent"));

        var withSnapshot = rows.Count(r => r.SnapshotAvailable);
        if (withSnapshot >= 2)
        {
            var newest = rows.First(r => r.SnapshotAvailable).Id;
            var previous = rows.First(r => r.SnapshotAvailable && r.Id != newest).Id;
            w.WriteLine($"  Compare two of them:  pathmemo diff {previous} {newest}");
        }

        return ExitCode.Ok;
    }

    /// <summary>
    /// The snapshot column. "deleted" is reserved for a tree that existed and was removed
    /// by retention; a scan whose process died never had one to lose.
    /// </summary>
    private static string Snapshot(ScanRow row) =>
        row.SnapshotBytes is { } size ? SizeFormat.Bytes(size)
        : row.Status is ScanStatus.Running or ScanStatus.Failed ? "none"
        : "deleted";

    private static string Describe(ScanFlags flags)
    {
        var parts = new List<string>(5);
        if ((flags & ScanFlags.Partial) != 0) parts.Add("partial");
        if ((flags & ScanFlags.Degraded) != 0) parts.Add("degraded");
        if ((flags & ScanFlags.Elevated) != 0) parts.Add("elevated");
        if ((flags & ScanFlags.Incremental) != 0) parts.Add("incremental");
        if ((flags & ScanFlags.PartialHardlinkResolution) != 0) parts.Add("links>=1MB");
        if ((flags & ScanFlags.NoAdsAccounting) != 0) parts.Add("no-ads");
        return string.Join(' ', parts);
    }

    private static void WriteJson(ScanCatalog catalog, IReadOnlyList<ScanRow> rows, HistoryOptions options)
    {
        using var stream = Console.OpenStandardOutput();
        using var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });

        json.WriteStartObject();
        json.WriteNumber("schemaVersion", 1);
        json.WriteStartArray("scans");

        foreach (var row in rows)
        {
            json.WriteStartObject();
            json.WriteNumber("id", row.Id);
            json.WriteString("startedUtc", Iso.Of(row.StartedUtc));
            if (row.FinishedUtc is { } finished) json.WriteString("finishedUtc", Iso.Of(finished));
            else json.WriteNull("finishedUtc");
            json.WriteString("status", row.Status);
            json.WriteString("scanner", ScanRepository.Name(row.Scanner));
            json.WriteNumber("flags", (long)row.Flags);
            json.WriteBoolean("elevated", (row.Flags & ScanFlags.Elevated) != 0);
            json.WriteNumber("totalFiles", row.TotalFiles);
            json.WriteNumber("totalDirectories", row.TotalDirectories);
            json.WriteNumber("allocatedBytes", row.AllocatedBytes);
            json.WriteNumber("logicalBytes", row.LogicalBytes);
            if (row.DurationMs is { } duration) json.WriteNumber("durationMs", duration);
            json.WriteNumber("errorCount", row.ErrorCount);
            json.WriteString("toolVersion", row.ToolVersion);
            json.WriteBoolean("snapshotAvailable", row.SnapshotAvailable);
            if (row.SnapshotBytes is { } snapshot) json.WriteNumber("snapshotBytes", snapshot);
            if (row.Note is { } note) json.WriteString("note", note); else json.WriteNull("note");

            json.WriteStartArray("roots");
            foreach (var root in row.Roots) json.WriteStringValue(root);
            json.WriteEndArray();

            json.WriteStartArray("volumes");
            foreach (var volume in catalog.Scans.Volumes(row.Id))
            {
                json.WriteStartObject();
                json.WriteString("volume", volume.Letter);
                if (volume.Label is { } label) json.WriteString("label", label);
                if (volume.FileSystem is { } fs) json.WriteString("filesystem", fs);
                json.WriteNumber("totalBytes", volume.TotalBytes);
                json.WriteNumber("freeBytes", volume.FreeBytes);
                json.WriteNumber("scannedBytes", volume.ScannedBytes);
                if (volume.UnaccountedBytes is { } gap) json.WriteNumber("unaccountedBytes", gap);
                else json.WriteNull("unaccountedBytes");
                json.WriteEndObject();
            }
            json.WriteEndArray();

            if (options.Categories)
            {
                json.WriteStartArray("categories");
                foreach (var category in catalog.Scans.Categories(row.Id))
                {
                    json.WriteStartObject();
                    json.WriteString("category", Analysis.FileCategories.Name(category.Category));
                    json.WriteNumber("allocatedBytes", category.AllocatedBytes);
                    json.WriteNumber("fileCount", category.FileCount);
                    json.WriteEndObject();
                }
                json.WriteEndArray();
            }

            json.WriteEndObject();
        }

        json.WriteEndArray();
        json.WriteEndObject();
        json.Flush();
        stream.Write("\n"u8);
    }
}

internal sealed record HistoryOptions
{
    internal int Limit { get; init; } = 50;
    internal DateTime? Since { get; init; }
    internal bool Json { get; init; }

    /// <summary>Include the per-category totals in the JSON output.</summary>
    internal bool Categories { get; init; }
}
