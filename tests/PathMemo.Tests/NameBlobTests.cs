using System.Text;
using PathMemo.Snapshots;
using Xunit;

namespace PathMemo.Tests;

public class NameBlobTests
{
    [Fact]
    public void Interning_the_same_name_twice_returns_the_same_offset()
    {
        var blob = new NameBlobBuilder(16);

        var a = blob.Intern("node_modules");
        var b = blob.Intern("node_modules");

        Assert.Equal(a, b);
        Assert.Equal(1, blob.SegmentCount);
    }

    [Fact]
    public void Different_names_get_different_offsets_and_round_trip()
    {
        var blob = new NameBlobBuilder(16);
        var names = new[] { "bin", "obj", "Microsoft", "node_modules", ".git", "a", "" };

        var offsets = names.Select(n => blob.Intern(n.AsSpan())).ToArray();

        for (var i = 0; i < names.Length; i++)
            Assert.Equal(names[i], Encoding.UTF8.GetString(blob.Read(offsets[i])));
    }

    [Fact]
    public void Offset_zero_is_reserved_so_it_can_mark_an_empty_slot()
    {
        var blob = new NameBlobBuilder(16);

        // The intern table uses 0 as "empty slot", so no interned name may ever live at
        // offset 0 - including the empty string, which would otherwise be
        // indistinguishable from an unused slot.
        Assert.NotEqual(0, blob.Intern(""));
        Assert.NotEqual(0, blob.Intern("x"));
        Assert.Equal("", Encoding.UTF8.GetString(blob.Read(blob.Intern(""))));
    }

    [Fact]
    public void Survives_growth_past_the_initial_capacity()
    {
        var blob = new NameBlobBuilder(4);
        var offsets = new Dictionary<string, int>();

        for (var i = 0; i < 5_000; i++)
            offsets[$"segment-{i}"] = blob.Intern($"segment-{i}");

        Assert.Equal(5_000, blob.SegmentCount);
        foreach (var (name, offset) in offsets)
            Assert.Equal(name, Encoding.UTF8.GetString(blob.Read(offset)));
    }

    [Fact]
    public void Handles_names_that_are_not_ascii()
    {
        var blob = new NameBlobBuilder(16);
        var names = new[] { "Документы", "写真", "emoji-\U0001F600", "ácombining" };

        foreach (var name in names)
            Assert.Equal(name, Encoding.UTF8.GetString(blob.Read(blob.Intern(name))));
    }

    [Fact]
    public void Deduplicates_by_exact_bytes_not_case()
    {
        var blob = new NameBlobBuilder(16);

        // Case must be preserved for display, so "Bin" and "bin" are distinct entries even
        // though NTFS would treat them as the same name.
        Assert.NotEqual(blob.Intern("Bin"), blob.Intern("bin"));
    }
}
