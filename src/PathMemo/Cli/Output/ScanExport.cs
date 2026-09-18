using System.Globalization;
using System.Text.Json;
using PathMemo.Analysis;
using PathMemo.Scanning;
using PathMemo.Snapshots;
using PathMemo.Storage;

namespace PathMemo.Cli.Output;

internal enum ScanFormat
{
    Console,
    Json,
    Csv,
}

/// <summary>
/// Machine-readable results of a scan: <c>--format json|csv</c> (README sections 13.2, 13.6).
/// </summary>
/// <remarks>
/// <see cref="Utf8JsonWriter"/> by hand rather than a serializer, for the same reason
/// there is no ORM: a reflection-based writer is what makes a trimmed build fail at
/// runtime instead of at build time (README section 18).
/// </remarks>
internal static class ScanExport
{
    internal static void Json(Stream output, ScanResult result, long? scanId, int topCount,
                              ScanAggregateSet? aggregates = null)
    {
        using var json = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = true });

        json.WriteStartObject();
        json.WriteNumber("schemaVersion", 1);
        if (scanId is { } id) json.WriteNumber("scanId", id); else json.WriteNull("scanId");
        json.WriteString("startedUtc", Iso.Of(result.StartedUtc));
        json.WriteNumber("durationMs", (long)result.Duration.TotalMilliseconds);
        json.WriteString("scanner", ScanRepository.Name(result.Scanner));
        json.WriteBoolean("elevated", (result.Flags & ScanFlags.Elevated) != 0);
        json.WriteBoolean("partial", (result.Flags & ScanFlags.Partial) != 0);

        // Only on an incremental scan, and only added to the object: a reader written
        // against schema 1 keeps working (README section 13.6).
        if ((result.Flags & ScanFlags.Incremental) != 0)
            json.WriteNumber("changedDirectories", result.ChangedDirectories);

        json.WriteNumber("totalFiles", result.FileCount);
        json.WriteNumber("totalDirectories", result.DirectoryCount);
        json.WriteNumber("allocatedBytes", result.AllocatedBytes);
        json.WriteNumber("logicalBytes", result.LogicalBytes);

        json.WriteStartArray("flags");
        foreach (var flag in Enum.GetValues<ScanFlags>())
        {
            if (flag == ScanFlags.None || (result.Flags & flag) == 0) continue;
            json.WriteStringValue(Camel(flag.ToString()));
        }
        json.WriteEndArray();

        json.WriteStartArray("volumes");
        var tree = result.Tree;
        for (var v = 0; v < result.Volumes.Count; v++)
        {
            var volume = result.Volumes[v];
            var scanned = v < tree.Roots.Length ? tree.Allocated[tree.Roots[v]] : 0;
            var wholeVolume = volume.Root.TrimEnd(Path.DirectorySeparatorChar).Length <= 2;

            json.WriteStartObject();
            json.WriteString("volume", volume.Letter);
            json.WriteString("root", volume.Root);
            if (volume.Label is { } label) json.WriteString("label", label);
            json.WriteString("filesystem", volume.FileSystem);
            json.WriteNumber("clusterBytes", volume.ClusterBytes);
            json.WriteNumber("totalBytes", (long)volume.TotalBytes);
            json.WriteNumber("freeBytes", (long)volume.FreeBytes);
            json.WriteNumber("usedBytes", (long)volume.UsedBytes);
            json.WriteNumber("scannedBytes", scanned);

            // Only a whole-volume scan has anything to reconcile against
            // (README section 3.5).
            if (wholeVolume) json.WriteNumber("unaccountedBytes", (long)volume.UsedBytes - scanned);
            else json.WriteNull("unaccountedBytes");

            json.WriteEndObject();
        }
        json.WriteEndArray();

        json.WriteStartObject("errors");
        json.WriteNumber("count", result.Errors.Count);
        json.WriteStartObject("byKind");
        foreach (var group in result.Errors.GroupBy(e => e.Kind).OrderByDescending(g => g.Count()))
            json.WriteNumber(Camel(group.Key.ToString()), group.Count());
        json.WriteEndObject();
        json.WriteEndObject();

        json.WriteStartArray("categories");
        foreach (var total in (aggregates ?? ScanAggregates.Compute(result.Tree)).Categories)
        {
            json.WriteStartObject();
            json.WriteString("category", FileCategories.Name(total.Category));
            json.WriteNumber("allocatedBytes", total.AllocatedBytes);
            json.WriteNumber("fileCount", total.FileCount);
            json.WriteEndObject();
        }
        json.WriteEndArray();

        WriteEntries(json, "topDirectories", result.Tree, directories: true, topCount);
        WriteEntries(json, "topFiles", result.Tree, directories: false, topCount);

        json.WriteStartArray("limitations");
        foreach (var limitation in Limitations(result.Flags)) json.WriteStringValue(limitation);
        json.WriteEndArray();

        json.WriteEndObject();
        json.Flush();
        output.Write("\n"u8);
    }

    private static void WriteEntries(Utf8JsonWriter json, string name, NodeStore tree, bool directories, int limit)
    {
        json.WriteStartArray(name);
        foreach (var node in Largest(tree, directories, limit))
        {
            json.WriteStartObject();
            json.WriteString("path", tree.GetPath(node));
            json.WriteNumber("allocatedBytes", tree.Allocated[node]);
            json.WriteNumber("logicalBytes", tree.Logical[node]);
            if (directories) json.WriteNumber("fileCount", tree.FileCount[node]);
            json.WriteString("modifiedUtc", Iso.Of(tree.ModifiedUtc(node)));
            json.WriteEndObject();
        }
        json.WriteEndArray();
    }

    /// <summary>
    /// The same largest-entries table as the console summary, one row per line. Not the
    /// whole tree: a full export is <c>pathmemo export</c>, and a million-row CSV out of
    /// a screening tool is a trap, not a feature.
    /// </summary>
    internal static void Csv(TextWriter output, ScanResult result, int limit)
    {
        output.WriteLine("kind,path,allocated_bytes,logical_bytes,file_count,modified_utc");

        foreach (var directories in (bool[])[true, false])
        {
            var kind = directories ? "directory" : "file";
            foreach (var node in Largest(result.Tree, directories, limit))
            {
                output.WriteLine(string.Join(',',
                    kind,
                    Quote(result.Tree.GetPath(node)),
                    result.Tree.Allocated[node].ToString(CultureInfo.InvariantCulture),
                    result.Tree.Logical[node].ToString(CultureInfo.InvariantCulture),
                    directories ? result.Tree.FileCount[node].ToString(CultureInfo.InvariantCulture) : "",
                    Iso.Of(result.Tree.ModifiedUtc(node))));
            }
        }
    }

    private static int[] Largest(NodeStore tree, bool directories, int limit)
    {
        var filter = new TreeQuery.TopFilter { Directories = directories, Limit = limit };
        return TreeQuery.Top(tree, filter, SizeMode.Unique);
    }

    internal static IReadOnlyList<string> Limitations(ScanFlags flags)
    {
        var limitations = new List<string>(4);

        if ((flags & ScanFlags.Incremental) != 0)
            limitations.Add("untouchedDirectoriesCarriedFromThePreviousScan");
        if ((flags & ScanFlags.PartialHardlinkResolution) != 0)
            limitations.Add("hardLinksResolvedOnlyAboveOneMegabyte");
        if ((flags & ScanFlags.NoAdsAccounting) != 0)
            limitations.Add("alternateDataStreamsNotCounted");
        if ((flags & ScanFlags.Elevated) == 0)
            limitations.Add("protectedDirectoriesSkipped");
        if ((flags & ScanFlags.Partial) != 0)
            limitations.Add("cancelledTotalsIncomplete");

        return limitations;
    }

    /// <summary>
    /// RFC 4180 quoting: file names legitimately contain commas, quotes and newlines.
    /// </summary>
    /// <remarks>
    /// No spreadsheet-formula guard, because every field written here is a full path and
    /// so begins with a drive letter or a separator - never with <c>=</c>. The command
    /// that exports arbitrary text fields will need one.
    /// </remarks>
    private static string Quote(string value) =>
        "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static string Camel(string pascal) =>
        pascal.Length == 0 ? pascal : char.ToLowerInvariant(pascal[0]) + pascal[1..];
}
