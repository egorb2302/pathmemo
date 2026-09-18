using System.Globalization;
using System.Text.Json;
using PathMemo.Analysis;
using PathMemo.Audit;

namespace PathMemo.Cli.Output;

/// <summary>
/// The reclaim report as text and as JSON (README sections 7.3, 13.6).
/// </summary>
/// <remarks>
/// Two numbers per row, because they are different questions: <c>SIZE</c> is what the rule
/// found and <c>RECLAIM</c> is what deleting it actually gives back. Where they differ the
/// note column says why - a hard-linked package store looks like gigabytes and frees
/// nothing while the projects that link into it are still there (README section 3.2).
/// </remarks>
internal static class ReclaimTable
{
    internal static void Print(ReclaimReport report, Risk ceiling, TextWriter w)
    {
        w.WriteLine();

        if (report.IsEmpty)
        {
            w.WriteLine($"No cleanup rule matched anything in scan {report.ScanId}.");
            PrintFootnotes(report, w);
            return;
        }

        var width = ConsoleWidth();
        var noteRoom = Math.Max(10, Math.Min(46, width - 78));
        var rule = new string('-', Math.Min(width - 4, 76 + noteRoom));

        w.WriteLine("  RULE                       MATCHES      SIZE   RECLAIM   RISK     RECOVERY      NOTE");
        w.WriteLine("  " + rule);

        foreach (var group in report.Groups)
        {
            w.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  {Fit(group.Rule.Id, 25),-25} {group.Count,8}  {SizeFormat.Bytes(group.Bytes),8}  "
                + $"{SizeFormat.Bytes(group.Reclaimable),8}   {ReclaimNames.Of(group.Rule.Risk),-8} "
                + $"{ReclaimNames.Of(group.Rule.Recoverability),-13} {Fit(Note(group), noteRoom)}"));
        }

        w.WriteLine("  " + rule);

        Total(w, "safe only", report, Risk.Safe);
        if (report.Groups.Any(g => g.Rule.Risk == Risk.Caution)) Total(w, "including caution", report, Risk.Caution);
        if (report.Groups.Any(g => g.Rule.Risk == Risk.Danger)) Total(w, "including danger", report, Risk.Danger);

        PrintFootnotes(report, w);

        var offered = report.OfferedAtMost(ceiling);
        w.WriteLine();

        if (offered > 0)
        {
            var scan = $" --scan {report.ScanId.ToString(CultureInfo.InvariantCulture)}";
            var risk = ceiling == Risk.Safe ? "" : $" --risk {ReclaimNames.Of(ceiling)}";

            w.WriteLine($"  pathmemo reclaim{scan}{risk} --dry-run    preview what would be deleted");
            w.WriteLine($"  pathmemo reclaim{scan}{risk} --apply      quarantine it "
                      + $"({SizeFormat.Bytes(offered)})");
        }
        else
        {
            w.WriteLine("  Nothing here is offered for deletion: see the notes above.");
        }

        w.WriteLine($"  pathmemo reclaim --rule <id>          the paths behind one row");
    }

    private static void Total(TextWriter w, string label, ReclaimReport report, Risk ceiling)
    {
        var groups = report.AtMost(ceiling).ToList();
        if (groups.Count == 0) return;

        w.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"  {label,-25} {groups.Sum(g => g.Count),8}  {SizeFormat.Bytes(groups.Sum(g => g.Bytes)),8}  "
            + $"{SizeFormat.Bytes(groups.Sum(g => g.Reclaimable)),8}"));
    }

    /// <summary>The one thing about this row the two size columns cannot say.</summary>
    private static string Note(ReclaimGroup group)
    {
        if (group.Rule.Action == ReclaimAction.Command) return "use " + group.Rule.Command;
        if (group.Rule.Action == ReclaimAction.Manual) return "decide by hand";

        if (group.RefusedCount > 0)
            return group.RefusedCount == group.Count
                ? "all refused by the guard"
                : $"{group.RefusedCount} refused by the guard";

        if (group.SharedBytes > 0) return $"{SizeFormat.Bytes(group.SharedBytes)} shared via hard links";
        if (group.Rule.ContentsOnly) return "contents only; the directory stays";
        if (group.Rule.Custom) return "from config.json";

        return group.Rule.Command is { } command ? command : "";
    }

    private static void PrintFootnotes(ReclaimReport report, TextWriter w)
    {
        if (report.KeptCount > 0)
            w.WriteLine($"  {report.KeptCount} match{(report.KeptCount == 1 ? "" : "es")} "
                      + $"({SizeFormat.Bytes(report.KeptBytes)}) held back by protect.keep");

        if (report.DisabledRules.Count > 0)
            w.WriteLine("  disabled in config.json: " + string.Join(", ", report.DisabledRules));
    }

    /// <summary>The paths behind one rule (<c>--rule</c>).</summary>
    internal static void PrintDetail(ReclaimGroup group, ReclaimReport report, int limit, TextWriter w)
    {
        var rule = group.Rule;

        w.WriteLine();
        w.WriteLine($"{rule.Id}   {ReclaimNames.Of(rule.Risk)} · {ReclaimNames.Of(rule.Recoverability)}");
        if (rule.What.Length > 0) w.WriteLine($"  {rule.What}");

        w.WriteLine($"  patterns: {string.Join("  ", rule.Patterns)}");

        w.WriteLine("  " + rule.Action switch
        {
            ReclaimAction.Command => $"pathmemo will not delete this - run: {rule.Command}",
            ReclaimAction.Manual => "reported only; deleting this is a decision, not a rule",
            _ => rule.Command is { } command
                ? $"pathmemo can delete it; the tidy way is: {command}"
                : "pathmemo can delete it",
        });

        w.WriteLine();
        w.WriteLine("       SIZE   RECLAIM  MODIFIED    PATH");

        foreach (var match in group.Matches.Take(limit))
        {
            w.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  {SizeFormat.Bytes(match.Bytes),9} {SizeFormat.Bytes(match.Reclaimable),9}  "
                + $"{match.ModifiedUtc:yyyy-MM-dd}  {PathDisplay.Shorten(match.Path, 64)}"));

            if (match.Refusal is { } refusal) w.WriteLine($"                          refused: {refusal}");
        }

        if (group.Count > limit) w.WriteLine($"  ... and {group.Count - limit} more (--limit)");

        w.WriteLine();
        w.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"  {group.Count} match{(group.Count == 1 ? "" : "es")} · {SizeFormat.Bytes(group.Bytes)} · "
            + $"{SizeFormat.Bytes(group.Reclaimable)} reclaimable · scan {report.ScanId}"));
    }

    /// <summary>The rule set itself, with no scan behind it (<c>--list</c>).</summary>
    internal static void PrintRules(IReadOnlyList<ReclaimRule> rules, IReadOnlyList<string> disabled, TextWriter w)
    {
        w.WriteLine();
        w.WriteLine("  RULE                       RISK     RECOVERY      WHAT");
        w.WriteLine("  " + new string('-', 96));

        foreach (var rule in rules.OrderBy(r => r.Id, StringComparer.OrdinalIgnoreCase))
        {
            w.WriteLine($"  {Fit(rule.Id, 25),-25}  {ReclaimNames.Of(rule.Risk),-8} "
                      + $"{ReclaimNames.Of(rule.Recoverability),-13} {Fit(rule.What, 44)}"
                      + (rule.Custom ? "  (config.json)" : ""));
        }

        if (disabled.Count > 0)
        {
            w.WriteLine();
            w.WriteLine("  disabled: " + string.Join(", ", disabled));
        }

        w.WriteLine();
        w.WriteLine($"  {rules.Count} rules · pathmemo reclaim --rule <id> to see what one of them matches");
    }

    internal static void WriteJson(ReclaimReport report, Risk ceiling, TextWriter w)
    {
        using var json = new Utf8JsonWriter(
            Console.OpenStandardOutput(), new JsonWriterOptions { Indented = true });

        json.WriteStartObject();
        json.WriteNumber("schemaVersion", 1);
        json.WriteNumber("scanId", report.ScanId);
        json.WriteString("scanStartedUtc", report.ScanStartedUtc.ToString("O", CultureInfo.InvariantCulture));
        json.WriteNumber("keptCount", report.KeptCount);
        json.WriteNumber("keptBytes", report.KeptBytes);

        json.WriteStartObject("totals");
        json.WriteNumber("safeBytes", report.ReclaimableAtMost(Risk.Safe));
        json.WriteNumber("cautionBytes", report.ReclaimableAtMost(Risk.Caution));
        json.WriteNumber("offeredBytes", report.OfferedAtMost(ceiling));
        json.WriteEndObject();

        json.WriteStartArray("rules");
        foreach (var group in report.Groups)
        {
            json.WriteStartObject();
            json.WriteString("id", group.Rule.Id);
            json.WriteString("risk", ReclaimNames.Of(group.Rule.Risk));
            json.WriteString("recoverability", ReclaimNames.Of(group.Rule.Recoverability));
            json.WriteString("action", group.Rule.Action.ToString().ToLowerInvariant());
            if (group.Rule.Command is { } command) json.WriteString("command", command);
            json.WriteNumber("matches", group.Count);
            json.WriteNumber("bytes", group.Bytes);
            json.WriteNumber("reclaimableBytes", group.Reclaimable);
            json.WriteNumber("sharedBytes", group.SharedBytes);

            json.WriteStartArray("paths");
            foreach (var match in group.Matches)
            {
                json.WriteStartObject();
                json.WriteString("path", match.Path);
                json.WriteNumber("bytes", match.Bytes);
                json.WriteNumber("reclaimableBytes", match.Reclaimable);
                json.WriteBoolean("directory", match.IsDirectory);
                if (match.Refusal is { } refusal) json.WriteString("refusal", refusal);
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

    private static string Fit(string text, int width) =>
        text.Length <= width ? text : text[..Math.Max(1, width - 1)] + "~";

    private static int ConsoleWidth()
    {
        try { return Console.IsOutputRedirected ? 120 : Math.Max(80, Console.WindowWidth); }
        catch (IOException) { return 120; }
    }
}
