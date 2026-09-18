using System.Globalization;
using System.Text.Json;
using PathMemo.Duplicates;

namespace PathMemo.Cli.Output;

/// <summary>
/// The duplicate report as text and as JSON (README sections 8, 13.6).
/// </summary>
/// <remarks>
/// Hard-link sets are printed apart from duplicates and never folded into the total, which
/// is the one number the whole command exists to produce. A row that says "3 copies, 1.2 GB"
/// about three names for one file would be a lie in the only column anybody reads
/// (README sections 3.2, 8.4).
/// </remarks>
internal static class DupeTable
{
    internal static void Print(DupeReport report, int limit, TextWriter w)
    {
        w.WriteLine();

        if (report.Declined)
        {
            w.WriteLine("Nothing was read.");
            return;
        }

        var duplicates = report.Duplicates.ToList();
        var hardlinks = report.Hardlinks.ToList();

        if (duplicates.Count == 0 && hardlinks.Count == 0)
        {
            w.WriteLine(report.Stats.Candidates == 0
                ? $"No two files in scan {report.ScanId} even share a size above "
                  + $"{SizeFormat.Bytes(report.MinBytes)}."
                : $"No duplicates in scan {report.ScanId}: "
                  + $"{report.Stats.Candidates:N0} files shared a size and none shared their contents.");

            Cost(report, w);
            return;
        }

        if (duplicates.Count > 0)
        {
            w.WriteLine("       SIZE  COPIES    WASTED  KEEP");
            w.WriteLine("  " + new string('-', 86));

            var shown = 0;
            foreach (var group in duplicates.Take(limit))
            {
                w.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"  {shown + 1,3}. {SizeFormat.Bytes(group.Bytes),9} {group.Count,6}  "
                    + $"{SizeFormat.Bytes(group.Wasted),8}  {PathDisplay.Shorten(group.Keeper.Path, 56)}"));
                shown++;
            }

            if (duplicates.Count > shown)
                w.WriteLine($"  ... and {duplicates.Count - shown:N0} more groups (--limit)");

