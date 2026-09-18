using System.Diagnostics;
using PathMemo.Config;
using PathMemo.Deletion;
using PathMemo.Snapshots;
using PathMemo.Storage;
using Xunit;

namespace PathMemo.Tests;

/// <summary>
/// Deletion against a real filesystem (README sections 9, 22.2).
/// </summary>
/// <remarks>
/// <para>
/// Nothing here can be faked usefully. Canonicalisation, junctions, rename-by-handle and
/// the free-space delta are Win32 behaviour; a test double would assert that the double
/// works. Everything happens under a directory in <c>%TEMP%</c> and a redirected data
/// directory, both removed afterwards.
/// </para>
/// <para>
/// The protection tests are the ones that matter most: they assert a refusal, so a bug
/// that made them pass for the wrong reason (a path that does not exist, say) would still
/// be a refusal. That is the correct failure direction for this module.
/// </para>
/// </remarks>
public sealed class DeletionTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "pathmemo-rm-" + Guid.NewGuid().ToString("N")[..12]);

    private readonly string _dataDirectory = Path.Combine(
        Path.GetTempPath(), "pathmemo-rmdb-" + Guid.NewGuid().ToString("N")[..12]);

    public DeletionTests()
    {
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_dataDirectory);
        AppPaths.Redirect(_dataDirectory);
        AppConfig.Reset();
    }

    public void Dispose()
    {
        RemoveReparsePoints(_root);
        Delete(_root);
        Delete(_dataDirectory);
        AppConfig.Reset();
    }

    private static void Delete(string directory)
    {
        try { Directory.Delete(directory, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException) { }
    }

    private static void RemoveReparsePoints(string directory)
    {
        if (!Directory.Exists(directory)) return;

        foreach (var child in Directory.EnumerateDirectories(directory))
        {
            if ((System.IO.File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0)
            {
                Directory.Delete(child, recursive: false);
                continue;
            }

            RemoveReparsePoints(child);
        }
    }

    private string File_(string relative, int bytes = 64)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    /// <summary>The per-item messages, so a failing assertion says what actually went wrong.</summary>
    private static string Why(OpOutcome outcome) =>
        string.Join("; ", outcome.Items.Select(i => $"{i.Item.DisplayPath} -> {i.Result}: {i.Message}"));

    private static DeleteEngine Engine() => new(AppConfig.Current, new PathGuard(ProtectedSet.Build()));

    // ---- README section 9.3: the twelve spellings -------------------------------------

    public static TheoryData<string> Bypasses()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);   // C:\Windows
        var drive = windows[..2];                                                     // C:
        var letter = windows[0];

        return
        [
            windows.ToLowerInvariant(),                      // case
            windows + @"\",                                  // trailing separator
            windows.Replace('\\', '/'),                      // forward slashes
            $@"\\?\{windows}",                               // Win32 namespace prefix
            $@"\\.\{windows}",                               // device namespace prefix
            $@"\\localhost\{letter}$\Windows",               // UNC to this machine
            $@"\\127.0.0.1\{letter}$\Windows",               // the same by address
            $@"{drive}\Users\..\Windows",                    // traversal
            $@"{drive}\PROGRA~1",                            // 8.3 short name
            $@"{drive}\Documents and Settings",              // junction -> C:\Users
            $@"{drive}\Users\All Users",                     // junction -> C:\ProgramData
            $@"{drive}\Windows\System32\drivers\etc",        // deep inside
        ];
    }

    [Theory]
    [MemberData(nameof(Bypasses))]
    public void Refuses_every_spelling_of_a_protected_path(string path)
    {
        var outcome = new PathGuard(ProtectedSet.Build()).Open(path);

        Assert.Null(outcome.Target);
        Assert.NotNull(outcome.Refusal);
    }

    [Fact]
    public void Canonicalises_the_aliases_of_one_directory_to_one_name()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var canonical = Canonical.TryOf(windows);

        Assert.NotNull(canonical);
        Assert.StartsWith(@"\\?\Volume{", canonical, StringComparison.OrdinalIgnoreCase);

        // The property the whole protected set rests on: different spellings, one name.
        Assert.Equal(canonical, Canonical.TryOf(windows.ToLowerInvariant()));
        Assert.Equal(canonical, Canonical.TryOf(windows + @"\"));
        Assert.Equal(canonical, Canonical.TryOf($@"\\?\{windows}"));
        Assert.Equal(canonical, Canonical.TryOf($@"{windows[..2]}\Users\..\Windows"));
    }

    [Fact]
    public void Refuses_what_it_cannot_open()
    {
        var outcome = new PathGuard(ProtectedSet.Build()).Open(Path.Combine(_root, "not-here"));

        Assert.Null(outcome.Target);
        Assert.Equal("does not exist", outcome.Refusal);
    }

    [Fact]
    public void Allows_an_ordinary_directory_deep_enough_to_be_ordinary()
    {
        File_(@"work\a.bin");

        using var outcome = new PathGuard(ProtectedSet.Build()).Open(Path.Combine(_root, "work")).Target;

        Assert.NotNull(outcome);
        Assert.True(outcome.IsDirectory);
    }

    // ---- README section 9.5: recursive deletion that cannot be redirected --------------

    [Fact]
    public void Deletes_a_tree_without_following_a_junction_out_of_it()
    {
        var outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(outside);
        var bystander = Path.Combine(outside, "keep-me.bin");
        System.IO.File.WriteAllBytes(bystander, new byte[128]);

        var doomed = Path.Combine(_root, "doomed");
        File_(@"doomed\a\b\c.bin", 256);
        File_(@"doomed\a\d.bin", 512);

        if (!TryCreateJunction(Path.Combine(doomed, "a", "link"), outside)) return;

        TreeDeleteReport report;
        using (var target = new PathGuard(ProtectedSet.Build()).Open(doomed, PathGuard.DeleteAccess).Target)
        {
            Assert.NotNull(target);
            report = HandleTreeDeleter.Delete(target);
        }

        Assert.True(report.Complete, string.Join("; ", report.Errors.Select(e => $"{e.Path}: {e.Message}")));
        Assert.False(Directory.Exists(doomed));
        Assert.Equal(1, report.ReparsePoints);
        Assert.Equal(2, report.Files);

        // The junction was removed as a link. What it pointed at is untouched - the whole
        // reason this deleter opens children relative to their parent handle.
        Assert.True(System.IO.File.Exists(bystander));
    }

    [Fact]
    public void Deletes_a_read_only_file()
    {
        var path = File_("readonly.bin");
        System.IO.File.SetAttributes(path, FileAttributes.ReadOnly);

        using (var target = new PathGuard(ProtectedSet.Build()).Open(path, PathGuard.DeleteAccess).Target)
        {
            Assert.NotNull(target);
            Assert.True(HandleTreeDeleter.Delete(target).Complete);
        }

        Assert.False(System.IO.File.Exists(path));
    }

    [Fact]
    public void Unlinks_a_file_another_process_still_holds_open()
    {
        var path = File_("busy.bin", 1024);

        // A cache inside a running application is exactly this case: the name has to go
        // now, and the data can go when the last handle closes (README section 9.5).
        using var reader = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);

        using (var target = new PathGuard(ProtectedSet.Build()).Open(path, PathGuard.DeleteAccess).Target)
        {
            Assert.NotNull(target);
            Assert.True(HandleTreeDeleter.Delete(target).Complete);
        }

        Assert.False(System.IO.File.Exists(path));
        Assert.Equal(1024, reader.Length);     // the open handle still reads its data
    }

    // ---- README sections 9.4, 9.7: quarantine, journal, restore, purge -----------------

    [Fact]
    public void Quarantines_restores_and_purges_with_a_journal_entry()
    {
        var kept = File_(@"stuff\big.bin", 4096);
        File_(@"stuff\nested\small.bin", 512);

        var engine = Engine();
        var plan = engine.Plan(new DeleteRequest { Paths = [Path.Combine(_root, "stuff")] });

        Assert.Single(plan.Items);
        Assert.Equal(DeleteMode.Quarantine, plan.Mode);
        Assert.Equal(2, plan.TotalFiles);

        using var catalog = ScanCatalog.Open();
        var journal = new DeleteRepository(catalog.Database);

        var outcome = engine.Execute(plan, journal);

        Assert.True(outcome.Status == "completed", Why(outcome));
        Assert.False(Directory.Exists(Path.Combine(_root, "stuff")));

        // Quarantine is a rename on the same volume: it frees nothing until purge.
        Assert.Equal(0, plan.FreesNow);
        Assert.Equal(plan.TotalBytes, outcome.DoneBytes);

        var row = journal.Find(outcome.OpId);
        Assert.NotNull(row);
        Assert.Equal("quarantine", row.Mode);
        Assert.Equal("completed", row.Status);
        Assert.NotNull(row.PurgeAfter);
        Assert.Single(journal.Items(outcome.OpId));

        var manifest = Quarantine.ReadManifest(outcome.OpId, out var error);
        Assert.Null(error);
        Assert.NotNull(manifest);
        Assert.Single(manifest.Items);

        // Restore puts the tree back where it was, contents and all.
        var (restored, failures) = DeleteEngine.Restore(manifest);
        Assert.Empty(failures);
        Assert.Equal(1, restored);
        Assert.True(System.IO.File.Exists(kept));
        Assert.True(System.IO.File.Exists(Path.Combine(_root, "stuff", "nested", "small.bin")));

        // And a second round, purged for real this time.
        var again = engine.Plan(new DeleteRequest { Paths = [Path.Combine(_root, "stuff")] });
        var second = engine.Execute(again, journal);
        var secondManifest = Quarantine.ReadManifest(second.OpId, out _);
        Assert.NotNull(secondManifest);

        var (_, purgeFailures) = engine.Purge(secondManifest);
        Assert.Empty(purgeFailures);

        journal.SetStatus(second.OpId, "purged");
        Assert.Equal("purged", journal.Find(second.OpId)!.Status);
        Assert.False(Directory.Exists(Path.Combine(AppPaths.QuarantineDirectory, Quarantine.FolderName(second.OpId))));
    }

    [Fact]
    public void Restore_refuses_to_overwrite_something_that_took_the_place()
    {
        var path = File_("moved.bin", 128);

        var engine = Engine();
        using var catalog = ScanCatalog.Open();
        var journal = new DeleteRepository(catalog.Database);

        var outcome = engine.Execute(engine.Plan(new DeleteRequest { Paths = [path] }), journal);
        Assert.True(outcome.Status == "completed", Why(outcome));

        // Something else now owns that name.
        System.IO.File.WriteAllText(path, "not the same file");

        var manifest = Quarantine.ReadManifest(outcome.OpId, out _)!;
        var (restored, failures) = DeleteEngine.Restore(manifest);

        Assert.Equal(0, restored);
        Assert.Single(failures);
        Assert.Contains("already exists", failures[0].Why, StringComparison.Ordinal);
        Assert.Equal("not the same file", System.IO.File.ReadAllText(path));
    }

    [Fact]
    public void A_dry_run_moves_nothing_and_stays_out_of_the_operations_journal()
    {
        var path = File_("rehearsal.bin", 2048);

        var engine = Engine();
        var plan = engine.Plan(new DeleteRequest { Paths = [path], DryRun = true });

        using var catalog = ScanCatalog.Open();
        var journal = new DeleteRepository(catalog.Database);
        journal.LogDryRun(plan);

        Assert.True(System.IO.File.Exists(path));
        Assert.Empty(journal.List(10));

        using var command = catalog.Database.Command("SELECT count(*), max(total_bytes) FROM dryrun_log");
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(1, reader.GetInt32(0));
        Assert.Equal(plan.TotalBytes, reader.GetInt64(1));
    }

    // ---- README section 9.3, step 5: verification against the scan ---------------------

    [Fact]
    public void Refuses_a_file_that_changed_since_the_scan()
    {
        var path = File_("changed.bin", 1024);

        // A snapshot that remembers this file as something else. Nothing about the file is
        // wrong; what is wrong is that the user decided using numbers that no longer hold.
        var tree = new TestTree().File(path, 999_999, mtime: 1_700_000_000);
        SnapshotFile.Write(SnapshotStore.PathFor(1), tree.Result(DateTime.UtcNow));

        var plan = Engine().Plan(new DeleteRequest { Paths = [path] });

        Assert.Empty(plan.Items);
        Assert.Single(plan.Refusals);
        Assert.Contains("rescan", plan.Refusals[0].Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Accepts_a_file_the_scan_agrees_about()
    {
        var path = File_("agreed.bin", 1024);

        // Through SnapshotTime, exactly as a scanner writes it: seconds since 2000, not
        // since 1970. Writing a Unix timestamp here once made this test agree with a bug
        // that refused every file the scan knew about (README section 5.3).
        var written = SnapshotTime.FromDateTime(System.IO.File.GetLastWriteTimeUtc(path));

        var tree = new TestTree().File(path, 1024, mtime: written);
        SnapshotFile.Write(SnapshotStore.PathFor(1), tree.Result(DateTime.UtcNow));

        var plan = Engine().Plan(new DeleteRequest { Paths = [path] });

        Assert.Single(plan.Items);
        Assert.Empty(plan.Refusals);
    }

    [Fact]
    public void A_directory_whose_modification_time_moved_is_still_deletable()
    {
        // Every cache the reclaim rules find is written to constantly, so an mtime older
        // than the scan is the normal case, not a warning (README sections 7.3, 9.3).
        File_(@"cache\a.bin", 4096);
        var directory = Path.Combine(_root, "cache");

        var tree = new TestTree().File(Path.Combine(directory, "a.bin"), 4096,
            mtime: SnapshotTime.FromDateTime(DateTime.UtcNow.AddDays(-3)));

        SnapshotFile.Write(SnapshotStore.PathFor(1), tree.Result(DateTime.UtcNow));

        var plan = Engine().Plan(new DeleteRequest { Paths = [directory] });

        Assert.Single(plan.Items);
        Assert.Empty(plan.Refusals);
    }

    [Fact]
    public void A_directory_that_holds_far_more_than_the_scan_measured_is_refused()
    {
        File_(@"grown\big.bin", 8 << 20);
        var directory = Path.Combine(_root, "grown");

        // The scan saw a few kilobytes here; there are eight megabytes now, and the number
        // the user decided with is not the number on the disk.
        var tree = new TestTree().File(Path.Combine(directory, "big.bin"), 4096);
        SnapshotFile.Write(SnapshotStore.PathFor(1), tree.Result(DateTime.UtcNow));

        var plan = Engine().Plan(new DeleteRequest { Paths = [directory] });

        Assert.Empty(plan.Items);
        Assert.Contains("rescan", Assert.Single(plan.Refusals).Reason, StringComparison.Ordinal);
    }

    // ---- README section 9.2: mode selection -------------------------------------------

    [Fact]
    public void Drops_out_of_recycle_mode_rather_than_letting_the_shell_destroy_things()
    {
        File_(@"heavy\a.bin", 4096);

        var config = new AppConfigProbe(recycleMaxBytes: 1);
        var engine = new DeleteEngine(config.Config, new PathGuard(ProtectedSet.Build()));

        var plan = engine.Plan(new DeleteRequest
        {
            Paths = [Path.Combine(_root, "heavy")],
            Mode = DeleteMode.Recycle,
        });

        Assert.Equal(DeleteMode.Quarantine, plan.Mode);
        Assert.NotNull(plan.ModeReason);
        Assert.Contains("recycleMaxBytes", plan.ModeReason, StringComparison.Ordinal);
    }

    [Fact]
    public void Sends_an_item_to_the_Recycle_Bin_through_the_shell()
    {
        // The only way to test IFileOperation is to use it, so this leaves one tiny,
        // obviously named file in the bin. Everything about this path is shell behaviour -
        // the apartment, the sink, the copy engine's own success codes - and a double
        // would prove nothing about any of it (README sections 9.6, 22.2).
        var path = File_("pathmemo-recycle-test.bin", 32);

        var outcome = RecycleBin.Send(
        [
            Engine().Plan(new DeleteRequest { Paths = [path], Mode = DeleteMode.Recycle }).Items.Single(),
        ], CancellationToken.None);

        Assert.Single(outcome);
        Assert.True(outcome[0].Result == ItemResult.Ok,
            $"{outcome[0].Result}: {outcome[0].Message} (0x{outcome[0].Error:X8})");

        Assert.False(System.IO.File.Exists(path));
    }

    [Fact]
    public void Deletes_a_file_whose_size_is_not_a_whole_number_of_clusters()
    {
        // A regression with teeth: the plan carries the on-disk size, rounded up to the
        // cluster, and the re-check at execution time reads the stream length. Comparing
        // the two rejected every file that was not cluster-aligned - which is most of them
        // (README sections 3.1, 9.3).
        var path = File_("odd-size.bin", 3_000_000);

        var engine = Engine();
        using var catalog = ScanCatalog.Open();

        var outcome = engine.Execute(
            engine.Plan(new DeleteRequest { Paths = [path], Mode = DeleteMode.Permanent }),
            new DeleteRepository(catalog.Database));

        Assert.True(outcome.Status == "completed", Why(outcome));
        Assert.False(System.IO.File.Exists(path));
    }

    [Fact]
    public void Permanent_deletion_frees_the_space_it_predicted()
    {
        // 8 MB: big enough that the free-space delta is not lost in the noise of whatever
        // else the machine is doing, small enough not to matter.
        var path = File_("large.bin", 8 << 20);

        var engine = Engine();
        var plan = engine.Plan(new DeleteRequest { Paths = [path], Mode = DeleteMode.Permanent });

        Assert.Equal(DeleteMode.Permanent, plan.Mode);
        Assert.Equal(8 << 20, plan.FreesNow);

        using var catalog = ScanCatalog.Open();
        var outcome = engine.Execute(plan, new DeleteRepository(catalog.Database));

        Assert.True(outcome.Status == "completed", Why(outcome));
        Assert.False(System.IO.File.Exists(path));

        // The measurement is of a live volume, so it is asserted as a direction and an
        // order of magnitude, not as an exact number (README section 9.8).
        Assert.NotNull(outcome.ActualFreedBytes);
        Assert.True(outcome.ActualFreedBytes > 4 << 20,
            $"freed {outcome.ActualFreedBytes} bytes after deleting {plan.TotalBytes}");
    }

    [Fact]
    public void Refuses_to_delete_a_path_twice_over()
    {
        File_(@"nest\inner\file.bin");

        var plan = Engine().Plan(new DeleteRequest
        {
            Paths = [Path.Combine(_root, "nest"), Path.Combine(_root, @"nest\inner")],
        });

        Assert.Single(plan.Items);
        Assert.Single(plan.Refusals);
        Assert.Contains("already covered", plan.Refusals[0].Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// Junctions need no elevation or developer mode, unlike symlinks. Returns false when
    /// the platform refuses anyway, so the test skips instead of failing spuriously.
    /// </summary>
    private static bool TryCreateJunction(string link, string target)
    {
        var psi = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("/c");
        psi.ArgumentList.Add("mklink");
        psi.ArgumentList.Add("/J");
        psi.ArgumentList.Add(link);
        psi.ArgumentList.Add(target);

        using var process = Process.Start(psi);
        if (process is null) return false;
        process.WaitForExit(10_000);
        return process.ExitCode == 0 && Directory.Exists(link);
    }
}

/// <summary>A configuration with one value moved, without a file on disk.</summary>
internal sealed class AppConfigProbe
{
    internal AppConfig Config { get; }

    internal AppConfigProbe(long recycleMaxBytes)
    {
        var path = Path.Combine(Path.GetTempPath(), "pathmemo-cfg-" + Guid.NewGuid().ToString("N")[..8] + ".json");
        System.IO.File.WriteAllText(path, $$"""
            { "delete": { "recycleMaxBytes": {{recycleMaxBytes}} } }
            """);

        try { Config = AppConfig.Load(path); }
        finally { System.IO.File.Delete(path); }
    }
}
