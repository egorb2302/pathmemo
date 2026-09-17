using System.Text;
using System.Text.Json;
using PathMemo.Cli.Output;
using PathMemo.Scanning;
using Xunit;

namespace PathMemo.Tests;

/// <summary>
/// The machine-readable scan output: <c>--format json|csv</c> (README sections 13.2, 13.6).
/// </summary>
/// <remarks>
/// The JSON contract is a promise to scripts: <c>schemaVersion</c>, bytes as integers,
/// times as ISO-8601 with a Z. These tests are what makes breaking it noisy.
/// </remarks>
public sealed class ExportTests
{
    private static ScanResult Sample() => new TestTree()
        .File(@"C:\movies\clip.mp4", 3L << 30)
        .File(@"C:\src\main.cs", 2048)
        .Result(new DateTime(2026, 9, 18, 7, 30, 0, DateTimeKind.Utc));

    [Fact]
    public void Json_states_its_schema_version_and_uses_integer_bytes()
    {
        using var stream = new MemoryStream();
        ScanExport.Json(stream, Sample(), scanId: 12, topCount: 5);

        using var document = JsonDocument.Parse(stream.ToArray());
        var root = document.RootElement;

        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(12, root.GetProperty("scanId").GetInt64());
        Assert.Equal("2026-09-18T07:30:00Z", root.GetProperty("startedUtc").GetString());
        Assert.Equal("mft", root.GetProperty("scanner").GetString());
        Assert.Equal(JsonValueKind.Number, root.GetProperty("allocatedBytes").ValueKind);
        Assert.Equal((3L << 30) + 2048, root.GetProperty("allocatedBytes").GetInt64());
        Assert.Equal(2, root.GetProperty("totalFiles").GetInt32());

        var volume = Assert.Single(root.GetProperty("volumes").EnumerateArray().ToList());
        Assert.Equal("C:", volume.GetProperty("volume").GetString());
        Assert.Equal(JsonValueKind.Number, volume.GetProperty("unaccountedBytes").ValueKind);

        var categories = root.GetProperty("categories").EnumerateArray().ToList();
        Assert.Equal("media", categories[0].GetProperty("category").GetString());

        Assert.NotEmpty(root.GetProperty("topFiles").EnumerateArray().ToList());
    }

    [Fact]
    public void Json_lists_the_accuracy_limits_of_a_degraded_scan()
    {
        var degraded = new TestTree()
            .File(@"C:\a.bin", 1024)
            .Result(DateTime.UtcNow, ScannerKind.Walk,
                    ScanFlags.Degraded | ScanFlags.PartialHardlinkResolution | ScanFlags.NoAdsAccounting);

        using var stream = new MemoryStream();
        ScanExport.Json(stream, degraded, scanId: null, topCount: 3);

        using var document = JsonDocument.Parse(stream.ToArray());
        var limitations = document.RootElement.GetProperty("limitations")
            .EnumerateArray().Select(e => e.GetString()).ToList();

        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("scanId").ValueKind);
        Assert.Contains("hardLinksResolvedOnlyAboveOneMegabyte", limitations);
        Assert.Contains("alternateDataStreamsNotCounted", limitations);
        Assert.Contains("protectedDirectoriesSkipped", limitations);
    }

    [Fact]
    public void Csv_quotes_a_path_that_contains_a_comma_or_a_quote()
    {
        var result = new TestTree()
            .File(@"C:\reports\report,final.txt", 10)
            .File("C:\\reports\\say \"hi\".txt", 20)
            .Result(DateTime.UtcNow);

        var text = new StringBuilder();
        using (var writer = new StringWriter(text)) ScanExport.Csv(writer, result, limit: 10);

        var lines = text.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal("kind,path,allocated_bytes,logical_bytes,file_count,modified_utc", lines[0]);
        Assert.Contains(lines, l => l.Contains(@"""C:\reports\report,final.txt""", StringComparison.Ordinal));

        // A quote in a name is doubled, not dropped: the row must still parse.
        Assert.Contains(lines, l => l.Contains(@"""C:\reports\say """"hi"""".txt""", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.EndsWith(",20,20,,2000-01-01T00:00:00Z", StringComparison.Ordinal));
    }
}
