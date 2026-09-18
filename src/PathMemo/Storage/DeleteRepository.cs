using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PathMemo.Deletion;

namespace PathMemo.Storage;

/// <summary>One row of the operations journal (README section 9.7).</summary>
internal sealed record DeleteOpRow
{
    internal required long Id { get; init; }
    internal required DateTime StartedAt { get; init; }
    internal DateTime? FinishedAt { get; init; }
    internal required string Mode { get; init; }
    internal required string Source { get; init; }
    internal long? ScanId { get; init; }
    internal required int ItemCount { get; init; }
    internal required long PredictedBytes { get; init; }
    internal long? ActualFreedBytes { get; init; }
    internal required string Status { get; init; }
    internal string? QuarantinePath { get; init; }
    internal DateTime? PurgeAfter { get; init; }
    internal string? Reason { get; init; }

    internal bool Restorable => Status == "completed" && Mode == "quarantine";
}

internal sealed record DeleteItemRow(
    long OpId, int Seq, string OriginalPath, string? StoredName, long SizeBytes,
    string Result, int? HResult, string? Message);

/// <summary>
/// The deletion journal: every operation and every item in it (README section 9.7).
/// </summary>
/// <remarks>
/// <para>
/// The row is written <b>before</b> anything is touched and updated afterwards, so a
/// process killed halfway still leaves a record of what it was doing. An operation with no
/// finish time is exactly that: started, outcome unknown.
/// </para>
/// <para>
/// Dry runs go to a separate table. A journal that mixes "would have deleted" with "did
/// delete" is worthless on the day someone has to read it carefully.
/// </para>
/// </remarks>
internal sealed class DeleteRepository(Database database)
{
    private readonly Database _db = database;

