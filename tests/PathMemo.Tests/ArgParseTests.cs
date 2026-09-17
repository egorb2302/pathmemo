using PathMemo.Analysis;
using PathMemo.Cli;
using PathMemo.Cli.Output;
using Xunit;

namespace PathMemo.Tests;

public class ArgParseTests
{
    [Theory]
    [InlineData("0", 0L)]
    [InlineData("1024", 1024L)]
    [InlineData("1KB", 1024L)]
    [InlineData("1kb", 1024L)]
    [InlineData("1 MB", 1048576L)]
    [InlineData("1GB", 1073741824L)]
    [InlineData("1GiB", 1073741824L)]
    [InlineData("1.5GB", 1610612736L)]
    [InlineData("500mb", 524288000L)]
    public void Parses_sizes(string text, long expected) =>
        Assert.Equal(expected, ArgParse.Size(text));

    [Theory]
    [InlineData("GB")]
    [InlineData("")]
    [InlineData("1XB")]
    [InlineData("abc")]
    public void Rejects_bad_sizes(string text) =>
        Assert.Throws<ArgumentException>(() => ArgParse.Size(text));

    [Theory]
    [InlineData("45s", 45)]
    [InlineData("90m", 90 * 60)]
    [InlineData("12h", 12 * 3600)]
    [InlineData("30d", 30 * 86400)]
    [InlineData("30", 30 * 86400)]
    [InlineData("2w", 14 * 86400)]
    public void Parses_durations(string text, double expectedSeconds) =>
        Assert.Equal(expectedSeconds, ArgParse.Duration(text).TotalSeconds);

    [Fact]
    public void Normalises_extension_lists()
    {
        var set = ArgParse.Extensions(".ISO, vhdx ,,zip");

        Assert.Equal(3, set.Count);
        Assert.Contains("iso", set);
        Assert.Contains("vhdx", set);
        Assert.Contains("zip", set);
    }

    [Theory]
    [InlineData("unique", (int)SizeMode.Unique)]
    [InlineData("ALLOCATED", (int)SizeMode.Allocated)]
    [InlineData("logical", (int)SizeMode.Logical)]
    public void Parses_size_modes(string text, int expected) =>
        Assert.Equal((SizeMode)expected, ArgParse.Mode(text));

    [Fact]
    public void Rejects_unknown_size_mode() =>
        Assert.Throws<ArgumentException>(() => ArgParse.Mode("physical"));
}

public class SizeFormatTests
{
    [Theory]
    [InlineData(0UL, "0 B")]
    [InlineData(999UL, "999 B")]
    [InlineData(1024UL, "1 KB")]
    [InlineData(1536UL, "1.5 KB")]
    [InlineData(1048576UL, "1 MB")]
    [InlineData(10737418240UL, "10 GB")]
    public void Formats_with_three_significant_figures(ulong bytes, string expected) =>
        Assert.Equal(expected, SizeFormat.Bytes(bytes));

    [Fact]
    public void Formats_negative_deltas() =>
        Assert.Equal("-1 GB", SizeFormat.Bytes(-1073741824L));

    [Fact]
    public void Never_exceeds_the_column_width_it_promises()
    {
        // The tree and top views right-align sizes in a 9-character column.
        ulong[] samples = [0, 1, 1023, 1024, 999_999, 1UL << 30, 1UL << 40, (1UL << 50) - 1];

        foreach (var sample in samples)
            Assert.True(SizeFormat.Bytes(sample).Length <= 9, $"{sample} -> '{SizeFormat.Bytes(sample)}'");
    }
}

public class PathDisplayTests
{
    [Fact]
    public void Keeps_the_tail_that_identifies_the_item()
    {
        var path = @"C:\Users\me\projects\bigapp\node_modules\.bin";

        var shortened = PathDisplay.Shorten(path, 32);

        Assert.True(PathDisplay.Width(shortened) <= 32);
        Assert.EndsWith(@".bin", shortened, StringComparison.Ordinal);
        Assert.StartsWith(@"C:", shortened, StringComparison.Ordinal);
    }

    [Fact]
    public void Leaves_short_paths_untouched()
    {
        var path = @"C:\Windows";
        Assert.Equal(path, PathDisplay.Shorten(path, 40));
    }

    [Fact]
    public void Counts_wide_characters_as_two_columns()
    {
        Assert.Equal(1, PathDisplay.Width("a"));
        Assert.Equal(2, PathDisplay.Width("写"));
        Assert.Equal(4, PathDisplay.Width("写真"));
        Assert.Equal(6, PathDisplay.Width("ab写真"));
        Assert.Equal(2, PathDisplay.Width("😀"));
    }

    [Fact]
    public void Never_returns_more_columns_than_asked_for()
    {
        var path = @"C:\Users\пользователь\Документы\проекты\очень-длинное-имя-папки\файл.txt";

        for (var width = 4; width <= 60; width++)
            Assert.True(PathDisplay.Width(PathDisplay.Shorten(path, width)) <= width,
                $"width {width} overflowed");
    }
}
