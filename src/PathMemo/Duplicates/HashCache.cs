using Microsoft.Data.Sqlite;
using PathMemo.Storage;

namespace PathMemo.Duplicates;

/// <summary>
/// Full hashes remembered between runs, keyed by file identity (README section 8.3).
/// </summary>
/// <remarks>
/// <para>
/// <b>Not by path.</b> A photo library that is reorganised once a year would throw away
/// every hash in it, and renaming is exactly what people do between two duplicate hunts.
/// The key is the pair NTFS itself uses - volume serial and file id - plus the size and
/// the modification time, so a file that was edited in place misses the cache and is read
/// again, which is the correct answer.
/// </para>
/// <para>
/// The time is stored as Unix seconds, and the column says so. The snapshot's own
/// timestamps use a different epoch (README section 5.3), and reading one as the other put
/// every comparison thirty years out in P6 - a bug that survived a full phase because the
/// test agreed with the code. Hence the conversion in one place, here.
/// </para>
/// <para>
/// Writes are buffered and flushed in a single transaction: a hundred thousand individual
/// inserts against a WAL database costs more than the hashing did.
/// </para>
/// </remarks>
internal sealed class HashCache(Database database, HashKind algorithm, int maxEntries = 200_000)
{
    private static readonly DateTime UnixEpoch = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly Database _db = database;
    private readonly string _algorithm = HashKinds.Name(algorithm);
    private readonly List<(FileIdentity Id, long Size, long Mtime, string Hash)> _pending = [];
    private readonly List<(FileIdentity Id, long Size, long Mtime)> _touched = [];

    internal int Hits { get; private set; }
    internal int Misses { get; private set; }

    internal static long Seconds(DateTime utc) => (long)(utc - UnixEpoch).TotalSeconds;

    /// <summary>The remembered hash of this exact file, if it has not changed since.</summary>
    internal bool TryGet(FileIdentity identity, long size, DateTime modifiedUtc, out string hash)
    {
        hash = "";
        if (!identity.IsKnown) return false;

        try
        {
            using var command = _db.Command("""
                SELECT full_hash FROM file_hashes
                 WHERE volume_serial = $volume AND file_id_low = $low AND file_id_high = $high
                   AND size_bytes = $size AND mtime_unix = $mtime AND hash_algo = $algo;
                """);

            Bind(command, identity, size, Seconds(modifiedUtc));

            if (command.ExecuteScalar() is not string stored)
            {
                Misses++;
                return false;
            }

            hash = stored;
            Hits++;
            _touched.Add((identity, size, Seconds(modifiedUtc)));
            return true;
        }
        catch (SqliteException)
        {
            // A cache is a convenience. A busy or damaged one means the file gets read,
            // not that the run fails (README section 11.1).
            Misses++;
            return false;
        }
    }

    internal void Put(FileIdentity identity, long size, DateTime modifiedUtc, string hash)
    {
        if (!identity.IsKnown || hash.Length == 0) return;

        _pending.Add((identity, size, Seconds(modifiedUtc), hash));
    }

    /// <summary>Writes what was learned, refreshes what was used, and trims to the cap.</summary>
    internal void Flush()
    {
        if (_pending.Count == 0 && _touched.Count == 0) return;

        try
        {
            using var transaction = _db.Begin();
            var now = Iso.Of(DateTime.UtcNow);

            using (var insert = _db.Command("""
                INSERT INTO file_hashes (volume_serial, file_id_low, file_id_high, size_bytes,
                                         mtime_unix, hash_algo, full_hash, computed_at, last_used_at)
                VALUES ($volume, $low, $high, $size, $mtime, $algo, $hash, $now, $now)
                ON CONFLICT DO UPDATE SET full_hash = $hash, last_used_at = $now;
                """))
            {
                foreach (var (id, size, mtime, hash) in _pending)
                {
                    insert.Parameters.Clear();
                    Bind(insert, id, size, mtime);
                    insert.Parameters.AddWithValue("$hash", hash);
                    insert.Parameters.AddWithValue("$now", now);
                    insert.ExecuteNonQuery();
                }
            }

            using (var touch = _db.Command("""
                UPDATE file_hashes SET last_used_at = $now
                 WHERE volume_serial = $volume AND file_id_low = $low AND file_id_high = $high
                   AND size_bytes = $size AND mtime_unix = $mtime AND hash_algo = $algo;
                """))
            {
                foreach (var (id, size, mtime) in _touched)
                {
                    touch.Parameters.Clear();
                    Bind(touch, id, size, mtime);
                    touch.Parameters.AddWithValue("$now", now);
                    touch.ExecuteNonQuery();
                }
            }

            transaction.Commit();
        }
        catch (SqliteException)
        {
            // Nothing is lost that cannot be recomputed.
        }

        _pending.Clear();
        _touched.Clear();

        Evict();
    }

    /// <summary>
    /// Keeps the table under its cap, oldest use first. 200k rows is about 30 MB, against
    /// a data directory that must stay under 500 MB whatever happens (README section 20).
    /// </summary>
    internal void Evict()
    {
        try
        {
            using var count = _db.Command("SELECT COUNT(*) FROM file_hashes;");
            var rows = Convert.ToInt64(count.ExecuteScalar() ?? 0L);

            if (rows <= maxEntries) return;

            using var delete = _db.Command("""
                DELETE FROM file_hashes WHERE rowid IN (
                    SELECT rowid FROM file_hashes ORDER BY last_used_at ASC LIMIT $excess);
                """);

            delete.Parameters.AddWithValue("$excess", rows - maxEntries);
            delete.ExecuteNonQuery();
        }
        catch (SqliteException)
        {
        }
    }

    private void Bind(SqliteCommand command, FileIdentity identity, long size, long mtime)
    {
        command.Parameters.AddWithValue("$volume", unchecked((long)identity.Volume));
        command.Parameters.AddWithValue("$low", unchecked((long)identity.Low));
        command.Parameters.AddWithValue("$high", unchecked((long)identity.High));
        command.Parameters.AddWithValue("$size", size);
        command.Parameters.AddWithValue("$mtime", mtime);
        command.Parameters.AddWithValue("$algo", _algorithm);
    }
}
