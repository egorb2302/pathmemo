using PathMemo.Platform;
using PathMemo.Scanning;
using PathMemo.Snapshots;
using Xunit;

namespace PathMemo.Tests;

/// <summary>
/// The incremental rebuild against a real directory tree, checked by rescanning it in full
/// (README section 22.2).
/// </summary>
/// <remarks>
/// <para>
/// This is the test that decides whether the feature is trustworthy. The claim an
/// incremental scan makes is not "it is fast" but "it says the same thing a full scan
/// would", and the only way to check that is to do both and compare every path and every
/// byte. The changed set is supplied by hand here rather than by the journal, because
/// reading the journal needs administrator rights while the rebuild - the part that can be
/// wrong in a hundred ways - does not.
/// </para>
/// <para>
/// The directories named are exactly the ones a journal record would name: the parent of
/// anything that was touched. Nothing else is named, so a mistake in "copy the untouched
/// part" shows up as a number that differs from the full scan's.
/// </para>
/// </remarks>
public sealed class IncrementalIntegrationTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "pathmemo-inc-" + Guid.NewGuid().ToString("N")[..12]);

    public IncrementalIntegrationTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task A_rebuild_from_the_changed_directories_says_what_a_full_scan_says()
    {
        Write(@"data\a.bin", 1 << 20);
        Write(@"data\doomed.bin", 4 << 20);
        Write(@"cache\deep\c.bin", 2 << 20);
        Write("root.bin", 512 << 10);

        var before = await FullScanAsync();

        // A file added, a file deleted, a file grown, and a whole subtree moved in from
        // outside - which is the case that emits one record and no contents at all.
        Write(@"data\fresh.bin", 8 << 20);
        File.Delete(Path.Combine(_root, @"data\doomed.bin"));
        Write("root.bin", 3 << 20);

        var staging = Path.Combine(Path.GetTempPath(), "pathmemo-stage-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(staging, "inner"));
        File.WriteAllBytes(Path.Combine(staging, "inner", "s1.bin"), new byte[6 << 20]);
        File.WriteAllBytes(Path.Combine(staging, "s2.bin"), new byte[1 << 20]);
        Directory.Move(staging, Path.Combine(_root, "moved"));

        var rebuilt = Rebuild(before.Tree, Path.Combine(_root, "data"), _root);
        var after = await FullScanAsync();

        Assert.Equal(Paths(after.Tree), Paths(rebuilt));
        Assert.Equal(after.Tree.Allocated[after.Tree.Roots[0]], rebuilt.Allocated[rebuilt.Roots[0]]);
        Assert.Equal(after.Tree.Logical[after.Tree.Roots[0]], rebuilt.Logical[rebuilt.Roots[0]]);
        Assert.Equal(after.Tree.FileCount[after.Tree.Roots[0]], rebuilt.FileCount[rebuilt.Roots[0]]);
    }

    [Fact]
    public async Task A_directory_left_alone_keeps_the_size_the_full_scan_gave_it()
    {
        Write(@"keep\deep\big.bin", 7 << 20);
        Write(@"work\a.bin", 1 << 20);

        var before = await FullScanAsync();
        Write(@"work\b.bin", 2 << 20);

        var rebuilt = Rebuild(before.Tree, Path.Combine(_root, "work"));
        var after = await FullScanAsync();

        var keptBefore = Analysis.TreeQuery.Find(before.Tree, Path.Combine(_root, "keep"));
        var keptAfter = Analysis.TreeQuery.Find(rebuilt, Path.Combine(_root, "keep"));

        Assert.Equal(before.Tree.Allocated[keptBefore], rebuilt.Allocated[keptAfter]);
        Assert.Equal(after.Tree.Allocated[after.Tree.Roots[0]], rebuilt.Allocated[rebuilt.Roots[0]]);
    }

    [Fact]
    public async Task An_empty_directory_that_filled_up_is_read_again()
    {
        Directory.CreateDirectory(Path.Combine(_root, "empty"));
        Write("root.bin", 1 << 20);

        var before = await FullScanAsync();
        Write(@"empty\now.bin", 5 << 20);

        var rebuilt = Rebuild(before.Tree, Path.Combine(_root, "empty"));
        var after = await FullScanAsync();

        Assert.Equal(after.Tree.Allocated[after.Tree.Roots[0]], rebuilt.Allocated[rebuilt.Roots[0]]);
        Assert.Contains("now.bin", Paths(rebuilt).Select(Path.GetFileName));
    }

    [Fact]
    public async Task A_re_read_file_gets_its_allocated_size_from_the_same_place_a_full_scan_does()
    {
        // Not "logical rounded up to a cluster": a one-byte file's data lives inside its own
        // MFT record and occupies no clusters at all, while a compressible one occupies
        // fewer than its length (README sections 3.1, 4.3). The only correct claim is that
        // the rebuild reports what a full scan of the same file reports.
        Write(@"work\tiny.bin", 1);

        var before = await FullScanAsync();
        Write(@"work\tiny2.bin", 1);
        Write(@"work\large.bin", 3 << 20);

        var rebuilt = Rebuild(before.Tree, Path.Combine(_root, "work"));
        var after = await FullScanAsync();

        foreach (var name in new[] { "tiny.bin", "tiny2.bin", "large.bin" })
        {
            var path = Path.Combine(_root, "work", name);
            var mine = Analysis.TreeQuery.Find(rebuilt, path);
            var theirs = Analysis.TreeQuery.Find(after.Tree, path);

            Assert.NotEqual(NodeStore.NoNode, mine);
            Assert.Equal(after.Tree.Logical[theirs], rebuilt.Logical[mine]);
            Assert.Equal(after.Tree.Allocated[theirs], rebuilt.Allocated[mine]);
        }
    }

    [Fact]
    public async Task Every_node_of_a_rebuilt_tree_still_has_its_children_in_one_range()
    {
        Write(@"a\b\c\deep.bin", 1 << 20);
        Write(@"a\sibling.bin", 1 << 20);
        Write(@"z\other.bin", 1 << 20);

        var before = await FullScanAsync();
        Write(@"a\b\added.bin", 1 << 20);

        var rebuilt = Rebuild(before.Tree, Path.Combine(_root, @"a\b"));

        for (var node = 0; node < rebuilt.Count; node++)
        {
            for (var c = rebuilt.FirstChild[node]; c < rebuilt.FirstChild[node] + rebuilt.ChildCount[node]; c++)
            {
                Assert.Equal(node, rebuilt.Parent[c]);
                Assert.True(c > node);
            }
        }

        // And the tree is still navigable by path, which is what the contiguity is for.
        Assert.NotEqual(NodeStore.NoNode, Analysis.TreeQuery.Find(rebuilt, Path.Combine(_root, @"a\b\c\deep.bin")));
    }

    private NodeStore Rebuild(NodeStore baseline, params string[] changed)
    {
        var lister = new DirectoryLister(new NameBlobBuilder(1 << 12), 0, resolveAllocatedSize: true);
        var errors = new List<ScanError>();

        var set = changed.Select(IncrementalTree.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var tree = IncrementalTree.Rebuild(baseline, set, UsnIncrementalScanner.Reader(lister), errors, default);

        Assert.Empty(errors);
        return tree;
    }

    private async Task<ScanResult> FullScanAsync()
    {
        var volume = VolumeInfo.Enumerate()
            .First(v => _root.StartsWith(v.Root, StringComparison.OrdinalIgnoreCase));

        var root = _root.EndsWith(Path.DirectorySeparatorChar) ? _root : _root + Path.DirectorySeparatorChar;
        var scoped = volume with { Root = root };

        return await new WalkScanner().ScanAsync(
            new ScanRequest { Roots = [root], Parallelism = 1 }, [scoped], null, default);
    }

    private void Write(string relative, int bytes)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
    }

    /// <summary>Every path in the tree, sorted, so two trees can be compared as sets.</summary>
    private static List<string> Paths(NodeStore tree)
    {
        var paths = new List<string>(tree.Count);
        for (var i = 0; i < tree.Count; i++) paths.Add(tree.GetPath(i));

        paths.Sort(StringComparer.OrdinalIgnoreCase);
        return paths;
    }
}
