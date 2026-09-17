using PathMemo.Analysis;
using PathMemo.Snapshots;
using Xunit;

namespace PathMemo.Tests;

/// <summary>
/// Per-scan category and extension totals (README section 11).
/// </summary>
public sealed class AggregateTests
{
    [Fact]
    public void Classifies_by_extension()
    {
        var tree = new TestTree()
            .File(@"C:\a\clip.mp4", 1000)
            .File(@"C:\a\backup.zip", 200)
            .File(@"C:\a\main.cs", 30)
            .File(@"C:\a\readme.md", 10)
            .File(@"C:\a\tool.exe", 50)
            .File(@"C:\a\nameless", 7)
            .Build();

        var byName = ScanAggregates.Compute(tree).Categories
            .ToDictionary(c => c.Category, c => c.AllocatedBytes);

        Assert.Equal(1000, byName[FileCategory.Media]);
        Assert.Equal(200, byName[FileCategory.Archive]);
        Assert.Equal(30, byName[FileCategory.Source]);
        Assert.Equal(10, byName[FileCategory.Document]);
        Assert.Equal(50, byName[FileCategory.App]);
        Assert.Equal(7, byName[FileCategory.Other]);
    }

    [Fact]
    public void A_directory_claims_its_whole_subtree()
    {
        // Windows is full of media files that are not the user's media, and an archive
        // inside a cache is still cache.
        var tree = new TestTree()
            .File(@"C:\Windows\Media\chimes.wav", 500)
            .File(@"C:\projects\app\node_modules\pkg\photo.png", 300)
            .File(@"C:\projects\app\src\main.cs", 20)
            .Build();

        var byName = ScanAggregates.Compute(tree).Categories
            .ToDictionary(c => c.Category, c => c.AllocatedBytes);

        Assert.Equal(500, byName[FileCategory.System]);
        Assert.Equal(300, byName[FileCategory.Cache]);
        Assert.Equal(20, byName[FileCategory.Source]);
        Assert.False(byName.ContainsKey(FileCategory.Media));
    }

    [Fact]
    public void A_directory_named_like_a_system_one_deeper_down_is_not_the_system()
    {
        var tree = new TestTree()
            .File(@"C:\projects\port\Windows\notes.txt", 100)
            .Build();

        var categories = ScanAggregates.Compute(tree).Categories;

        Assert.Equal(FileCategory.Document, Assert.Single(categories).Category);
    }

    [Fact]
    public void Ntfs_metafiles_at_the_volume_root_are_system()
    {
        var tree = new TestTree()
            .File(@"C:\$MFT", 1L << 30)
            .File(@"C:\$LogFile", 64L << 20)
            .File(@"C:\projects\$draft", 100)      // a user file may be named anything
            .Build();

        var byName = ScanAggregates.Compute(tree).Categories
            .ToDictionary(c => c.Category, c => c.AllocatedBytes);

        Assert.Equal((1L << 30) + (64L << 20), byName[FileCategory.System]);
        Assert.Equal(100, byName[FileCategory.Other]);
    }

    [Fact]
    public void Hard_link_aliases_add_a_name_but_not_bytes()
    {
        var tree = new TestTree()
            .File(@"C:\store\real.dll", 1000)
            .File(@"C:\apps\alias.dll", 1000, NodeFlags.HardlinkAlias)
            .Build();

        var app = Assert.Single(ScanAggregates.Compute(tree).Categories);

        Assert.Equal(FileCategory.App, app.Category);
        Assert.Equal(1000, app.AllocatedBytes);
        Assert.Equal(1, app.FileCount);
    }

    [Fact]
    public void Totals_extensions_and_rolls_the_tail_into_one_row()
    {
        var tree = new TestTree();
        tree.File(@"C:\a\one.iso", 4000);
        tree.File(@"C:\a\two.iso", 1000);
        for (var i = 0; i < ScanAggregates.MaxExtensionRows + 40; i++)
            tree.File($@"C:\junk\file{i}.x{i}", 3);

        var extensions = ScanAggregates.Compute(tree.Build()).Extensions;

        Assert.Equal(ScanAggregates.MaxExtensionRows + 1, extensions.Count);

        var iso = extensions.Single(e => e.Extension == "iso");
        Assert.Equal(5000, iso.AllocatedBytes);
        Assert.Equal(2, iso.FileCount);

        var rest = extensions.Single(e => e.Extension == ScanAggregates.RestExtension);
        Assert.Equal(41 * 3, rest.AllocatedBytes);
        Assert.Equal(41, rest.FileCount);
    }

    [Fact]
    public void A_dotfile_has_no_extension()
    {
        var tree = new TestTree()
            .File(@"C:\repo\.gitignore", 10)
            .File(@"C:\repo\Makefile", 20)
            .Build();

        var extensions = ScanAggregates.Compute(tree).Extensions;

        var none = Assert.Single(extensions);
        Assert.Equal(ScanAggregates.NoExtension, none.Extension);
        Assert.Equal(30, none.AllocatedBytes);
    }
}
