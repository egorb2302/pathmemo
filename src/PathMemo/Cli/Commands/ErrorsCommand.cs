using System.Globalization;
using System.Text.Json;
using PathMemo.Analysis;
using PathMemo.Cli.Output;
using PathMemo.Scanning;
using PathMemo.Snapshots;

namespace PathMemo.Cli.Commands;

/// <summary>
/// <c>pathmemo errors</c>: what a scan could not read (README section 4.9).
/// </summary>
/// <remarks>
/// <para>
/// The counterpart to the unaccounted bytes in a scan summary. "1.4% unaccounted" is a
/// number; this is the list of paths behind it, and the difference matters when the answer
/// to "where did the space go" is "into a directory the scan was not allowed to open".
/// </para>
/// <para>
/// It reads the ERRORS section alone - a few hundred strings out of a file whose tree is
/// 74 MB, which takes under a millisecond instead of a fifth of a second (README section
/// 5.5). <c>--sizes</c> is what asks for the tree as well, because the bytes behind a
/// refused path can only come from there.
/// </para>
/// </remarks>
internal static class ErrorsCommand
{
    internal static int Run(ErrorsOptions options)
    {
        var parts = options.Sizes ? SnapshotParts.Errors | SnapshotParts.Tree : SnapshotParts.Errors;
        if (!SnapshotLoader.TryLoad(options.ScanId, parts, out var snapshot, out var id))
            return ExitCode.NoData;

        if (options.Json)
        {
            WriteJson(snapshot, id, options);
            return ExitCode.Ok;
        }

        var w = Console.Out;
        w.WriteLine();

        if (snapshot.Errors.Count == 0)
        {
            w.WriteLine($"Scan {id} read every path it visited.");

            // An unelevated scan skips protected directories without producing an error per
            // path, so "no errors" must not be read as "nothing was missed".
            if ((snapshot.Flags & ScanFlags.Elevated) == 0)
                w.WriteLine("  It ran without administrator rights, so directories it could not open at all" +
                            " are missing from the tree rather than listed here (README section 4.1).");

            return ExitCode.Ok;
        }

        w.WriteLine($"Scan {id}: {snapshot.Errors.Count:N0} unreadable path" +
                    (snapshot.Errors.Count == 1 ? "" : "s"));
        w.WriteLine();

        var groups = snapshot.Errors
            .GroupBy(e => e.Kind)
            .OrderByDescending(g => g.Count())
            .ToList();

        foreach (var group in groups)
        {
            var known = options.Sizes ? Known(snapshot.Tree, group) : null;
            w.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "  {0,-18} {1,6:N0} path{2}{3}",
                group.Key, group.Count(), group.Count() == 1 ? " " : "s",
                known is { } k ? $"    {k.Found:N0} of them are in the tree, holding {SizeFormat.Bytes(k.Bytes)}" : ""));
        }

        if (options.Kind is { } wanted && groups.TrueForAll(g => g.Key != wanted))
        {
            w.WriteLine();
            w.WriteLine($"No path in this scan failed with {wanted}.");
            return ExitCode.Ok;
        }

        foreach (var group in groups)
        {
            if (options.Kind is { } only && group.Key != only) continue;

            w.WriteLine();
            w.WriteLine($"{group.Key}");

            var shown = 0;
            foreach (var error in group)
            {
                if (shown++ == options.Limit) break;

                // The full path, never shortened: it is the payload here, and a list of
                // paths with the middles removed is a list nobody can act on.
                w.WriteLine($"  {error.Path}");

                if (Reason(error) is { } reason) w.WriteLine($"      {reason}");
            }

            if (group.Count() > shown)
                w.WriteLine($"  ... and {group.Count() - shown:N0} more (--limit {group.Count()} for all of them)");
        }

        w.WriteLine();
        w.WriteLine((snapshot.Flags & ScanFlags.Elevated) != 0
            ? "Elevated, so this is what even administrator rights could not read: files held open," +
              " names Windows itself cannot express, or a filesystem that refused."
            : "Run 'pathmemo scan' from an administrator prompt to read most of these (README section 4.1).");

        return ExitCode.Ok;
    }

    /// <summary>
    /// What the failure adds to the kind and the path, or null when it adds nothing.
    /// </summary>
    /// <remarks>
    /// The BCL's own message for a refused directory is "Access to the path '...' is denied",
    /// which is the kind and the path again. Printing it under every one of 527 paths doubles
    /// the length of the list and says nothing.
    /// </remarks>
    internal static string? Reason(ScanError error)
    {
        var code = error.Win32Code != 0 ? $"Win32 {error.Win32Code}" : null;
        var message = error.Message.Length == 0
                      || error.Message.Contains(error.Path, StringComparison.OrdinalIgnoreCase)
            ? null
            : error.Message;

        return (code, message) switch
        {
            (null, null) => null,
            (null, { } only) => only,
            ({ } only, null) => only,
            var (c, m) => $"{c}: {m}",
        };
    }

    /// <summary>
    /// How many of a group's paths are in the tree at all, and what they are known to hold.
    /// </summary>
    /// <remarks>
    /// A walk scan puts a refused directory in the tree with nothing under it, so the answer
    /// there is "all of them, holding 0" - which is the honest one. An MFT scan read the
    /// metadata past the ACL, so the bytes are real (README sections 4.1, 4.9).
    /// </remarks>
    internal static (int Found, long Bytes)? Known(NodeStore tree, IEnumerable<ScanError> group)
    {
        if (tree.Count == 0) return null;

        long total = 0;
        var found = 0;

        foreach (var error in group)
        {
            var node = TreeQuery.Find(tree, error.Path);
            if (node == NodeStore.NoNode) continue;

            total += tree.Allocated[node];
            found++;
        }

        return (found, total);
    }

    private static void WriteJson(SnapshotContents snapshot, long id, ErrorsOptions options)
    {
        using var stream = Console.OpenStandardOutput();
        using var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });

        json.WriteStartObject();
        json.WriteNumber("schemaVersion", 1);
        json.WriteNumber("scanId", id);
        json.WriteBoolean("elevated", (snapshot.Flags & ScanFlags.Elevated) != 0);
        json.WriteNumber("count", snapshot.Errors.Count);

        json.WriteStartObject("byKind");
        foreach (var group in snapshot.Errors.GroupBy(e => e.Kind).OrderByDescending(g => g.Count()))
            json.WriteNumber(Camel(group.Key.ToString()), group.Count());
        json.WriteEndObject();

        json.WriteStartArray("paths");
        var shown = 0;
        foreach (var error in snapshot.Errors)
        {
            if (options.Kind is { } only && error.Kind != only) continue;
            if (shown++ == options.Limit) break;

            json.WriteStartObject();
            json.WriteString("path", error.Path);
            json.WriteString("kind", Camel(error.Kind.ToString()));
            json.WriteNumber("win32Code", error.Win32Code);
            json.WriteString("message", error.Message);

            if (options.Sizes && TreeQuery.Find(snapshot.Tree, error.Path) is var node
                && node != NodeStore.NoNode)
                json.WriteNumber("allocatedBytes", snapshot.Tree.Allocated[node]);

            json.WriteEndObject();
        }
        json.WriteEndArray();

        json.WriteEndObject();
        json.Flush();
        stream.Write("\n"u8);
    }

    private static string Camel(string pascal) =>
        pascal.Length == 0 ? pascal : char.ToLowerInvariant(pascal[0]) + pascal[1..];
}

internal sealed record ErrorsOptions
{
    internal long? ScanId { get; init; }

    /// <summary>Paths listed per kind. The summary table always shows every kind.</summary>
    internal int Limit { get; init; } = 20;

    internal ScanErrorKind? Kind { get; init; }

    /// <summary>Also open the tree, to say how many bytes sit behind the refused paths.</summary>
    internal bool Sizes { get; init; }

    internal bool Json { get; init; }
}
