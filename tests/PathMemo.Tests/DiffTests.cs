using PathMemo.Analysis;
using PathMemo.Scanning;
using Xunit;

namespace PathMemo.Tests;

/// <summary>
/// Comparing two snapshots (README section 10.2).
/// </summary>
public sealed class DiffTests
{
    private static readonly DateTime Monday = new(2026, 9, 10, 3, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Thursday = new(2026, 9, 17, 3, 0, 0, DateTimeKind.Utc);

    private const long Gb = 1L << 30;

    [Fact]
    public void Reports_the_deepest_directory_that_explains_the_change()
    {
        // 12 GB of Docker becomes 22 GB. Users, me and AppData all grew by the same
        // 10 GB, and naming any of them would be true and useless.
        var before = new TestTree()
            .File(@"C:\Users\me\AppData\Local\Docker\disk.vhdx", 12 * Gb)
            .File(@"C:\Users\me\Documents\notes.txt", 4096)
            .Snapshot(Monday);

        var after = new TestTree()
            .File(@"C:\Users\me\AppData\Local\Docker\disk.vhdx", 22 * Gb)
            .File(@"C:\Users\me\Documents\notes.txt", 4096)
            .Snapshot(Thursday);

        var diff = SnapshotDiff.Compare(41, before, 43, after);
        var volume = Assert.Single(diff.Volumes);

        Assert.Equal(10 * Gb, volume.Delta);
        var row = Assert.Single(volume.Grew);
        Assert.Equal(@"C:\Users\me\AppData\Local\Docker\disk.vhdx", row.Path);
        Assert.Equal(DiffRowKind.File, row.Kind);
        Assert.Equal(12 * Gb, row.Before);
        Assert.Equal(22 * Gb, row.After);
        Assert.Empty(volume.Shrank);
    }

    [Fact]
    public void Splits_the_report_when_several_children_grew()
    {
        var before = new TestTree()
            .File(@"C:\a\one.bin", 10 * Gb)
            .File(@"C:\b\two.bin", 10 * Gb)
            .Snapshot(Monday);

        var after = new TestTree()
            .File(@"C:\a\one.bin", 14 * Gb)
            .File(@"C:\b\two.bin", 15 * Gb)
            .Snapshot(Thursday);

        var diff = SnapshotDiff.Compare(1, before, 2, after);
        var volume = Assert.Single(diff.Volumes);

        Assert.Equal(9 * Gb, volume.Delta);
        Assert.Equal(2, volume.Grew.Count);
        Assert.Equal([@"C:\b\two.bin", @"C:\a\one.bin"], volume.Grew.Select(r => r.Path));
        Assert.Equal(9 * Gb, volume.GrewBytes);
    }

    [Fact]
    public void Rows_add_up_to_the_volume_change()
    {
        var before = new TestTree()
            .File(@"C:\downloads\iso.img", 6 * Gb)
            .File(@"C:\games\data.pak", 20 * Gb)
            .Snapshot(Monday);

        var after = new TestTree()
            .File(@"C:\games\data.pak", 20 * Gb)
            .File(@"C:\games\patch.pak", 5 * Gb)
            .File(@"C:\projects\node_modules\big.js", 2 * Gb)
            .Snapshot(Thursday);

        var diff = SnapshotDiff.Compare(1, before, 2, after);
        var volume = Assert.Single(diff.Volumes);

        // The property that makes the report trustworthy: every listed row is disjoint,
        // and together they are the whole change (README principle P1).
        Assert.Equal(volume.Delta, volume.GrewBytes + volume.ShrankBytes);
        Assert.Equal(0, volume.Unexplained);
    }

    [Fact]
    public void Change_too_small_to_list_is_reported_as_unexplained()
    {
        // One file grows by 2 GB next to 50 files growing by 1 MB each. Against a 2 GB
        // change the 50 MB is below the threshold at every level, so no row carries it -
        // and the report names the remainder instead of quietly losing it.
        var before = new TestTree().File(@"C:\mix\big.bin", Gb);
        var after = new TestTree().File(@"C:\mix\big.bin", 3 * Gb);
        for (var i = 0; i < 50; i++)
        {
            before.File($@"C:\mix\noise{i}.bin", 1L << 20);
            after.File($@"C:\mix\noise{i}.bin", 2L << 20);
        }

        var volume = Assert.Single(
            SnapshotDiff.Compare(1, before.Snapshot(Monday), 2, after.Snapshot(Thursday)).Volumes);

        Assert.Equal(2 * Gb + (50L << 20), volume.Delta);
        Assert.Equal(@"C:\mix\big.bin", Assert.Single(volume.Grew).Path);
        Assert.Equal(2 * Gb, volume.GrewBytes);
        Assert.Equal(50L << 20, volume.Unexplained);
        Assert.Equal(volume.Delta, volume.GrewBytes + volume.ShrankBytes + volume.Unexplained);
    }

    [Fact]
    public void Collects_many_small_changes_as_one_residual_row()
    {
        var before = new TestTree();
        var after = new TestTree();
        for (var i = 0; i < 400; i++)
        {
            before.File($@"C:\Windows\SoftwareDistribution\p{i}.cab", 20L << 20);
            after.File($@"C:\Windows\SoftwareDistribution\p{i}.cab", 40L << 20);
        }

        var diff = SnapshotDiff.Compare(1, before.Snapshot(Monday), 2, after.Snapshot(Thursday));
        var volume = Assert.Single(diff.Volumes);

        // Nothing below it is worth naming, so the directory itself is the answer.
        var row = Assert.Single(volume.Grew);
        Assert.Equal(DiffRowKind.Directory, row.Kind);
        Assert.Equal(@"C:\Windows\SoftwareDistribution", row.Path);
        Assert.Equal(400 * (20L << 20), row.Delta);
        Assert.Equal(400 * (20L << 20), row.Before);
    }

    [Fact]
    public void Names_the_big_child_and_keeps_the_rest_as_a_residual()
    {
        var before = new TestTree().File(@"C:\mix\big.bin", Gb);
        var after = new TestTree().File(@"C:\mix\big.bin", 11 * Gb);
        for (var i = 0; i < 300; i++)
        {
            before.File($@"C:\mix\small{i}.bin", 1L << 20);
            after.File($@"C:\mix\small{i}.bin", 21L << 20);
        }

        var diff = SnapshotDiff.Compare(1, before.Snapshot(Monday), 2, after.Snapshot(Thursday));
        var volume = Assert.Single(diff.Volumes);

        Assert.Equal(2, volume.Grew.Count);
        Assert.Equal(@"C:\mix\big.bin", volume.Grew[0].Path);
        Assert.Equal(10 * Gb, volume.Grew[0].Delta);

        var residual = volume.Grew[1];
        Assert.Equal(DiffRowKind.Residual, residual.Kind);
        Assert.Equal(@"C:\mix", residual.Path);
        Assert.Equal(300 * (20L << 20), residual.Delta);
        Assert.Equal(volume.Delta, volume.GrewBytes);
    }

    [Fact]
    public void Counts_what_appeared_and_disappeared_and_names_the_largest_new_file()
    {
        var before = new TestTree()
            .File(@"C:\keep\stay.bin", 30 * Gb)
            .File(@"C:\old\gone.bin", 3 * Gb)
            .Snapshot(Monday);

        var after = new TestTree()
            .File(@"C:\keep\stay.bin", 30 * Gb)
            .File(@"C:\Users\me\Downloads\Win11_24H2.iso", 5 * Gb)
            .File(@"C:\Users\me\Downloads\small.txt", 200)
            .Snapshot(Thursday);

        var diff = SnapshotDiff.Compare(1, before, 2, after);
        var volume = Assert.Single(diff.Volumes);

        Assert.Equal(5 * Gb + 200, volume.Appeared.Bytes);
        Assert.Equal(1, volume.Appeared.Paths);                 // one new top-level entry: Users
        Assert.Equal(@"C:\Users\me\Downloads\Win11_24H2.iso", volume.Appeared.LargestFile);
        Assert.Equal(5 * Gb, volume.Appeared.LargestFileBytes);

        Assert.Equal(3 * Gb, volume.Disappeared.Bytes);
        Assert.Equal(@"C:\old\gone.bin", volume.Disappeared.LargestFile);

        var gone = Assert.Single(volume.Shrank);
        Assert.Equal(@"C:\old", gone.Path);
        Assert.Null(gone.After);
    }

    [Fact]
    public void Ignores_changes_below_the_threshold()
    {
        var before = new TestTree().File(@"C:\data\file.bin", 10 * Gb).Snapshot(Monday);
        var after = new TestTree().File(@"C:\data\file.bin", 10 * Gb + (32L << 20)).Snapshot(Thursday);

        var diff = SnapshotDiff.Compare(1, before, 2, after);
        var volume = Assert.Single(diff.Volumes);

        // 32 MB is below the 64 MB floor, so nothing is worth a line.
        Assert.Empty(volume.Grew);
        Assert.Empty(volume.Shrank);
        Assert.Equal(32L << 20, volume.Delta);
    }

    [Fact]
    public void Scales_the_threshold_to_the_size_of_the_change()
    {
        // 1% of a 100 GB change is 1 GB, so a 100 MB child is noise next to it. The same
        // 100 MB is the whole story when nothing else moved.
        var beforeBig = new TestTree()
            .File(@"C:\d\bulk.bin", 10 * Gb)
            .File(@"C:\d\little.bin", 1L << 20)
            .Snapshot(Monday);

        var afterBig = new TestTree()
            .File(@"C:\d\bulk.bin", 110 * Gb)
            .File(@"C:\d\little.bin", 101L << 20)
            .Snapshot(Thursday);

        var big = SnapshotDiff.Compare(1, beforeBig, 2, afterBig).Volumes.Single();
        Assert.Equal(@"C:\d\bulk.bin", Assert.Single(big.Grew).Path);

        var beforeSmall = new TestTree().File(@"C:\d\little.bin", 1L << 20).Snapshot(Monday);
        var afterSmall = new TestTree().File(@"C:\d\little.bin", 101L << 20).Snapshot(Thursday);

        var small = SnapshotDiff.Compare(1, beforeSmall, 2, afterSmall).Volumes.Single();
        Assert.Equal(@"C:\d\little.bin", Assert.Single(small.Grew).Path);
    }

    [Fact]
    public void Notes_a_volume_present_in_only_one_scan()
    {
        var before = new TestTree()
            .File(@"C:\a.bin", Gb)
            .File(@"D:\b.bin", Gb)
            .Snapshot(Monday);

        var after = new TestTree().File(@"C:\a.bin", Gb).Snapshot(Thursday);

        var diff = SnapshotDiff.Compare(1, before, 2, after);

        Assert.Single(diff.Volumes);
        Assert.Contains("D: was scanned in 1 but not in 2", diff.Notes);
    }

    [Fact]
    public void Treats_a_name_that_changed_kind_as_two_events()
    {
        var before = new TestTree().File(@"C:\thing", 4 * Gb).Snapshot(Monday);
        var after = new TestTree().File(@"C:\thing\inside.bin", 4 * Gb).Snapshot(Thursday);

        var diff = SnapshotDiff.Compare(1, before, 2, after);
        var volume = Assert.Single(diff.Volumes);

        Assert.Equal(0, volume.Delta);
        Assert.Single(volume.Grew);          // the new directory
        Assert.Single(volume.Shrank);        // the file that used to hold the name
    }

    [Fact]
    public void Warns_when_the_two_scans_used_different_scanners()
    {
        var before = new TestTree()
            .File(@"C:\a.bin", Gb)
            .Snapshot(Monday, ScannerKind.Walk,
                      ScanFlags.Degraded | ScanFlags.PartialHardlinkResolution | ScanFlags.NoAdsAccounting);

        var after = new TestTree().File(@"C:\a.bin", Gb).Snapshot(Thursday, ScannerKind.Mft, ScanFlags.Elevated);

        var diff = SnapshotDiff.Compare(1, before, 2, after);
        var warnings = PathMemo.Cli.Commands.DiffCommand.Incomparability(diff, before, after);

        Assert.Equal(4, warnings.Count);
        Assert.Contains(warnings, w => w.Contains("walk scanner", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.Contains("administrator", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.Contains("component store", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.Contains("alternate data streams", StringComparison.Ordinal));
    }

    [Fact]
    public void Hard_link_aliases_do_not_count_twice_in_a_diff()
    {
        var before = new TestTree()
            .File(@"C:\store\real.dll", 4 * Gb)
            .File(@"C:\apps\alias.dll", 4 * Gb, Snapshots.NodeFlags.HardlinkAlias)
            .Snapshot(Monday);

        var after = new TestTree()
            .File(@"C:\store\real.dll", 4 * Gb)
            .File(@"C:\apps\alias.dll", 4 * Gb, Snapshots.NodeFlags.HardlinkAlias)
            .Snapshot(Thursday);

        var volume = Assert.Single(SnapshotDiff.Compare(1, before, 2, after).Volumes);

        Assert.Equal(0, volume.Delta);
        Assert.Equal(4 * Gb, volume.After);
        Assert.Empty(volume.Grew);
    }
}