    /// <summary>
    /// Opens an operation and returns its id, which the quarantine also uses as its
    /// directory name (README section 9.4).
    /// </summary>
    internal long Begin(DeletePlan plan, DateTime startedUtc, DateTime? purgeAfter)
    {
        using var command = _db.Command("""
            INSERT INTO delete_ops (started_at, mode, source, scan_id, item_count,
                                    predicted_bytes, status, purge_after, reason)
            VALUES ($started, $mode, $source, $scan, $items, $predicted, 'running', $purge, $reason);
            SELECT last_insert_rowid();
            """);

        command.Parameters.AddWithValue("$started", Iso.Of(startedUtc));
        command.Parameters.AddWithValue("$mode", DeleteModes.Name(plan.Mode));
        command.Parameters.AddWithValue("$source", plan.Source.ToString().ToLowerInvariant());
        command.Parameters.AddWithValue("$scan", plan.ScanId ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$items", plan.Items.Count);
        command.Parameters.AddWithValue("$predicted", plan.TotalBytes);
        command.Parameters.AddWithValue("$purge", purgeAfter is { } p ? Iso.Of(p) : (object)DBNull.Value);
        command.Parameters.AddWithValue("$reason", plan.Reason ?? (object)DBNull.Value);

        return (long)command.ExecuteScalar()!;
    }

    /// <summary>Closes the operation and writes one row per item.</summary>
    internal void Complete(OpOutcome outcome, DateTime finishedUtc)
    {
        using var transaction = _db.Begin();

        using (var command = _db.Command("""
            UPDATE delete_ops
               SET finished_at = $finished, status = $status, actual_freed_bytes = $freed,
                   quarantine_path = $quarantine, item_count = $items, predicted_bytes = $predicted
             WHERE id = $id
            """))
        {
            command.Transaction = transaction;
            command.Parameters.AddWithValue("$finished", Iso.Of(finishedUtc));
            command.Parameters.AddWithValue("$status", outcome.Status);
            command.Parameters.AddWithValue("$freed", outcome.ActualFreedBytes ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("$quarantine", outcome.QuarantinePath ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("$items", outcome.Items.Count);
            command.Parameters.AddWithValue("$predicted", outcome.PredictedBytes);
            command.Parameters.AddWithValue("$id", outcome.OpId);
            command.ExecuteNonQuery();
        }

        using (var command = _db.Command("""
            INSERT INTO delete_items (op_id, seq, original_path, stored_name, size_bytes,
                                      result, hresult, message)
            VALUES ($op, $seq, $path, $stored, $size, $result, $hresult, $message)
            """))
        {
            command.Transaction = transaction;

            var seq = 0;
            foreach (var item in outcome.Items)
            {
                command.Parameters.Clear();
                command.Parameters.AddWithValue("$op", outcome.OpId);
                command.Parameters.AddWithValue("$seq", ++seq);
                command.Parameters.AddWithValue("$path", item.Item.DisplayPath);
                command.Parameters.AddWithValue("$stored", item.StoredName ?? (object)DBNull.Value);
                command.Parameters.AddWithValue("$size", item.Item.Bytes);
                command.Parameters.AddWithValue("$result", item.Result.ToString().ToLowerInvariant());
                command.Parameters.AddWithValue("$hresult", item.Error == 0 ? DBNull.Value : item.Error);
                command.Parameters.AddWithValue("$message", item.Message ?? (object)DBNull.Value);
                command.ExecuteNonQuery();
            }
        }

        transaction.Commit();
    }

    /// <summary>Marks an operation restored or purged, with what the purge actually freed.</summary>
    internal void SetStatus(long opId, string status, long? freedBytes = null)
    {
        using var command = _db.Command("""
            UPDATE delete_ops
               SET status = $status,
                   actual_freed_bytes = COALESCE($freed, actual_freed_bytes),
                   finished_at = COALESCE(finished_at, $now)
             WHERE id = $id
            """);

        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$freed", freedBytes ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$now", Iso.Of(DateTime.UtcNow));
        command.Parameters.AddWithValue("$id", opId);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Records a rehearsal. Deliberately in its own table, and deliberately holding the
    /// paths: the point of a dry run is to be able to prove afterwards what it covered.
    /// </summary>
    internal long LogDryRun(DeletePlan plan)
    {
        using var command = _db.Command("""
            INSERT INTO dryrun_log (ran_at, source, item_count, total_bytes, detail_json)
            VALUES ($ran, $source, $items, $bytes, $detail);
            SELECT last_insert_rowid();
            """);

        command.Parameters.AddWithValue("$ran", Iso.Of(DateTime.UtcNow));
        command.Parameters.AddWithValue("$source", plan.Source.ToString().ToLowerInvariant());
        command.Parameters.AddWithValue("$items", plan.Items.Count);
        command.Parameters.AddWithValue("$bytes", plan.TotalBytes);
        command.Parameters.AddWithValue("$detail", Detail(plan));

        return (long)command.ExecuteScalar()!;
    }

    private static string Detail(DeletePlan plan)
    {
        using var stream = new MemoryStream(512);
        using (var json = new Utf8JsonWriter(stream))
        {
            json.WriteStartObject();
            json.WriteString("mode", DeleteModes.Name(plan.Mode));
            json.WriteString("token", plan.Token);
            if (plan.ModeReason is { } why) json.WriteString("modeReason", why);

            json.WriteStartArray("items");
            foreach (var item in plan.Items.Take(1000))
            {
                json.WriteStartObject();
                json.WriteString("path", item.DisplayPath);
                json.WriteNumber("bytes", item.Bytes);
                json.WriteBoolean("directory", item.IsDirectory);
                json.WriteEndObject();
            }
            json.WriteEndArray();

            json.WriteStartArray("refused");
            foreach (var refusal in plan.Refusals.Take(1000))
            {
                json.WriteStartObject();
                json.WriteString("path", refusal.Path);
                json.WriteString("reason", refusal.Reason);
                json.WriteEndObject();
            }
            json.WriteEndArray();

            json.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    internal IReadOnlyList<DeleteOpRow> List(int limit)
    {
        using var command = _db.Command($"""
            SELECT id, started_at, finished_at, mode, source, scan_id, item_count,
                   predicted_bytes, actual_freed_bytes, status, quarantine_path, purge_after, reason
              FROM delete_ops
             ORDER BY id DESC
             LIMIT {Math.Clamp(limit, 1, 10_000)}
            """);

        var rows = new List<DeleteOpRow>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) rows.Add(Read(reader));

        return rows;
    }

    internal DeleteOpRow? Find(long id)
    {
        using var command = _db.Command("""
            SELECT id, started_at, finished_at, mode, source, scan_id, item_count,
                   predicted_bytes, actual_freed_bytes, status, quarantine_path, purge_after, reason
              FROM delete_ops WHERE id = $id
            """);
        command.Parameters.AddWithValue("$id", id);

        using var reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    internal IReadOnlyList<DeleteItemRow> Items(long opId)
    {
        using var command = _db.Command("""
            SELECT op_id, seq, original_path, stored_name, size_bytes, result, hresult, message
              FROM delete_items WHERE op_id = $id ORDER BY seq
            """);
        command.Parameters.AddWithValue("$id", opId);

        var rows = new List<DeleteItemRow>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new DeleteItemRow(
                reader.GetInt64(0), reader.GetInt32(1), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetInt64(4), reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetInt32(6),
                reader.IsDBNull(7) ? null : reader.GetString(7)));
        }

        return rows;
    }

    /// <summary>Quarantines whose retention has run out (README section 9.4).</summary>
    internal IReadOnlyList<DeleteOpRow> Expired(DateTime nowUtc) =>
        [.. List(1000).Where(row => row is { Status: "completed", Mode: "quarantine" }
                                    && row.PurgeAfter is { } due && due <= nowUtc)];

    private static DeleteOpRow Read(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        StartedAt = Iso.Parse(reader.GetString(1)) ?? default,
        FinishedAt = reader.IsDBNull(2) ? null : Iso.Parse(reader.GetString(2)),
        Mode = reader.GetString(3),
        Source = reader.GetString(4),
        ScanId = reader.IsDBNull(5) ? null : reader.GetInt64(5),
        ItemCount = reader.GetInt32(6),
        PredictedBytes = reader.GetInt64(7),
        ActualFreedBytes = reader.IsDBNull(8) ? null : reader.GetInt64(8),
        Status = reader.GetString(9),
        QuarantinePath = reader.IsDBNull(10) ? null : reader.GetString(10),
        PurgeAfter = reader.IsDBNull(11) ? null : Iso.Parse(reader.GetString(11)),
        Reason = reader.IsDBNull(12) ? null : reader.GetString(12),
    };
}
