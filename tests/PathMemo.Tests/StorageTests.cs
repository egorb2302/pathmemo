using PathMemo.Config;
using PathMemo.Scanning;
using PathMemo.Snapshots;
using PathMemo.Storage;
using Xunit;

namespace PathMemo.Tests;

/// <summary>
/// The metadata store: schema, migration, history rows and reconciliation with the
/// snapshots directory (README sections 10, 11).
/// </summary>
/// <remarks>
/// Against a real SQLite file in %TEMP%, not an in-memory database: WAL mode, the clean
/// marker and the file-vs-row reconciliation are exactly the parts that only exist on
/// disk, and they are what these tests are about.
/// </remarks>
public sealed class StorageTests : IDisposable
{
    private readonly string _dataDirectory = Path.Combine(
        Path.GetTempPath(), "pathmemo-db-" + Guid.NewGuid().ToString("N")[..12]);

    public StorageTests()
    {
        Directory.CreateDirectory(_dataDirectory);
        TestStore.Use(_dataDirectory);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dataDirectory, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    [Fact]
    public void Creates_the_schema_once_and_reopens_it()
    {
        using (var first = Database.Open())
        {
            Assert.Equal(Database.SchemaVersion, first.Version);
        }

        // The clean marker was written on close, so the second open must not spend time
        // on an integrity check.
        using var second = Database.Open();
        Assert.Equal(Database.SchemaVersion, second.Version);
        Assert.False(second.RecoveredFromUncleanExit);
        Assert.Null(second.IntegrityResult);
    }

    [Fact]
    public void Checks_integrity_only_after_an_unclean_exit()
    {
        using (var _ = Database.Open()) { }

        File.Delete(AppPaths.CleanShutdownMarker);

        using var reopened = Database.Open();
        Assert.True(reopened.RecoveredFromUncleanExit);
        Assert.Equal("ok", reopened.IntegrityResult);
    }

    [Fact]
    public void Refuses_a_database_from_a_newer_build()
    {
        using (var database = Database.Open())
        {
            using var command = database.Command("PRAGMA user_version = 99");
            command.ExecuteNonQuery();
        }

        var thrown = Assert.Throws<DatabaseException>(() => Database.Open());
        Assert.Contains("newer pathmemo", thrown.Message, StringComparison.Ordinal);
        Assert.False(thrown.Locked);
    }

    [Fact]
    public void Records_a_scan_with_its_volumes_and_aggregates()
    {
        var result = new TestTree()
            .File(@"C:\movies\holiday.mp4", 4L << 30)
            .File(@"C:\src\app\main.cs", 4096)
            .File(@"C:\src\app\node_modules\pkg\index.js", 1L << 20)
            .Result(new DateTime(2026, 9, 18, 10, 0, 0, DateTimeKind.Utc));

        using var catalog = ScanCatalog.Open();
        var id = catalog.Scans.BeginNew(result.StartedUtc, ScannerKind.Mft, [@"C:\"], "before the cleanup");

        var running = catalog.Scans.Find(id);
        Assert.NotNull(running);
        Assert.Equal(ScanStatus.Running, running.Status);
        Assert.Equal("before the cleanup", running.Note);
        Assert.Equal([@"C:\"], running.Roots);

        catalog.Scans.Complete(id, result, ScanStatus.Completed, @"C:\snapshots\1.pmsnap", 12345);

        var row = catalog.Scans.Find(id);
        Assert.NotNull(row);
        Assert.Equal(ScanStatus.Completed, row.Status);
        Assert.Equal(3, row.TotalFiles);
        Assert.Equal(result.AllocatedBytes, row.AllocatedBytes);
        Assert.Equal(12345, row.SnapshotBytes);
        Assert.True(row.SnapshotAvailable);
        Assert.Equal(ScannerKind.Mft, row.Scanner);

        var volumes = catalog.Scans.Volumes(id);
        var volume = Assert.Single(volumes);
        Assert.Equal("C:", volume.Letter);
        Assert.Equal(result.AllocatedBytes, volume.ScannedBytes);
        Assert.Equal(volume.UsedBytes - volume.ScannedBytes, volume.UnaccountedBytes);

        var categories = catalog.Scans.Categories(id);
        Assert.Equal(4L << 30, categories.Single(c => c.Category == PathMemo.Analysis.FileCategory.Media).AllocatedBytes);
        Assert.Equal(1L << 20, categories.Single(c => c.Category == PathMemo.Analysis.FileCategory.Cache).AllocatedBytes);
    }

    [Fact]
    public void Keeps_the_numbers_of_a_scan_whose_snapshot_is_gone()
    {
        var result = new TestTree()
            .File(@"C:\big.bin", 8L << 30)
            .Result(DateTime.UtcNow);

        using var catalog = ScanCatalog.Open();
        var id = catalog.Scans.BeginNew(result.StartedUtc, ScannerKind.Walk, [@"C:\"], null);
        catalog.Scans.Complete(id, result, ScanStatus.Completed, @"C:\snapshots\gone.pmsnap", 999);

        catalog.ForgetSnapshots([id]);

        var row = catalog.Scans.Find(id);
        Assert.NotNull(row);
        Assert.False(row.SnapshotAvailable);
        Assert.Null(row.SnapshotBytes);
        Assert.Equal(8L << 30, row.AllocatedBytes);      // the data point survives
    }

    [Fact]
    public void Imports_a_snapshot_that_has_no_row()
    {
        var result = new TestTree()
            .File(@"C:\iso\win.iso", 6L << 30)
            .Result(new DateTime(2026, 9, 1, 3, 0, 0, DateTimeKind.Utc));

        // A snapshot on disk with an empty database: what happens when the database is
        // deleted, or when snapshots are copied from another machine.
        SnapshotStore.Save(result, 7);

        using var catalog = ScanCatalog.Open();
        var (imported, forgotten) = catalog.Reconcile();

        Assert.Equal(1, imported);
        Assert.Equal(0, forgotten);

        var row = catalog.Scans.Find(7);
        Assert.NotNull(row);
        Assert.Equal(6L << 30, row.AllocatedBytes);
        Assert.True(row.SnapshotAvailable);
        Assert.Equal(new DateTime(2026, 9, 1, 3, 0, 0, DateTimeKind.Utc), row.StartedUtc);

        // And the next scan does not reuse the number.
        Assert.Equal(8, catalog.Scans.BeginNew(DateTime.UtcNow, ScannerKind.Walk, [@"C:\"], null));
    }

    [Fact]
    public void Forgets_a_row_whose_snapshot_was_deleted_by_hand()
    {
        var result = new TestTree().File(@"C:\a.bin", 1L << 30).Result(DateTime.UtcNow);

        using var catalog = ScanCatalog.Open();
        SnapshotStore.Save(result, 3);
        catalog.Reconcile();
        Assert.True(catalog.Scans.Find(3)!.SnapshotAvailable);

        File.Delete(SnapshotStore.PathFor(3));
        var (_, forgotten) = catalog.Reconcile();

        Assert.Equal(1, forgotten);
        Assert.False(catalog.Scans.Find(3)!.SnapshotAvailable);
    }

    [Fact]
    public void Marks_a_corrupt_snapshot_unavailable_instead_of_failing()
    {
        var result = new TestTree().File(@"C:\a.bin", 1L << 30).Result(DateTime.UtcNow);

        using var catalog = ScanCatalog.Open();
        SnapshotStore.Save(result, 5);
        catalog.Reconcile();

        // A snapshot truncated by a full disk, or from a future format version.
        File.WriteAllBytes(SnapshotStore.PathFor(5), new byte[64]);

        var (_, forgotten) = catalog.Reconcile();

        Assert.Equal(1, forgotten);
        var row = catalog.Scans.Find(5);
        Assert.NotNull(row);
        Assert.False(row.SnapshotAvailable);
        Assert.Equal(1L << 30, row.AllocatedBytes);
    }

    [Fact]
    public void Filters_history_by_date_and_limit()
    {
        using var catalog = ScanCatalog.Open();

        for (var day = 1; day <= 5; day++)
        {
            var started = new DateTime(2026, 9, day, 3, 0, 0, DateTimeKind.Utc);
            var result = new TestTree().File(@"C:\a.bin", day << 20).Result(started);
            var id = catalog.Scans.BeginNew(started, ScannerKind.Walk, [@"C:\"], null);
            catalog.Scans.Complete(id, result, ScanStatus.Completed, null, null);
        }

        Assert.Equal(5, catalog.Scans.List().Count);
        Assert.Equal(2, catalog.Scans.List(limit: 2).Count);

        var recent = catalog.Scans.List(since: new DateTime(2026, 9, 4, 0, 0, 0, DateTimeKind.Utc));
        Assert.Equal(2, recent.Count);
        Assert.Equal([5, 4], recent.Select(r => r.Id));
    }

    [Fact]
    public void A_locked_database_is_reported_and_never_thrown_at_a_scan()
    {
        using var catalog = ScanCatalog.Open();

        // Don't wait out the five-second busy timeout in a unit test.
        using (var impatient = catalog.Database.Command("PRAGMA busy_timeout = 0"))
            impatient.ExecuteNonQuery();

        using var blocker = new Microsoft.Data.Sqlite.SqliteConnection(
            $"Data Source={AppPaths.DatabasePath};Pooling=False");
        blocker.Open();
        using (var exclusive = blocker.CreateCommand())
        {
            exclusive.CommandText = "BEGIN EXCLUSIVE";
            exclusive.ExecuteNonQuery();
        }

        var written = catalog.TryWrite(
            () => catalog.Scans.BeginNew(DateTime.UtcNow, ScannerKind.Walk, [@"C:\"], null),
            out var error);

        Assert.False(written);
        Assert.Equal("the history database is in use by another process", error);

        // And the catalog stays quiet from then on, rather than reporting every step.
        Assert.False(catalog.TryWrite(() => catalog.Scans.ClearSnapshot(1), out var second));
        Assert.Null(second);
    }

    [Fact]
    public void Marks_a_scan_that_died_mid_run()
    {
        using var catalog = ScanCatalog.Open();
        var id = catalog.Scans.BeginNew(DateTime.UtcNow, ScannerKind.Mft, [@"C:\"], null);
        catalog.Scans.Fail(id, ScanStatus.Failed, "the volume went away");

        var row = catalog.Scans.Find(id);
        Assert.NotNull(row);
        Assert.Equal(ScanStatus.Failed, row.Status);
        Assert.Equal("the volume went away", row.Note);
        Assert.NotNull(row.FinishedUtc);
    }
}
