using System.Globalization;
using System.IO.Hashing;
using System.Text;
using System.Text.Json;
using PathMemo.Cli.Output;
using PathMemo.Config;
using PathMemo.Snapshots;
using PathMemo.Storage;

namespace PathMemo.Cli.Commands;

/// <summary>
/// <c>pathmemo export</c>: the whole tree of one scan as JSON or CSV (README section 13).
/// </summary>
/// <remarks>
/// <para>
/// Every other command answers a question; this one hands over the data and lets somebody
/// else ask. It is the one place that writes a row per node rather than a top-N table -
/// 1.6M lines and several hundred megabytes of text - so it always goes to a file and says
/// how large that file turned out to be.
/// </para>
/// <para>
/// Written by walking the tree depth-first with one <see cref="StringBuilder"/> for the
/// current path, pushed and popped per segment. <see cref="NodeStore.GetPath"/> per node
/// would rebuild every ancestor 1.6M times.
/// </para>
/// </remarks>
internal static class ExportCommand
{
    internal static int Run(ExportOptions options)
    {
        if (!SnapshotLoader.TryLoad(options.ScanId,
                SnapshotParts.Tree | SnapshotParts.Volumes, out var snapshot, out var id))
            return ExitCode.NoData;

        var redact = options.Redact ?? AppConfig.Current.Export.RedactPaths;
        var path = options.Output ?? Default(id, options.Format);

        if (File.Exists(path) && !options.Yes)
        {
            Console.Error.WriteLine($"pathmemo: {path} exists - pass --yes to overwrite it");
            return ExitCode.Usage;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);

            var clock = System.Diagnostics.Stopwatch.StartNew();
            var rows = options.Format == ScanFormat.Csv
                ? WriteCsv(path, snapshot, redact)
                : WriteJson(path, snapshot, id, redact);
            clock.Stop();

            var bytes = new FileInfo(path).Length;
            Console.WriteLine($"Exported scan {id}: {rows:N0} entries, {SizeFormat.Bytes(bytes)}");
            Console.WriteLine($"  {path}");
            if (redact) Console.WriteLine("  Names are hashed; structure, sizes and extensions are intact (README threat T13).");

            Diagnostics.Note("wrote {0:N0} rows in {1:F0} ms", rows, clock.Elapsed.TotalMilliseconds);
            Diagnostics.Memory(Console.Error, "after writing the export");
            return ExitCode.Ok;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            Console.Error.WriteLine($"pathmemo: could not write {path}: {ex.Message}");
            return ExitCode.Failure;
        }
    }

    /// <summary>
    /// Where an export goes when <c>--output</c> is not given: the store's own exports
    /// directory, which exists so that a file this large does not land in the working
    /// directory by surprise.
    /// </summary>
    private static string Default(long id, ScanFormat format) => Path.Combine(
        AppPaths.ExportsDirectory,
        $"scan-{id:0000}.{(format == ScanFormat.Csv ? "csv" : "json")}");

    internal static long WriteCsv(string path, SnapshotContents snapshot, bool redact)
    {
        using var w = new StreamWriter(path, append: false, new UTF8Encoding(false));

        // The same six columns as the summary CSV, so one parser reads both, plus the flags
        // a full export cannot do without: a reparse point counted as 0 and a hard link
        // counted once are facts about the number in the row next to them.
        w.WriteLine("kind,path,allocated_bytes,logical_bytes,file_count,modified_utc,flags");

        var rows = 0L;
        Walk(snapshot.Tree, redact, (node, full) =>
        {
            var tree = snapshot.Tree;
            var directory = tree.IsDirectory(node);

            w.Write(directory ? "directory," : "file,");
            w.Write(Quote(full));
            w.Write(',');
            w.Write(tree.Allocated[node].ToString(CultureInfo.InvariantCulture));
            w.Write(',');
            w.Write(tree.Logical[node].ToString(CultureInfo.InvariantCulture));
            w.Write(',');
            if (directory) w.Write(tree.FileCount[node].ToString(CultureInfo.InvariantCulture));
            w.Write(',');
            w.Write(Iso.Of(tree.ModifiedUtc(node)));
            w.Write(',');
            w.Write(Flags(tree.Flags[node]));
            w.Write('\n');
            rows++;
        });

        return rows;
    }

    internal static long WriteJson(string path, SnapshotContents snapshot, long id, bool redact)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
        using var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false });

        json.WriteStartObject();
        json.WriteNumber("schemaVersion", 1);
        json.WriteNumber("scanId", id);
        json.WriteString("startedUtc", Iso.Of(snapshot.StartedUtc));
        json.WriteString("scanner", ScanRepository.Name(snapshot.Scanner));
        json.WriteBoolean("redacted", redact);

        json.WriteStartArray("volumes");
        foreach (var volume in snapshot.Volumes)
        {
            json.WriteStartObject();
            json.WriteString("volume", volume.Letter);
            json.WriteString("root", volume.Root);
            json.WriteString("filesystem", volume.FileSystem);
            json.WriteNumber("clusterBytes", volume.ClusterBytes);
            json.WriteNumber("totalBytes", (long)volume.TotalBytes);
            json.WriteNumber("freeBytes", (long)volume.FreeBytes);
            json.WriteEndObject();
        }
        json.WriteEndArray();

        var rows = 0L;
        json.WriteStartArray("entries");
        Walk(snapshot.Tree, redact, (node, full) =>
        {
            var tree = snapshot.Tree;
            var directory = tree.IsDirectory(node);

            json.WriteStartObject();
            json.WriteString("path", full);
            json.WriteString("kind", directory ? "directory" : "file");
            json.WriteNumber("allocatedBytes", tree.Allocated[node]);
            json.WriteNumber("logicalBytes", tree.Logical[node]);
            if (directory) json.WriteNumber("fileCount", tree.FileCount[node]);
            if (tree.LinkCount[node] > 1) json.WriteNumber("linkCount", tree.LinkCount[node]);
            json.WriteString("modifiedUtc", Iso.Of(tree.ModifiedUtc(node)));

            if (Flags(tree.Flags[node]) is { Length: > 0 } flags) json.WriteString("flags", flags);

            json.WriteEndObject();
            rows++;

            // A million objects in one writer would buffer a million objects. Flushing on a
            // round number keeps the memory flat at a few hundred kilobytes.
            if (rows % 4096 == 0) json.Flush();
        });
        json.WriteEndArray();

        json.WriteNumber("entryCount", rows);
        json.WriteEndObject();
        json.Flush();
        stream.Write("\n"u8);

        return rows;
    }

    /// <summary>
    /// Depth-first over the whole tree, handing each node its full path.
    /// </summary>
    /// <remarks>
    /// An explicit stack rather than recursion: a path can legitimately be 200 segments
    /// deep (README section 15.5), and a stack frame per segment per node is not a cost
    /// worth taking for a loop this simple.
    /// </remarks>
    private static void Walk(NodeStore tree, bool redact, Action<int, string> emit)
    {
        if (tree.Count == 0) return;

        var builder = new StringBuilder(260);
        var stack = new Stack<(int Node, int Child, int Length)>();

        foreach (var root in tree.Roots)
        {
            builder.Clear();
            builder.Append(Segment(tree, root, redact));
            emit(root, builder.ToString());
            stack.Push((root, 0, builder.Length));

            while (stack.Count > 0)
            {
                var (node, child, length) = stack.Pop();
                var children = tree.Children(node);
                var first = children.Start.Value;
                var count = children.End.Value - first;

                if (child == count)
                {
                    builder.Length = length;
                    continue;
                }

                stack.Push((node, child + 1, length));

                var index = first + child;
                builder.Length = length;

                // A volume root already ends in a separator; nothing else does.
                if (length > 0 && builder[length - 1] != Path.DirectorySeparatorChar)
                    builder.Append(Path.DirectorySeparatorChar);

                builder.Append(Segment(tree, index, redact));
                var full = builder.ToString();
                emit(index, full);

                if (tree.ChildCount[index] > 0) stack.Push((index, 0, builder.Length));
                else builder.Length = length;
            }
        }
    }

    /// <summary>
    /// One path segment, hashed when the export is redacted (README threat T13).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The hash is of the lowercased name, so the same directory name is the same token
    /// everywhere: that is what keeps a redacted export useful - the shape of the disk, the
    /// sizes and the repetition survive, the names do not.
    /// </para>
    /// <para>
    /// The extension is kept in the clear. It is not identifying, and losing it would make a
    /// redacted export unable to answer the question exports are actually for: which kind of
    /// file filled the disk. A volume root is kept as it is, because "C:" is not private and
    /// an export with no roots cannot be read at all.
    /// </para>
    /// </remarks>
    private static string Segment(NodeStore tree, int node, bool redact)
    {
        var name = tree.Name(node);
        if (!redact || tree.Parent[node] == NodeStore.NoNode) return name;

        // Only a file's extension. A directory called "$Recycle.Bin" has no extension in
        // any useful sense, and keeping ".Bin" on it would say something about the name
        // without saying anything about the contents.
        var extension = tree.IsDirectory(node) ? "" : Path.GetExtension(name);
        var hash = XxHash3.HashToUInt64(Encoding.UTF8.GetBytes(name.ToLowerInvariant()));
        return hash.ToString("x16", CultureInfo.InvariantCulture)[..12] + extension.ToLowerInvariant();
    }

    private static string Flags(NodeFlags flags)
    {
        var parts = new List<string>(3);
        if ((flags & NodeFlags.Reparse) != 0) parts.Add("reparse");
        if ((flags & NodeFlags.HardlinkAlias) != 0) parts.Add("hardlink");
        if ((flags & NodeFlags.CloudOnly) != 0) parts.Add("cloud");
        if ((flags & NodeFlags.Sparse) != 0) parts.Add("sparse");
        if ((flags & NodeFlags.SelfData) != 0) parts.Add("self");
        if ((flags & NodeFlags.Encrypted) != 0) parts.Add("encrypted");
        if ((flags & NodeFlags.Incomplete) != 0) parts.Add("incomplete");
        return string.Join('|', parts);
    }

    /// <summary>
    /// RFC 4180 quoting, plus the guard <see cref="ScanExport"/> can do without: a redacted
    /// name begins with a hex digit, but a real one can begin with <c>=</c> or <c>+</c>, and
    /// a spreadsheet reads that as a formula.
    /// </summary>
    private static string Quote(string value)
    {
        var escaped = value.Replace("\"", "\"\"", StringComparison.Ordinal);
        return value.Length > 0 && value[0] is '=' or '+' or '-' or '@'
            ? "\"'" + escaped + "\""
            : "\"" + escaped + "\"";
    }
}

internal sealed record ExportOptions
{
    internal long? ScanId { get; init; }
    internal ScanFormat Format { get; init; } = ScanFormat.Json;
    internal string? Output { get; init; }

    /// <summary>Null means "whatever <c>export.redactPaths</c> says" (README section 12).</summary>
    internal bool? Redact { get; init; }

    internal bool Yes { get; init; }
}
