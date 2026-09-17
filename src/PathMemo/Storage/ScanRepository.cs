using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PathMemo.Analysis;
using PathMemo.Config;
using PathMemo.Platform;
using PathMemo.Scanning;

namespace PathMemo.Storage;

/// <summary>ISO-8601 UTC with a Z, the only time format in the database (README section 13.6).</summary>
internal static class Iso
{
    private const string Format = "yyyy-MM-dd'T'HH:mm:ss'Z'";

    internal static string Of(DateTime utc) => utc.ToString(Format, CultureInfo.InvariantCulture);

    internal static DateTime? Parse(string? text) =>
        DateTime.TryParse(text, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var value)
            ? value
            : null;
}

/// <summary>
/// Reads and writes the scan history (README sections 10, 11).
/// </summary>
/// <remarks>
/// The scan id and the snapshot file number are the same number by construction: the id
/// is allocated before the scan starts and the snapshot is written under it. Two
/// independent numbering schemes would mean <c>history</c> and <c>tree --scan</c>
/// disagreeing about what "scan 42" is.
/// </remarks>
internal sealed class ScanRepository(Database database)
{
    private readonly Database _db = database;

    /// <summary>
    /// Opens a row for a scan that is about to start and returns its id, which is also
    /// the number its snapshot will be written under.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The id comes from <c>AUTOINCREMENT</c> rather than from "highest number seen plus
    /// one", so two pathmemos starting at the same moment cannot pick the same number and
    /// overwrite each other's snapshot. <c>AUTOINCREMENT</c> also stays above ids inserted
    /// explicitly by <see cref="Import"/>, which is what keeps imported snapshots safe.
    /// </para>
    /// <para>
    /// A process that dies mid-scan leaves the row as <c>running</c>, which is the honest
    /// record of what happened.
    /// </para>
    /// </remarks>
    internal long BeginNew(DateTime startedUtc, ScannerKind planned,
                           IReadOnlyList<string> roots, string? note)
    {
        using var command = _db.Command("""
            INSERT INTO scans (started_at, status, scanner, flags, roots, tool_version, note)
            VALUES ($started, $status, $scanner, 0, $roots, $version, $note);
            SELECT last_insert_rowid();
            """);

        command.Parameters.AddWithValue("$started", Iso.Of(startedUtc));
        command.Parameters.AddWithValue("$status", ScanStatus.Running);
        command.Parameters.AddWithValue("$scanner", Name(planned));
        command.Parameters.AddWithValue("$roots", EncodeRoots(roots));
        command.Parameters.AddWithValue("$version", AppInfo.Version);
        command.Parameters.AddWithValue("$note", note ?? (object)DBNull.Value);

        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>Fills in the result of a finished scan, its volumes and its aggregates.</summary>
    internal void Complete(long id, ScanResult result, string status,
                           string? snapshotPath, long? snapshotBytes,
                           ScanAggregateSet? aggregates = null)
    {
        using var transaction = _db.Begin();

        using (var command = _db.Command("""
            UPDATE scans SET
                finished_at = $finished, status = $status, scanner = $scanner, flags = $flags,
                total_files = $files, total_dirs = $dirs,
                allocated_bytes = $allocated, logical_bytes = $logical,
                duration_ms = $duration, error_count = $errors,
                snapshot_path = $snapshotPath, snapshot_bytes = $snapshotBytes
            WHERE id = $id
            """))
        {
            command.Transaction = transaction;
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$finished", Iso.Of(result.StartedUtc + result.Duration));
            command.Parameters.AddWithValue("$status", status);
            command.Parameters.AddWithValue("$scanner", Name(result.Scanner));
            command.Parameters.AddWithValue("$flags", (long)result.Flags);
            command.Parameters.AddWithValue("$files", result.FileCount);
            command.Parameters.AddWithValue("$dirs", result.DirectoryCount);
            command.Parameters.AddWithValue("$allocated", result.AllocatedBytes);
            command.Parameters.AddWithValue("$logical", result.LogicalBytes);
            command.Parameters.AddWithValue("$duration", (long)result.Duration.TotalMilliseconds);
            command.Parameters.AddWithValue("$errors", result.Errors.Count);
            command.Parameters.AddWithValue("$snapshotPath", snapshotPath ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("$snapshotBytes", snapshotBytes ?? (object)DBNull.Value);
            command.ExecuteNonQuery();
        }

        WriteVolumes(transaction, id, result);
        WriteAggregates(transaction, id, aggregates ?? ScanAggregates.Compute(result.Tree));

        transaction.Commit();
    }

    internal void Fail(long id, string status, string? note = null)
    {
        using var command = _db.Command("""
            UPDATE scans SET status = $status, finished_at = $finished,
                             note = COALESCE($note, note)
            WHERE id = $id
            """);

        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$finished", Iso.Of(DateTime.UtcNow));
        command.Parameters.AddWithValue("$note", note ?? (object)DBNull.Value);
        command.ExecuteNonQuery();
    }

    private void WriteVolumes(SqliteTransaction transaction, long id, ScanResult result)
    {
        using (var clear = _db.Command("DELETE FROM scan_volumes WHERE scan_id = $id"))
        {
            clear.Transaction = transaction;
            clear.Parameters.AddWithValue("$id", id);
            clear.ExecuteNonQuery();
        }

        using var command = _db.Command("""
            INSERT INTO scan_volumes (scan_id, letter, label, filesystem, volume_serial, volume_guid,
                                      cluster_bytes, total_bytes, free_bytes, scanned_bytes,
                                      unaccounted_bytes)
            VALUES ($id, $letter, $label, $fs, $serial, $guid, $cluster, $total, $free, $scanned,
                    $unaccounted)
            ON CONFLICT(scan_id, letter) DO NOTHING
            """);
        command.Transaction = transaction;

        var tree = result.Tree;
        for (var v = 0; v < result.Volumes.Count; v++)
        {
            var volume = result.Volumes[v];
            var scanned = v < tree.Roots.Length ? tree.Allocated[tree.Roots[v]] : 0;

            // Unaccounted space only means anything for a whole-volume scan: a subtree
            // has nothing to reconcile against (README section 3.5).
            var wholeVolume = volume.Root.TrimEnd(Path.DirectorySeparatorChar).Length <= 2;
            var unaccounted = wholeVolume ? (long)volume.UsedBytes - scanned : (long?)null;

            command.Parameters.Clear();
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$letter", volume.Letter);
            command.Parameters.AddWithValue("$label", volume.Label ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("$fs", volume.FileSystem);
            command.Parameters.AddWithValue("$serial", volume.Serial);
            command.Parameters.AddWithValue("$guid", volume.VolumeGuidPath ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("$cluster", volume.ClusterBytes);
            command.Parameters.AddWithValue("$total", (long)volume.TotalBytes);
            command.Parameters.AddWithValue("$free", (long)volume.FreeBytes);
            command.Parameters.AddWithValue("$scanned", scanned);
            command.Parameters.AddWithValue("$unaccounted", unaccounted ?? (object)DBNull.Value);
            command.ExecuteNonQuery();
        }
    }

    private void WriteAggregates(SqliteTransaction transaction, long id, ScanAggregateSet aggregates)
    {
        using (var categories = _db.Command("""
            INSERT INTO scan_category_totals (scan_id, category, allocated_bytes, file_count)
            VALUES ($id, $category, $bytes, $files)
            ON CONFLICT(scan_id, category) DO UPDATE SET
                allocated_bytes = $bytes, file_count = $files
            """))
        {
            categories.Transaction = transaction;
            foreach (var total in aggregates.Categories)
            {
                categories.Parameters.Clear();
                categories.Parameters.AddWithValue("$id", id);
                categories.Parameters.AddWithValue("$category", FileCategories.Name(total.Category));
                categories.Parameters.AddWithValue("$bytes", total.AllocatedBytes);
                categories.Parameters.AddWithValue("$files", total.FileCount);
                categories.ExecuteNonQuery();
            }
        }

        using var extensions = _db.Command("""
            INSERT INTO scan_extension_totals (scan_id, extension, allocated_bytes, file_count)
            VALUES ($id, $extension, $bytes, $files)
            ON CONFLICT(scan_id, extension) DO UPDATE SET
                allocated_bytes = $bytes, file_count = $files
            """);
        extensions.Transaction = transaction;

        foreach (var total in aggregates.Extensions)
        {
            extensions.Parameters.Clear();
            extensions.Parameters.AddWithValue("$id", id);
            extensions.Parameters.AddWithValue("$extension", total.Extension);
            extensions.Parameters.AddWithValue("$bytes", total.AllocatedBytes);
            extensions.Parameters.AddWithValue("$files", total.FileCount);
            extensions.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Inserts a row for a snapshot that has no row: the database was deleted, or the
    /// snapshot predates it. Keeps <c>history</c> honest about what is on disk.
    /// </summary>
    internal void Import(long id, Snapshots.SnapshotContents snapshot, string snapshotPath, long snapshotBytes)
    {
        using var transaction = _db.Begin();

        var tree = snapshot.Tree;
        var files = 0;
        long allocated = 0, logical = 0;
        foreach (var root in tree.Roots)
        {
            files += tree.FileCount[root];
            allocated += tree.Allocated[root];
            logical += tree.Logical[root];
        }

        using (var command = _db.Command("""
            INSERT INTO scans (id, started_at, finished_at, status, scanner, flags, roots,
                               total_files, total_dirs, allocated_bytes, logical_bytes,
                               duration_ms, error_count, tool_version, snapshot_path, snapshot_bytes, note)
            VALUES ($id, $started, $finished, $status, $scanner, $flags, $roots,
                    $files, $dirs, $allocated, $logical,
                    $duration, $errors, $version, $snapshotPath, $snapshotBytes, $note)
            """))
        {
            command.Transaction = transaction;
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$started", Iso.Of(snapshot.StartedUtc));
            command.Parameters.AddWithValue("$finished", Iso.Of(snapshot.StartedUtc + snapshot.Duration));
            command.Parameters.AddWithValue("$status",
                (snapshot.Flags & ScanFlags.Partial) != 0 ? ScanStatus.Cancelled : ScanStatus.Completed);
            command.Parameters.AddWithValue("$scanner", Name(snapshot.Scanner));
            command.Parameters.AddWithValue("$flags", (long)snapshot.Flags);
            command.Parameters.AddWithValue("$roots", EncodeRoots([.. snapshot.Volumes.Select(v => v.Root)]));
            command.Parameters.AddWithValue("$files", files);
            command.Parameters.AddWithValue("$dirs", tree.Count - files);
            command.Parameters.AddWithValue("$allocated", allocated);
            command.Parameters.AddWithValue("$logical", logical);
            command.Parameters.AddWithValue("$duration", (long)snapshot.Duration.TotalMilliseconds);
            command.Parameters.AddWithValue("$errors", snapshot.Errors.Count);
            command.Parameters.AddWithValue("$version", "imported");
            command.Parameters.AddWithValue("$snapshotPath", snapshotPath);
            command.Parameters.AddWithValue("$snapshotBytes", snapshotBytes);
            command.Parameters.AddWithValue("$note", "imported from the snapshot file");
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    /// <summary>Retention removed the tree; the numbers stay (README section 5.4).</summary>
    internal void ClearSnapshot(long id)
    {
        using var command = _db.Command(
            "UPDATE scans SET snapshot_path = NULL, snapshot_bytes = NULL WHERE id = $id");
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    internal int Count()
    {
        using var command = _db.Command("SELECT COUNT(*) FROM scans");
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    internal IReadOnlyList<ScanRow> List(int limit = 50, DateTime? since = null)
    {
        using var command = _db.Command($"""
            SELECT {Columns} FROM scans
            WHERE ($since IS NULL OR started_at >= $since)
            ORDER BY id DESC
            LIMIT $limit
            """);

        command.Parameters.AddWithValue("$limit", limit <= 0 ? 50 : limit);
        command.Parameters.AddWithValue("$since", since is { } value ? Iso.Of(value) : (object)DBNull.Value);

        var rows = new List<ScanRow>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) rows.Add(Read(reader));
        return rows;
    }

    internal ScanRow? Find(long id)
    {
        using var command = _db.Command($"SELECT {Columns} FROM scans WHERE id = $id");
        command.Parameters.AddWithValue("$id", id);

        using var reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    internal IReadOnlyList<ScanVolumeRow> Volumes(long scanId)
    {
        using var command = _db.Command("""
            SELECT letter, label, filesystem, volume_serial, volume_guid, cluster_bytes,
                   total_bytes, free_bytes, scanned_bytes, metadata_bytes, unaccounted_bytes,
                   usn_journal_id, next_usn
            FROM scan_volumes WHERE scan_id = $id ORDER BY letter
            """);
        command.Parameters.AddWithValue("$id", scanId);

        var rows = new List<ScanVolumeRow>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new ScanVolumeRow
            {
                Letter = reader.GetString(0),
                Label = reader.IsDBNull(1) ? null : reader.GetString(1),
                FileSystem = reader.IsDBNull(2) ? null : reader.GetString(2),
                VolumeSerial = reader.GetInt64(3),
                VolumeGuid = reader.IsDBNull(4) ? null : reader.GetString(4),
                ClusterBytes = reader.GetInt64(5),
                TotalBytes = reader.GetInt64(6),
                FreeBytes = reader.GetInt64(7),
                ScannedBytes = reader.GetInt64(8),
                MetadataBytes = reader.IsDBNull(9) ? null : reader.GetInt64(9),
                UnaccountedBytes = reader.IsDBNull(10) ? null : reader.GetInt64(10),
                UsnJournalId = reader.IsDBNull(11) ? null : reader.GetInt64(11),
                NextUsn = reader.IsDBNull(12) ? null : reader.GetInt64(12),
            });
        }
        return rows;
    }

    internal IReadOnlyList<CategoryTotal> Categories(long scanId)
    {
        using var command = _db.Command("""
            SELECT category, allocated_bytes, file_count FROM scan_category_totals
            WHERE scan_id = $id ORDER BY allocated_bytes DESC
            """);
        command.Parameters.AddWithValue("$id", scanId);

        var rows = new List<CategoryTotal>(8);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new CategoryTotal(
                ParseCategory(reader.GetString(0)), reader.GetInt64(1), reader.GetInt32(2)));
        }
        return rows;
    }

    private const string Columns = """
        id, started_at, finished_at, status, scanner, flags, roots, total_files, total_dirs,
        allocated_bytes, logical_bytes, duration_ms, error_count, tool_version,
        snapshot_path, snapshot_bytes, note
        """;

    private static ScanRow Read(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        StartedUtc = Iso.Parse(reader.GetString(1)) ?? DateTime.UnixEpoch,
        FinishedUtc = reader.IsDBNull(2) ? null : Iso.Parse(reader.GetString(2)),
        Status = reader.GetString(3),
        Scanner = ParseScanner(reader.GetString(4)),
        Flags = (ScanFlags)reader.GetInt64(5),
        Roots = DecodeRoots(reader.GetString(6)),
        TotalFiles = reader.GetInt32(7),
        TotalDirectories = reader.GetInt32(8),
        AllocatedBytes = reader.GetInt64(9),
        LogicalBytes = reader.GetInt64(10),
        DurationMs = reader.IsDBNull(11) ? null : reader.GetInt64(11),
        ErrorCount = reader.GetInt32(12),
        ToolVersion = reader.GetString(13),
        SnapshotPath = reader.IsDBNull(14) ? null : reader.GetString(14),
        SnapshotBytes = reader.IsDBNull(15) ? null : reader.GetInt64(15),
        Note = reader.IsDBNull(16) ? null : reader.GetString(16),
    };

    internal static string Name(ScannerKind kind) => kind switch
    {
        ScannerKind.Mft => "mft",
        ScannerKind.Incremental => "incremental",
        _ => "walk",
    };

    private static ScannerKind ParseScanner(string value) => value switch
    {
        "mft" => ScannerKind.Mft,
        "incremental" => ScannerKind.Incremental,
        _ => ScannerKind.Walk,
    };

    private static FileCategory ParseCategory(string value) => value switch
    {
        "media" => FileCategory.Media,
        "archive" => FileCategory.Archive,
        "cache" => FileCategory.Cache,
        "source" => FileCategory.Source,
        "document" => FileCategory.Document,
        "app" => FileCategory.App,
        "system" => FileCategory.System,
        _ => FileCategory.Other,
    };

    /// <summary>
    /// Roots as a JSON array. Written with <see cref="Utf8JsonWriter"/> rather than a
    /// serializer: no reflection, so trimming stays safe (README section 18).
    /// </summary>
    private static string EncodeRoots(IReadOnlyList<string> roots)
    {
        using var stream = new MemoryStream(128);
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            foreach (var root in roots) writer.WriteStringValue(root);
            writer.WriteEndArray();
        }
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    private static IReadOnlyList<string> DecodeRoots(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array) return [];

            var roots = new List<string>(4);
            foreach (var element in document.RootElement.EnumerateArray())
                if (element.GetString() is { } root) roots.Add(root);

            return roots;
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
