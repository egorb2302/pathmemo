using System.Globalization;
using Microsoft.Data.Sqlite;
using PathMemo.Config;

namespace PathMemo.Storage;

/// <summary>
/// Thrown when the database cannot be used. <see cref="Locked"/> separates
/// "another pathmemo has it" (exit code 8) from a real failure.
/// </summary>
internal sealed class DatabaseException(string message, bool locked = false, Exception? inner = null)
    : Exception(message, inner)
{
    internal bool Locked { get; } = locked;
}

/// <summary>
/// The metadata store: scan history, aggregates, audit findings, the deletion journal
/// (README section 11).
/// </summary>
/// <remarks>
/// <para>
/// Deliberately small. The file tree of a scan is a binary snapshot, not rows: a million
/// rows per scan with a retention policy worth having would make the disk-space tool the
/// largest thing on the disk (README section 5.1).
/// </para>
/// <para>
/// Hand-written SQL against <see cref="SqliteDataReader"/>, no micro-ORM: about fifteen
/// queries, and reflection-based mapping is what makes trimming unsafe (README section 18).
/// </para>
/// </remarks>
internal sealed class Database : IDisposable
{
    internal const int SchemaVersion = 1;

    private readonly SqliteConnection _connection;
    private readonly string _cleanMarker;
    private bool _disposed;

    private Database(SqliteConnection connection, string cleanMarker)
    {
        _connection = connection;
        _cleanMarker = cleanMarker;
    }

    internal SqliteConnection Connection => _connection;

    /// <summary>Whether the previous process exit left the store without its clean marker.</summary>
    internal bool RecoveredFromUncleanExit { get; private init; }

    /// <summary>Result of <c>PRAGMA integrity_check</c>, run only after an unclean exit.</summary>
    internal string? IntegrityResult { get; private init; }

    internal static Database Open() => Open(AppPaths.DatabasePath);

    internal static Database Open(string path)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);

        // The clean-shutdown marker is removed on every start and written back on a
        // clean exit, so a missing marker means the last run died. Only then is the
        // integrity check worth its cost (README section 11).
        var marker = Path.Combine(directory, ".clean");
        var unclean = !File.Exists(marker);
        TryDelete(marker);

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,

            // No pooling: a CLI process opens the database once, and a pooled
            // connection keeps the file handle alive past Dispose, which breaks
            // both the tests and any tool that wants to move the data directory.
            Pooling = false,
            DefaultTimeout = 5,
        };

        SqliteConnection? connection = null;
        try
        {
            connection = new SqliteConnection(builder.ConnectionString);
            connection.Open();
            Configure(connection);

            var integrity = unclean ? IntegrityCheck(connection) : null;
            Migrate(connection);

            return new Database(connection, marker)
            {
                RecoveredFromUncleanExit = unclean,
                IntegrityResult = integrity,
            };
        }
        catch (SqliteException ex)
        {
            connection?.Dispose();
            throw Translate(ex, path);
        }
        catch
        {
            connection?.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Opens the store, or returns null with a message. Used by commands that are still
    /// useful without history: a scan must not fail because the database is busy.
    /// </summary>
    internal static Database? TryOpen(out string? error)
    {
        try
        {
            error = null;
            return Open();
        }
        catch (DatabaseException ex)
        {
            error = ex.Message;
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = ex.Message;
            return null;
        }
    }

    private static void Configure(SqliteConnection connection)
    {
        // WAL so a reader (a second window showing history) never blocks the writer.
        // cache_size is 16 MB rather than the tempting 64: this database is tiny and
        // the memory budget belongs to the scan (README sections 11, 17.3).
        Execute(connection, "PRAGMA journal_mode = WAL");
        Execute(connection, "PRAGMA synchronous = NORMAL");
        Execute(connection, "PRAGMA foreign_keys = ON");
        Execute(connection, "PRAGMA temp_store = MEMORY");
        Execute(connection, "PRAGMA busy_timeout = 5000");
        Execute(connection, "PRAGMA cache_size = -16384");
    }

    private static string IntegrityCheck(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check(32)";

        var lines = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) lines.Add(reader.GetString(0));

        return lines.Count == 0 ? "ok" : string.Join("; ", lines);
    }

    private static void Migrate(SqliteConnection connection)
    {
        var version = UserVersion(connection);

        if (version > SchemaVersion)
            throw new DatabaseException(
                $"the database was written by a newer pathmemo (schema v{version}, this build understands v{SchemaVersion})");

        if (version == SchemaVersion) return;

        using var transaction = connection.BeginTransaction();

        if (version == 0) Execute(connection, SchemaText(), transaction);

        Execute(connection, "PRAGMA user_version = " + SchemaVersion.ToString(CultureInfo.InvariantCulture), transaction);
        transaction.Commit();
    }

    /// <summary>Schema version of the open file, from <c>PRAGMA user_version</c>.</summary>
    internal int Version => UserVersion(_connection);

    private static int UserVersion(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version";
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    internal static string SchemaText()
    {
        using var stream = typeof(Database).Assembly.GetManifestResourceStream("PathMemo.Storage.Schema.sql")
            ?? throw new DatabaseException("the embedded schema is missing from this build");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Folds the write-ahead log back into the database after a scan, so the store does
    /// not sit on a WAL the size of its data (README section 11).
    /// </summary>
    internal void Checkpoint()
    {
        try { Execute(_connection, "PRAGMA wal_checkpoint(PASSIVE)"); }
        catch (SqliteException) { /* a concurrent reader holds it; the next run does it */ }
    }

    internal SqliteCommand Command(string sql)
    {
        var command = _connection.CreateCommand();
        command.CommandText = sql;
        return command;
    }

    internal SqliteTransaction Begin() => _connection.BeginTransaction();

    internal static DatabaseException Translate(SqliteException ex, string path)
    {
        // SQLITE_BUSY and SQLITE_LOCKED: someone else has the file, which is a
        // different answer to the caller than a broken database (exit code 8).
        var locked = ex.SqliteErrorCode is 5 or 6;

        return locked
            ? new DatabaseException("the pathmemo database is in use by another process", locked: true, ex)
            : new DatabaseException($"cannot use {path}: {ex.Message}", locked: false, ex);
    }

    private static void Execute(SqliteConnection connection, string sql, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        command.ExecuteNonQuery();
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { Checkpoint(); }
        catch (DatabaseException) { }
        finally
        {
            _connection.Dispose();

            // Written last: its presence is the statement "the previous run ended on
            // its own terms", which is what suppresses the next integrity check.
            try { File.WriteAllText(_cleanMarker, ""); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
