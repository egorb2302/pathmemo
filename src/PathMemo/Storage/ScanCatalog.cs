using Microsoft.Data.Sqlite;
using PathMemo.Snapshots;

namespace PathMemo.Storage;

/// <summary>
/// The database and the snapshots directory, kept consistent with each other.
/// </summary>
/// <remarks>
/// Two stores can always disagree: a snapshot file can be copied in or deleted by hand,
/// and the database can be removed while the trees survive. Rather than trusting either,
/// <see cref="Reconcile"/> makes the rows describe what is actually on disk before any
/// command reads them - the file is the fact, the row is the index.
/// </remarks>
internal sealed class ScanCatalog : IDisposable
{
    private readonly Database _database;

    private ScanCatalog(Database database)
    {
        _database = database;
        Scans = new ScanRepository(database);
    }

    internal ScanRepository Scans { get; }

    internal Database Database => _database;

    internal static ScanCatalog Open() => new(Storage.Database.Open());

    /// <summary>Opens the catalog, or returns null with a reason a command can print.</summary>
    internal static ScanCatalog? TryOpen(out string? error)
    {
        var database = Storage.Database.TryOpen(out error);
        return database is null ? null : new ScanCatalog(database);
    }

    /// <summary>
    /// Imports snapshots that have no row, and forgets the trees of rows whose snapshot
    /// is gone. Returns the number of changes, for <c>doctor</c>.
    /// </summary>
    internal (int Imported, int Forgotten) Reconcile()
    {
        var imported = 0;
        var forgotten = 0;

        try
        {
            var onDisk = SnapshotStore.List();
            var known = new HashSet<long>();

            foreach (var row in Scans.List(limit: int.MaxValue))
            {
                known.Add(row.Id);

                if (row.SnapshotPath is null) continue;

                // Present is not the same as openable: a snapshot truncated by a full
                // disk or written by a future format must stop claiming to be readable.
                if (File.Exists(row.SnapshotPath) && SnapshotFile.IsReadable(row.SnapshotPath)) continue;

                Scans.ClearSnapshot(row.Id);
                forgotten++;
            }

            foreach (var entry in onDisk)
            {
                if (known.Contains(entry.Id)) continue;

                try
                {
                    Scans.Import(entry.Id, SnapshotFile.Read(entry.Path), entry.Path, entry.Bytes);
                    imported++;
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException)
                {
                    // An unreadable snapshot is not a reason to fail a history listing.
                }
            }
        }
        catch (SqliteException)
        {
            // Another pathmemo is writing. Listing what is already recorded is still
            // correct and useful; the next run reconciles.
            _broken = true;
        }

        return (imported, forgotten);
    }

    /// <summary>
    /// Runs a write that the caller can live without. A locked or broken database must
    /// never cost the user a scan, so the first failure is reported and the catalog stops
    /// being used for the rest of the run.
    /// </summary>
    internal bool TryWrite(Action work, out string? error)
    {
        error = null;
        if (_broken) return false;

        try
        {
            work();
            return true;
        }
        catch (SqliteException ex)
        {
            _broken = true;
            error = ex.SqliteErrorCode is 5 or 6
                ? "the history database is in use by another process"
                : ex.Message;
            return false;
        }
        catch (DatabaseException ex)
        {
            _broken = true;
            error = ex.Message;
            return false;
        }
    }

    private bool _broken;

    /// <summary>Retention deleted these snapshots; their rows keep the numbers.</summary>
    internal void ForgetSnapshots(IReadOnlyList<long> ids)
    {
        foreach (var id in ids) Scans.ClearSnapshot(id);
    }

    public void Dispose() => _database.Dispose();
}