            w.WriteLine("  " + new string('-', 86));
            w.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  {duplicates.Count:N0} group{(duplicates.Count == 1 ? "" : "s")} · "
                + $"{duplicates.Sum(g => g.Count):N0} files · "
                + $"{SizeFormat.Bytes(duplicates.Sum(g => g.Wasted))} would be freed"));
        }

        if (hardlinks.Count > 0)
        {
            var names = hardlinks.Sum(g => g.Count).ToString("N0", CultureInfo.InvariantCulture);
            var stored = SizeFormat.Bytes(hardlinks.Sum(g => g.Bytes));
            var sets = hardlinks.Count.ToString("N0", CultureInfo.InvariantCulture);

            w.WriteLine();
            w.WriteLine($"  {sets} hard-link set{(hardlinks.Count == 1 ? "" : "s")} "
                      + $"({names} names, {stored} stored once) - deleting a name there "
                      + "frees nothing, so they are not in the total");
        }

        Cost(report, w);

        var offered = duplicates.Sum(g => g.Wasted);
        w.WriteLine();

        if (offered > 0)
        {
            w.WriteLine("  pathmemo dupes --group <n>            the files of one group");
            w.WriteLine("  pathmemo dupes --dry-run              what --apply would delete");
            w.WriteLine($"  pathmemo dupes --apply               quarantine every copy but the keeper "
                      + $"({SizeFormat.Bytes(offered)})");
        }
    }

    /// <summary>What the run actually did, which is how a user judges whether to run it again.</summary>
    private static void Cost(DupeReport report, TextWriter w)
    {
        var stats = report.Stats;
        w.WriteLine();

        // A stored run has no cost to report - it did its reading some time ago, and
        // saying "read 0 B in 0.0s" about it would be a lie dressed as a statistic.
        if (report.Stored)
        {
            w.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  from the search of {report.RanAtUtc:yyyy-MM-dd HH:mm} UTC · "
                + $"'pathmemo dupes' reads the disk again"));
            return;
        }

        w.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"  read {SizeFormat.Bytes(stats.BytesRead)} from {stats.Opened:N0} files in "
            + $"{Duration(stats.Elapsed)} · {stats.FromCache:N0} hashes came from the cache"));

        if (stats.CloudSkipped > 0)
            w.WriteLine($"  {stats.CloudSkipped:N0} cloud-only files were not read (reading one downloads it)");

        if (stats.Unreadable > 0)
            w.WriteLine($"  {stats.Unreadable:N0} files could not be opened and are not in any group");

        if (stats.Impostors > 0)
            w.WriteLine($"  {stats.Impostors:N0} files agreed on a hash and differed byte for byte - "
                      + "stage 4 kept them apart");

        if (report.Partial)
            w.WriteLine("  the run was cancelled: these groups are real, the totals are a floor");
    }

    /// <summary>The files of one group (<c>--group</c>).</summary>
    internal static void PrintGroup(DupeGroup group, int number, int limit, TextWriter w)
    {
        var kind = group.Kind == DupeKind.HardlinkSet ? "hard-link set" : "duplicate";
        var hash = group.Hash.Length > 0 ? "   " + group.Hash[..Math.Min(16, group.Hash.Length)] : "";

        w.WriteLine();
        w.WriteLine($"  group {number}   {SizeFormat.Bytes(group.Bytes)} x {group.Count}   {kind}{hash}");

        w.WriteLine();

        // The keeper first, whatever its place in the list: a hundred and fourteen copies
        // of one cache file is a real group, and the row that matters must not be the
        // fifty-seventh one down.
        var ordered = group.Files.OrderByDescending(f => f.Keeper).ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase);

        foreach (var file in ordered.Take(limit))
        {
            var mark = file.Keeper ? "keep" : file.Protected ? "kept" : file.LinkCount > 1 ? "link" : "    ";
            var when = file.ModifiedUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var why = file.Why is { } reason ? "  " + reason : "";

            w.WriteLine($"  {mark}  {when}  {PathDisplay.Shorten(file.Path, 62),-62}{why}");
        }

        if (group.Count > limit)
            w.WriteLine($"  ... and {group.Count - limit} more copies (--limit)");

        if (group.Kind == DupeKind.HardlinkSet)
        {
            w.WriteLine();
            w.WriteLine("  These are names for one file. Deleting all but one frees nothing at all.");
        }
    }

    /// <summary>One path per line: the copies a deletion would take (<c>--paths-only</c>).</summary>
    internal static void PrintPaths(IEnumerable<DupeGroup> groups, TextWriter w)
    {
        foreach (var group in groups)
            foreach (var victim in group.Victims)
                w.WriteLine(victim.Path);
    }

    internal static void WriteJson(DupeReport report, TextWriter w)
    {
        using var json = new Utf8JsonWriter(
            Console.OpenStandardOutput(), new JsonWriterOptions { Indented = true });

        json.WriteStartObject();
        json.WriteNumber("schemaVersion", 1);
        json.WriteNumber("scanId", report.ScanId);
        json.WriteString("ranAtUtc", report.RanAtUtc.ToString("O", CultureInfo.InvariantCulture));
        json.WriteString("hashAlgorithm", HashKinds.Name(report.Algorithm));
        json.WriteNumber("minSizeBytes", report.MinBytes);
        json.WriteBoolean("partial", report.Partial);

        json.WriteStartObject("totals");
        json.WriteNumber("groups", report.Duplicates.Count());
        json.WriteNumber("hardlinkSets", report.Hardlinks.Count());
        json.WriteNumber("wastedBytes", report.Wasted);
        json.WriteNumber("candidates", report.Stats.Candidates);
        json.WriteNumber("bytesRead", report.Stats.BytesRead);
        json.WriteNumber("cloudSkipped", report.Stats.CloudSkipped);
        json.WriteNumber("unreadable", report.Stats.Unreadable);
        json.WriteNumber("elapsedMs", (long)report.Stats.Elapsed.TotalMilliseconds);
        json.WriteEndObject();

        json.WriteStartArray("groups");
        foreach (var group in report.Groups)
        {
            json.WriteStartObject();
            json.WriteString("kind", group.Kind == DupeKind.HardlinkSet ? "hardlink_set" : "duplicate");
            json.WriteNumber("sizeBytes", group.Bytes);
            json.WriteNumber("fileCount", group.Count);
            json.WriteNumber("wastedBytes", group.Wasted);
            json.WriteString("hash", group.Hash);

            json.WriteStartArray("files");
            foreach (var file in group.Files)
            {
                json.WriteStartObject();
                json.WriteString("path", file.Path);
                json.WriteNumber("bytes", file.Bytes);
                json.WriteNumber("allocatedBytes", file.Allocated);
                json.WriteString("modifiedUtc", file.ModifiedUtc.ToString("O", CultureInfo.InvariantCulture));
                json.WriteNumber("linkCount", file.LinkCount);
                json.WriteBoolean("keep", file.Keeper);
                json.WriteBoolean("protected", file.Protected);
                if (file.Why is { } why) json.WriteString("why", why);
                json.WriteEndObject();
            }
            json.WriteEndArray();

            json.WriteEndObject();
        }
        json.WriteEndArray();

        json.WriteEndObject();
        json.Flush();

        w.WriteLine();
    }

    private static string Duration(TimeSpan elapsed) => elapsed.TotalSeconds < 90
        ? string.Create(CultureInfo.InvariantCulture, $"{elapsed.TotalSeconds:F1}s")
        : string.Create(CultureInfo.InvariantCulture, $"{(int)elapsed.TotalMinutes}m{elapsed.Seconds:D2}s");
}
