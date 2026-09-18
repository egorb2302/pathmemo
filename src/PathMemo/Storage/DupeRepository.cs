using PathMemo.Duplicates;

namespace PathMemo.Storage;

/// <summary>
/// The last duplicate search, kept so a screen can show it without reading the disk again
/// (README section 11).
/// </summary>
/// <remarks>
/// <para>
/// A cache, not history. Scans are worth keeping - they are how "what changed" is answered
/// - but a duplicate run is a statement about the disk as it was five minutes ago, and
/// twelve of them tell nobody anything. Saving one deletes the one before it.
/// </para>
/// <para>
/// What comes back is the shape of the report, not the report: the per-file allocated size
/// is not a column, so a reloaded group carries the total it measured and each file stands
/// at the group's content size. The screens show group totals, and anything that is about
/// to delete re-reads the files anyway (README section 8.4).
/// </para>
/// </remarks>
internal sealed class DupeRepository(Database database)
{
    private readonly Database _db = database;

    internal long Save(DupeReport report)
    {
        using var transaction = _db.Begin();

        using (var clear = _db.Command("DELETE FROM dupe_runs;"))
            clear.ExecuteNonQuery();

        long runId;
        using (var run = _db.Command("""
            INSERT INTO dupe_runs (ran_at, scan_id, min_size, hash_algo, group_count, wasted_bytes)
            VALUES ($ran, $scan, $min, $algo, $groups, $wasted);
            SELECT last_insert_rowid();
            """))
        {
            run.Parameters.AddWithValue("$ran", Iso.Of(report.RanAtUtc));
            run.Parameters.AddWithValue("$scan", report.ScanId > 0 ? report.ScanId : (object)DBNull.Value);
            run.Parameters.AddWithValue("$min", report.MinBytes);
            run.Parameters.AddWithValue("$algo", HashKinds.Name(report.Algorithm));
            run.Parameters.AddWithValue("$groups", report.Groups.Count);
            run.Parameters.AddWithValue("$wasted", report.Wasted);

            runId = (long)run.ExecuteScalar()!;
        }

        using var group = _db.Command("""
            INSERT INTO dupe_groups (run_id, size_bytes, file_count, wasted_bytes, kind, full_hash)
            VALUES ($run, $size, $files, $wasted, $kind, $hash);
            SELECT last_insert_rowid();
            """);

        using var file = _db.Command("""
            INSERT INTO dupe_files (group_id, seq, path, mtime_unix, link_count, suggested_keep)
            VALUES ($group, $seq, $path, $mtime, $links, $keep);
            """);

        foreach (var entry in report.Groups)
        {
            group.Parameters.Clear();
            group.Parameters.AddWithValue("$run", runId);
            group.Parameters.AddWithValue("$size", entry.Bytes);
            group.Parameters.AddWithValue("$files", entry.Count);
            group.Parameters.AddWithValue("$wasted", entry.Wasted);
            group.Parameters.AddWithValue("$kind", Name(entry.Kind));
            group.Parameters.AddWithValue("$hash", entry.Hash);

            var groupId = (long)group.ExecuteScalar()!;
            var seq = 0;

            foreach (var member in entry.Files)
            {
                file.Parameters.Clear();
                file.Parameters.AddWithValue("$group", groupId);
                file.Parameters.AddWithValue("$seq", seq++);
                file.Parameters.AddWithValue("$path", member.Path);
                file.Parameters.AddWithValue("$mtime", HashCache.Seconds(member.ModifiedUtc));
                file.Parameters.AddWithValue("$links", member.LinkCount);
                file.Parameters.AddWithValue("$keep", member.Keeper ? 1 : 0);
                file.ExecuteNonQuery();
            }
        }

        transaction.Commit();
        return runId;
    }

    /// <summary>The stored run, or null when there has not been one.</summary>
    internal DupeReport? Latest()
    {
        long runId;
        var report = new DupeReport { Groups = [], ScanId = 0 };

        using (var command = _db.Command("""
            SELECT id, ran_at, scan_id, min_size, hash_algo FROM dupe_runs ORDER BY id DESC LIMIT 1;
            """))
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read()) return null;

            runId = reader.GetInt64(0);
            HashKinds.TryParse(reader.GetString(4), out var algorithm);

            report = report with
            {
                RanAtUtc = Iso.Parse(reader.GetString(1)) ?? DateTime.UtcNow,
                ScanId = reader.IsDBNull(2) ? 0 : reader.GetInt64(2),
                MinBytes = reader.GetInt64(3),
                Algorithm = algorithm,
            };
        }

        var groups = new List<DupeGroup>();
        var files = Files(runId);

        using (var command = _db.Command("""
            SELECT id, size_bytes, wasted_bytes, kind, full_hash
              FROM dupe_groups WHERE run_id = $run ORDER BY wasted_bytes DESC, id;
            """))
        {
            command.Parameters.AddWithValue("$run", runId);

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var id = reader.GetInt64(0);
                var size = reader.GetInt64(1);

                if (!files.TryGetValue(id, out var members) || members.Count == 0) continue;

                groups.Add(new DupeGroup
                {
                    Kind = reader.GetString(3) == "hardlink_set" ? DupeKind.HardlinkSet : DupeKind.Duplicate,
                    Bytes = size,
                    Wasted = reader.GetInt64(2),
                    Hash = reader.GetString(4),
                    Files = [.. members.Select(m => m with { Bytes = size, Allocated = size })],
                });
            }
        }

        return report with { Groups = groups, Stored = true };
    }

    private Dictionary<long, List<DupeFile>> Files(long runId)
    {
        var byGroup = new Dictionary<long, List<DupeFile>>();

        using var command = _db.Command("""
            SELECT f.group_id, f.path, f.mtime_unix, f.link_count, f.suggested_keep
              FROM dupe_files f JOIN dupe_groups g ON g.id = f.group_id
             WHERE g.run_id = $run ORDER BY f.group_id, f.seq;
            """);

        command.Parameters.AddWithValue("$run", runId);

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var group = reader.GetInt64(0);
            if (!byGroup.TryGetValue(group, out var list)) byGroup[group] = list = [];

            list.Add(new DupeFile
            {
                Path = reader.GetString(1),
                Bytes = 0,
                Allocated = 0,
                ModifiedUtc = reader.IsDBNull(2)
                    ? default
                    : DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(2)).UtcDateTime,
                LinkCount = reader.GetInt32(3),
                Keeper = reader.GetInt32(4) != 0,
            });
        }

        return byGroup;
    }

    private static string Name(DupeKind kind) => kind == DupeKind.HardlinkSet ? "hardlink_set" : "duplicate";
}
