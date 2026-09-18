using System.Runtime.InteropServices;
using PathMemo.Config;
using PathMemo.Duplicates;
using PathMemo.Snapshots;
using PathMemo.Storage;
using Xunit;

namespace PathMemo.Tests;

/// <summary>
/// The duplicate finder against real files (README sections 8, 22.2).
/// </summary>
/// <remarks>
/// <para>
/// Real files, because everything interesting here is filesystem behaviour: hard links
/// share a file id, a hash comes off a handle, and a file that changes between the search
/// and the deletion is the case the whole module is built around. A fake filesystem would
/// only assert that the fake behaves like the fake.
/// </para>
/// <para>
/// The tree is synthetic and the files are real: the finder takes a snapshot and reads the
/// paths in it, so a test states which files exist by creating them and states what the
/// scan saw by listing them.
/// </para>
/// </remarks>
public sealed partial class DuplicateTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "pathmemo-dupes-" + Guid.NewGuid().ToString("N")[..12]);

    private readonly string _dataDirectory = Path.Combine(
        Path.GetTempPath(), "pathmemo-dupedb-" + Guid.NewGuid().ToString("N")[..12]);

    public DuplicateTests()
    {
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_dataDirectory);
        AppPaths.Redirect(_dataDirectory);
        AppConfig.Reset();
    }

    public void Dispose()
    {
        Delete(_root);
        Delete(_dataDirectory);
        AppConfig.Reset();
    }

    private static void Delete(string directory)
    {
        try { Directory.Delete(directory, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException) { }
    }

    [Fact]
    public void Three_copies_of_one_file_are_one_group_that_can_give_back_two()
    {
        var a = Write(@"photos\holiday.jpg", Pattern(3 << 20, seed: 1));
        var b = Write(@"backup\holiday.jpg", Pattern(3 << 20, seed: 1));
        var c = Write(@"Downloads\holiday.jpg", Pattern(3 << 20, seed: 1));

        var report = Find(a, b, c);

        var group = Assert.Single(report.Duplicates);
        Assert.Equal(3, group.Count);
        Assert.Equal(2L * (3 << 20), group.Wasted);
        Assert.Equal(2, group.Victims.Count());
    }

    [Fact]
    public void Two_files_of_the_same_size_that_differ_are_not_a_group()
    {
        var a = Write("one.bin", Pattern(2 << 20, seed: 1));
        var b = Write("two.bin", Pattern(2 << 20, seed: 2));

        var report = Find(a, b);

        Assert.Empty(report.Groups);
        Assert.Equal(2, report.Stats.Candidates);
    }

    [Fact]
    public void A_file_whose_size_nothing_shares_is_never_even_opened()
    {
        var a = Write("one.bin", Pattern(2 << 20, seed: 1));
        var b = Write("two.bin", Pattern((2 << 20) + 4096, seed: 1));

        var report = Find(a, b);

        Assert.Empty(report.Groups);
        Assert.Equal(0, report.Stats.Candidates);
        Assert.Equal(0, report.Stats.Opened);
    }

    [Fact]
    public void A_hard_link_set_is_shown_apart_from_duplicates_and_frees_nothing()
    {
        var real = Write(@"store\package.bin", Pattern(2 << 20, seed: 7));
        var link = Path.Combine(_root, @"project\node_modules\package.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);

        Assert.True(CreateHardLink(link, real, 0), "the test filesystem does not support hard links");

        var report = Find(real, link);

        var group = Assert.Single(report.Groups);
        Assert.Equal(DupeKind.HardlinkSet, group.Kind);
        Assert.Equal(0, group.Wasted);
        Assert.Empty(group.Victims);
        Assert.Empty(report.Duplicates);
    }

    [Fact]
    public void A_real_copy_beside_a_hard_link_set_is_still_a_duplicate()
    {
        var real = Write(@"store\package.bin", Pattern(2 << 20, seed: 7));
        var link = Path.Combine(_root, @"store\alias.bin");
        Assert.True(CreateHardLink(link, real, 0));

        var copy = Write(@"other\package.bin", Pattern(2 << 20, seed: 7));

        var report = Find(real, link, copy);

        Assert.Single(report.Hardlinks);

        var duplicate = Assert.Single(report.Duplicates);
        Assert.Equal(2, duplicate.Count);
        Assert.Equal(2L << 20, duplicate.Wasted);

        // The linked file survives and the standalone copy is the one on offer: keeping the
        // linked one costs nothing, and deleting a name of it would free nothing (README 8.4).
        Assert.True(duplicate.Keeper.LinkCount > 1);
        Assert.Equal(copy, Assert.Single(duplicate.Victims).Path, ignoreCase: true);
    }

    [Fact]
    public void A_file_with_several_names_is_the_survivor_and_never_a_copy_to_delete()
    {
        var group = new DupeGroup
        {
            Kind = DupeKind.Duplicate,
            Bytes = 2 << 20,
            Files = Keeper.Mark(
            [
                new DupeFile { Path = @"C:\one.bin", Bytes = 2 << 20, Allocated = 2 << 20 },
                new DupeFile { Path = @"C:\deep\two.bin", Bytes = 2 << 20, Allocated = 2 << 20, LinkCount = 3 },
            ], []),
        };

        Assert.Equal(@"C:\deep\two.bin", group.Keeper.Path);
        Assert.Equal("3 names for this file", group.Keeper.Why);
        Assert.Equal(@"C:\one.bin", Assert.Single(group.Victims).Path);
    }

    [Fact]
    public void Re_verification_refuses_a_copy_that_grew_a_second_name()
    {
        var keeper = Write(@"a\shot.jpg", Pattern(2 << 20, seed: 3));
        var victim = Write(@"b\shot.jpg", Pattern(2 << 20, seed: 3));

        var group = Assert.Single(Find(keeper, victim).Duplicates);
        var marked = new HashSet<string>([victim], StringComparer.OrdinalIgnoreCase);

        Assert.True(DuplicateFinder.Reverify([group], marked, new DupeQuery(), null, out _));

        Assert.True(CreateHardLink(Path.Combine(_root, @"b\also.jpg"), victim, 0));

        Assert.False(DuplicateFinder.Reverify([group], marked, new DupeQuery(), null, out var complaint));
        Assert.Contains("frees nothing", complaint, StringComparison.Ordinal);
    }

    /// <remarks>
    /// Over paths rather than files: everything this class writes lives under <c>%TEMP%</c>,
    /// which is itself one of the places the rule calls throwaway, so a real-file version of
    /// this test could only ever assert the tie-break underneath it.
    /// </remarks>
    [Fact]
    public void The_copy_in_Downloads_is_not_the_one_that_survives()
    {
        var group = Choose(
            @"C:\Users\me\Downloads\shot.jpg",
            @"C:\Users\me\Pictures\2024\summer\shot.jpg");

        Assert.EndsWith(@"summer\shot.jpg", group.Keeper.Path, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("outside Temp and Downloads", group.Keeper.Why);
        Assert.EndsWith(@"Downloads\shot.jpg", Assert.Single(group.Victims).Path,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Between_two_ordinary_copies_the_shallower_path_survives()
    {
        var group = Choose(@"C:\photos\shot.jpg", @"C:\work\site\assets\img\shot.jpg");

        Assert.Equal(@"C:\photos\shot.jpg", group.Keeper.Path);
    }

    [Fact]
    public void A_keep_listed_path_wins_the_survivor_slot_even_from_Downloads()
    {
        var group = Choose([@"C:\shot.jpg", @"C:\Users\me\Downloads\shot.jpg"],
            keep: [@"C:\Users\me\Downloads\shot.jpg"]);

        Assert.EndsWith(@"Downloads\shot.jpg", group.Keeper.Path, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("protect.keep", group.Keeper.Why);
    }

    [Fact]
    public void A_keep_listed_copy_that_is_not_the_survivor_is_still_never_offered()
    {
        var group = Choose(
            [@"C:\a\shot.jpg", @"C:\b\shot.jpg", @"C:\c\shot.jpg"],
            keep: [@"C:\b\shot.jpg", @"C:\c\shot.jpg"]);

        Assert.Equal(@"C:\b\shot.jpg", group.Keeper.Path);
        Assert.True(group.Files.Single(f => f.Path == @"C:\c\shot.jpg").Protected);
        Assert.Equal(@"C:\a\shot.jpg", Assert.Single(group.Victims).Path);
    }

    [Fact]
    public void A_keep_listed_copy_is_the_one_the_search_keeps()
    {
        var first = Write(@"a\shot.jpg", Pattern(2 << 20, seed: 3));
        var second = Write(@"b\shot.jpg", Pattern(2 << 20, seed: 3));
        var third = Write(@"c\shot.jpg", Pattern(2 << 20, seed: 3));

        var group = Assert.Single(Find([first, second, third], keep: [second]).Duplicates);

        Assert.Equal(second, group.Keeper.Path, ignoreCase: true);
        Assert.DoesNotContain(second, group.Victims.Select(v => v.Path), StringComparer.OrdinalIgnoreCase);

        // Two ordinary copies of a file the user has claimed: both go, the claimed one stays.
        Assert.Equal(4L << 20, group.Wasted);
    }

    [Fact]
    public void The_last_copy_of_a_group_cannot_be_marked()
    {
        var a = Write(@"a\shot.jpg", Pattern(2 << 20, seed: 3));
        var b = Write(@"b\shot.jpg", Pattern(2 << 20, seed: 3));

        var group = Assert.Single(Find(a, b).Duplicates);

        Assert.True(Keeper.Survives([group], new HashSet<string>([b], StringComparer.OrdinalIgnoreCase), out _));

        Assert.False(Keeper.Survives(
            [group], new HashSet<string>([a, b], StringComparer.OrdinalIgnoreCase), out var complaint));

        Assert.Contains("must keep one", complaint, StringComparison.Ordinal);
    }

    [Fact]
    public void Marking_a_kept_path_is_refused_by_name()
    {
        var a = Write(@"a\shot.jpg", Pattern(2 << 20, seed: 3));
        var b = Write(@"b\shot.jpg", Pattern(2 << 20, seed: 3));

        var group = Assert.Single(Find([a, b], keep: [b]).Duplicates);

        Assert.False(Keeper.Survives(
            [group], new HashSet<string>([b], StringComparer.OrdinalIgnoreCase), out var complaint));

        Assert.Contains("keep list", complaint, StringComparison.Ordinal);
    }

    [Fact]
    public void Byte_for_byte_comparison_splits_files_that_a_hash_agreed_about()
    {
        // Stage 4 is given the group stage 3 would have produced if the hash had collided.
        // A real collision cannot be manufactured, and the code path can: this is what it
        // does when it happens (README section 8.1).
        var same = new[]
        {
            Write(@"x\one.bin", Pattern(1 << 20, seed: 4)),
            Write(@"x\two.bin", Pattern(1 << 20, seed: 4)),
        };

        var other = Write(@"x\three.bin", Pattern(1 << 20, seed: 5));

        var files = same.Append(other)
            .Select(p => new DupeFile { Path = p, Bytes = 1 << 20, Allocated = 1 << 20 })
            .ToList();

        var stats = new DupeStats();
        var classes = ByteComparer.Partition(files, new DupeQuery(), stats, CancellationToken.None);

        Assert.Equal(2, classes.Count);
        Assert.Equal(2, classes[0].Count);
        Assert.Single(classes[1]);
        Assert.Equal(1, stats.Impostors);
    }

    [Fact]
    public void Re_verification_refuses_the_whole_operation_when_a_copy_changed()
    {
        var keeper = Write(@"a\shot.jpg", Pattern(2 << 20, seed: 3));
        var victim = Write(@"b\shot.jpg", Pattern(2 << 20, seed: 3));

        var group = Assert.Single(Find(keeper, victim).Duplicates);
        var marked = new HashSet<string>([victim], StringComparer.OrdinalIgnoreCase);

        Assert.True(DuplicateFinder.Reverify([group], marked, new DupeQuery(), null, out _));

        File.WriteAllBytes(victim, Pattern(2 << 20, seed: 9));

        Assert.False(DuplicateFinder.Reverify([group], marked, new DupeQuery(), null, out var complaint));
        Assert.Contains(victim, complaint, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Re_verification_refuses_to_delete_a_name_from_a_hard_link_set()
    {
        var real = Write(@"store\package.bin", Pattern(2 << 20, seed: 7));
        var link = Path.Combine(_root, @"store\alias.bin");
        Assert.True(CreateHardLink(link, real, 0));

        var group = Assert.Single(Find(real, link).Hardlinks);
        var marked = new HashSet<string>([link], StringComparer.OrdinalIgnoreCase);

        Assert.False(DuplicateFinder.Reverify([group], marked, new DupeQuery(), null, out var complaint));
        Assert.Contains("frees nothing", complaint, StringComparison.Ordinal);
    }

    [Fact]
    public void The_second_run_takes_its_hashes_from_the_cache_instead_of_the_disk()
    {
        var a = Write(@"a\big.bin", Pattern(4 << 20, seed: 11));
        var b = Write(@"b\big.bin", Pattern(4 << 20, seed: 11));

        using var database = Database.Open(Path.Combine(_dataDirectory, "cache.db"));
        var cache = new HashCache(database, HashKind.Xxh128);

        var first = Find([a, b], cache: cache);
        var second = Find([a, b], cache: cache);

        Assert.Single(first.Duplicates);
        Assert.Single(second.Duplicates);

        Assert.Equal(0, first.Stats.FromCache);
        Assert.Equal(2, second.Stats.FromCache);
        Assert.True(second.Stats.BytesRead < first.Stats.BytesRead,
            $"the second run read {second.Stats.BytesRead} against {first.Stats.BytesRead}");
    }

    [Fact]
    public void A_cloud_only_file_is_never_opened()
    {
        // The paths do not exist: if the finder opened them it would count them as
        // unreadable, and the assertion below would fail.
        var tree = new TestTree()
            .File(@"C:\OneDrive\a\report.docx", 8 << 20, NodeFlags.CloudOnly)
            .File(@"C:\OneDrive\b\report.docx", 8 << 20, NodeFlags.CloudOnly)
            .Build();

        var report = DuplicateFinder.Find(tree, 1, Query([]), CancellationToken.None);

        Assert.Empty(report.Groups);
        Assert.Equal(2, report.Stats.CloudSkipped);
        Assert.Equal(0, report.Stats.Opened);
        Assert.Equal(0, report.Stats.Unreadable);
    }

    [Fact]
    public void Files_below_the_floor_and_outside_the_extensions_never_reach_the_disk()
    {
        var a = Write(@"a\clip.mp4", Pattern(2 << 20, seed: 12));
        var b = Write(@"b\clip.mp4", Pattern(2 << 20, seed: 12));
        var c = Write(@"a\notes.txt", Pattern(4096, seed: 13));
        var d = Write(@"b\notes.txt", Pattern(4096, seed: 13));

        Assert.Single(Find([a, b, c, d], query: q => q with { MinBytes = 1 << 20 }).Duplicates);

        var text = Find([a, b, c, d], query: q => q with
        {
            MinBytes = 1024,
            Extensions = new HashSet<string>(["txt"], StringComparer.Ordinal),
        }).Duplicates.ToList();

        Assert.DoesNotContain(text, g => g.Bytes > 1 << 20);
        Assert.Single(text);
    }

    [Fact]
    public void Only_what_is_under_the_given_path_is_compared()
    {
        var a = Write(@"a\clip.mp4", Pattern(2 << 20, seed: 12));
        var b = Write(@"b\clip.mp4", Pattern(2 << 20, seed: 12));

        Assert.Single(Find([a, b]).Duplicates);

        Assert.Empty(Find([a, b], query: q => q with { Under = Path.Combine(_root, "a") }).Duplicates);
    }

    [Fact]
    public void Files_on_two_volumes_are_matched_unless_the_search_is_told_not_to()
    {
        // The snapshot's volumes, not the disk's: a machine with one drive must still be
        // able to prove that --same-volume does something (README section 8.4).
        var tree = new TestTree()
            .File(@"C:\photos\shot.jpg", 8 << 20)
            .File(@"D:\backup\shot.jpg", 8 << 20)
            .Build();

        var across = DuplicateFinder.Find(tree, 1, Query([]), CancellationToken.None);
        var within = DuplicateFinder.Find(
            tree, 1, Query([]) with { CrossVolume = false }, CancellationToken.None);

        // Neither path exists, so nothing is ever grouped - but the candidate count says
        // whether the two files were even considered against each other.
        Assert.Equal(2, across.Stats.Candidates);
        Assert.Equal(0, within.Stats.Candidates);
    }

    [Fact]
    public void The_run_is_stored_and_comes_back_with_its_totals()
    {
        var a = Write(@"a\shot.jpg", Pattern(2 << 20, seed: 3));
        var b = Write(@"b\shot.jpg", Pattern(2 << 20, seed: 3));

        var report = Find(a, b);

        using var database = Database.Open(Path.Combine(_dataDirectory, "runs.db"));
        var repository = new DupeRepository(database);

        // The run belongs to a scan row; this test has none, and a run without one is legal.
        repository.Save(report with { ScanId = 0 });
        var stored = repository.Latest();

        Assert.NotNull(stored);
        var group = Assert.Single(stored.Duplicates);
        Assert.Equal(report.Wasted, stored.Wasted);
        Assert.Equal(2, group.Count);
        Assert.Equal(a, group.Keeper.Path, ignoreCase: true);
    }

    [Fact]
    public void Saving_a_run_replaces_the_one_before_it()
    {
        var a = Write(@"a\shot.jpg", Pattern(2 << 20, seed: 3));
        var b = Write(@"b\shot.jpg", Pattern(2 << 20, seed: 3));

        using var database = Database.Open(Path.Combine(_dataDirectory, "runs.db"));
        var repository = new DupeRepository(database);

        repository.Save(Find(a, b) with { ScanId = 0 });
        repository.Save(Find(a, b) with { ScanId = 0 });

        using var count = database.Command("SELECT COUNT(*) FROM dupe_runs;");
        Assert.Equal(1L, Convert.ToInt64(count.ExecuteScalar()));
    }

    [Fact]
    public void A_cancelled_run_says_its_totals_are_a_floor()
    {
        var a = Write(@"a\shot.jpg", Pattern(2 << 20, seed: 3));
        var b = Write(@"b\shot.jpg", Pattern(2 << 20, seed: 3));

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var report = DuplicateFinder.Find(Tree(a, b), 1, Query([]), cancellation.Token);

        Assert.True(report.Partial);
        Assert.Empty(report.Groups);
    }

    [Fact]
    public void Declining_the_read_notice_reads_nothing_at_all()
    {
        var a = Write(@"a\shot.jpg", Pattern(2 << 20, seed: 3));
        var b = Write(@"b\shot.jpg", Pattern(2 << 20, seed: 3));

        var report = DuplicateFinder.Find(
            Tree(a, b), 1, Query([]) with { Confirm = (_, _) => DupeConsent.Cancel }, CancellationToken.None);

        Assert.True(report.Declined);
        Assert.Equal(0, report.Stats.Opened);
        Assert.Empty(report.Groups);
    }

    [Fact]
    public void The_notice_is_given_the_bytes_the_run_is_about_to_read()
    {
        var a = Write(@"a\shot.jpg", Pattern(2 << 20, seed: 3));
        var b = Write(@"b\shot.jpg", Pattern(2 << 20, seed: 3));

        long announced = 0;
        var files = 0;

        DuplicateFinder.Find(Tree(a, b), 1, Query([]) with
        {
            Confirm = (bytes, count) => { announced = bytes; files = count; return DupeConsent.Continue; },
        }, CancellationToken.None);

        Assert.Equal(4L << 20, announced);
        Assert.Equal(2, files);
    }

    [Fact]
    public void The_duplicates_section_of_the_configuration_is_read()
    {
        var path = Path.Combine(_dataDirectory, "config.json");
        File.WriteAllText(path, """
            {
              "duplicates": {
                "minSize": "64MB",
                "hashAlgorithm": "sha256",
                "byteForByteVerify": false,
                "crossVolume": false,
                "hashCacheMaxEntries": 5000
              }
            }
            """);

        var config = AppConfig.Load(path);

        Assert.Equal(64L << 20, config.Duplicates.MinSize);
        Assert.Equal(HashKind.Sha256, config.Duplicates.HashAlgorithm);
        Assert.False(config.Duplicates.ByteForByteVerify);
        Assert.False(config.Duplicates.CrossVolume);
        Assert.Equal(5000, config.Duplicates.HashCacheMaxEntries);
        Assert.Empty(config.Warnings);
    }

    [Fact]
    public void An_unknown_hash_is_a_warning_and_the_default_stands()
    {
        var path = Path.Combine(_dataDirectory, "config.json");
        File.WriteAllText(path, """{ "duplicates": { "hashAlgorithm": "blake3" } }""");

        var config = AppConfig.Load(path);

        Assert.Equal(HashKind.Xxh128, config.Duplicates.HashAlgorithm);
        Assert.Contains(config.Warnings, w => w.Contains("blake3", StringComparison.Ordinal));
    }

    [Fact]
    public void Sha256_finds_the_same_groups_as_the_default_hash()
    {
        var a = Write(@"a\shot.jpg", Pattern(2 << 20, seed: 3));
        var b = Write(@"b\shot.jpg", Pattern(2 << 20, seed: 3));

        var fast = Assert.Single(Find([a, b]).Duplicates);
        var crypto = Assert.Single(
            Find([a, b], query: q => q with { Algorithm = HashKind.Sha256 }).Duplicates);

        Assert.Equal(fast.Count, crypto.Count);
        Assert.NotEqual(fast.Hash, crypto.Hash);
    }

    // ---- helpers ----

    /// <summary>Runs the survivor rules over paths alone, with no disk behind them.</summary>
    private static DupeGroup Choose(params string[] paths) => Choose(paths, keep: []);

    private static DupeGroup Choose(string[] paths, IReadOnlyList<string> keep) => new()
    {
        Kind = DupeKind.Duplicate,
        Bytes = 2 << 20,
        Files = Keeper.Mark(
            [.. paths.Select(p => new DupeFile { Path = p, Bytes = 2 << 20, Allocated = 2 << 20 })],
            [.. keep.Select(PathGlob.Parse)]),
    };

    private DupeReport Find(params string[] paths) => Find(paths, keep: null);

    private DupeReport Find(
        string[] paths,
        IReadOnlyList<string>? keep = null,
        HashCache? cache = null,
        Func<DupeQuery, DupeQuery>? query = null)
    {
        var request = Query(keep ?? []) with { Cache = cache };
        if (query is not null) request = query(request);

        return DuplicateFinder.Find(Tree(paths), 1, request, CancellationToken.None);
    }

    /// <summary>One thread and no medium detection: a test asserts behaviour, not throughput.</summary>
    private static DupeQuery Query(IReadOnlyList<string> keep) => new()
    {
        MinBytes = 1 << 20,
        Keep = keep,
        Parallelism = 1,
    };

    private NodeStore Tree(params string[] paths)
    {
        var tree = new TestTree();

        foreach (var path in paths)
        {
            var info = new FileInfo(path);
            tree.File(path, info.Length, mtime: SnapshotTime.FromDateTime(info.LastWriteTimeUtc));
        }

        return tree.Build();
    }

    private string Write(string relative, byte[] contents)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, contents);
        return path;
    }

    /// <summary>
    /// Bytes that differ from seed to seed in the middle as well as at the ends, so a test
    /// cannot pass because two files happen to share their first and last 64 KB.
    /// </summary>
    private static byte[] Pattern(int length, int seed)
    {
        var bytes = new byte[length];
        var random = new Random(seed);
        random.NextBytes(bytes);
        return bytes;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateHardLinkW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateHardLink(string lpFileName, string lpExistingFileName, nint attributes);
}
