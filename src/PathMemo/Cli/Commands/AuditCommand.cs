using System.Globalization;
using System.Text;
using System.Text.Json;
using PathMemo.Audit;
using PathMemo.Cli.Output;
using PathMemo.Platform;

namespace PathMemo.Cli.Commands;

/// <summary>
/// <c>pathmemo audit</c>: the space no directory walk can see (README section 6).
/// </summary>
/// <remarks>
/// Nothing here changes the system. Remedies are printed, and copied with <c>--copy</c>;
/// running them is the user's act, in their own elevated prompt. <c>--apply</c> arrives
/// with the operations journal in P6, because a command that changes the machine must be
/// logged before it is offered (README section 6.3).
/// </remarks>
internal static class AuditCommand
{
    internal static int Run(AuditOptions options, CancellationToken ct)
    {
        var probes = AuditRunner.AllProbes();

        if (options.Id is { } id)
        {
            probes = probes.Where(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase)).ToList();
            if (probes.Count == 0)
            {
                Console.Error.WriteLine($"pathmemo: no audit probe named '{id}'");
                Console.Error.WriteLine("Known probes: " + string.Join(", ", AuditRunner.AllProbes().Select(p => p.Id)));
                return ExitCode.Usage;
            }
        }

        var progress = options.Quiet || options.Json || Console.IsErrorRedirected
            ? null
            : new Progress<string>(title => Console.Error.Write($"\r  probing {title,-40}"));

        var report = AuditRunner.Run(probes, progress, ct);

        if (progress is not null) Console.Error.Write("\r" + new string(' ', 52) + "\r");

        if (options.Json)
        {
            WriteJson(report, options.Id is not null);
            return ExitCode.Ok;
        }

        if (options.Id is not null)
        {
            var findings = report.Findings.ToList();
            PrintDetails(findings, report);
            if (options.Copy) CopyRemedy(findings, options.CopyIndex);
            return ExitCode.Ok;
        }

        Print(report, Console.Out);
        return ExitCode.Ok;
    }

    internal static void Print(AuditReport report, TextWriter w)
    {
        var volumes = VolumeInfo.Enumerate(fixedOnly: true);

        w.WriteLine();
        foreach (var v in volumes)
        {
            w.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "Volume {0}   {1} total   ·   {2} free   ·   {3:F1}% used",
                v.Letter, SizeFormat.Bytes(v.TotalBytes), SizeFormat.Bytes(v.FreeBytes), v.UsedPercent));
        }
        w.WriteLine();

        var measured = report.Findings.Where(f => f.Status == FindingStatus.Measured).ToList();

        var reclaimable = measured.Where(f => f.ReclaimableBytes > 0)
            .OrderByDescending(f => f.ReclaimableBytes).ToList();
        var unmeasured = measured.Where(f => f.ReclaimableBytes is null)
            .OrderByDescending(f => f.UsedBytes ?? 0).ToList();
        var fixedSize = measured.Where(f => f.ReclaimableBytes == 0 && f.UsedBytes > 0)
            .OrderByDescending(f => f.UsedBytes).ToList();
        var blocked = report.Findings
            .Where(f => f.Status is FindingStatus.NeedsElevation or FindingStatus.Unknown
                                  or FindingStatus.Error or FindingStatus.NoSnapshot)
            .ToList();

        Section(w, "RECLAIMABLE", reclaimable.Sum(f => f.ReclaimableBytes ?? 0));
        if (reclaimable.Count == 0) w.WriteLine("  nothing measurable to reclaim");
        foreach (var f in reclaimable) PrintFinding(w, f, f.ReclaimableBytes!.Value);

        if (unmeasured.Count > 0)
        {
            Section(w, "WORTH A LOOK  (how much comes back is not knowable)",
                unmeasured.Sum(f => f.UsedBytes ?? 0));
            foreach (var f in unmeasured) PrintFinding(w, f, f.UsedBytes, brief: true);
        }

        if (fixedSize.Count > 0)
        {
            Section(w, "NOT RECLAIMABLE", fixedSize.Sum(f => f.UsedBytes ?? 0));
            foreach (var f in fixedSize)
                w.WriteLine(string.Format(CultureInfo.InvariantCulture, "  {0,-42} {1,9}",
                    Fit(f.Title + VolumeSuffix(f), 42), SizeFormat.Bytes(f.UsedBytes ?? 0)));
        }

        if (blocked.Count > 0)
        {
            w.WriteLine();
            w.WriteLine("  NOT MEASURED");
            w.WriteLine("  " + new string('-', 62));
            foreach (var f in blocked)
            {
                var why = f.Status switch
                {
                    FindingStatus.NeedsElevation => "needs administrator",
                    FindingStatus.NoSnapshot => "needs a scan",
                    FindingStatus.Error => "failed",
                    _ => "unknown",
                };
                w.WriteLine(string.Format(CultureInfo.InvariantCulture, "  {0,-42} {1}",
                    Fit(f.Title + VolumeSuffix(f), 42), why));
                if (f.Note is { } note && f.Status != FindingStatus.NeedsElevation)
                    w.WriteLine("    " + note);
            }

            if (!report.Elevated && blocked.Any(f => f.Status == FindingStatus.NeedsElevation))
            {
                w.WriteLine();
                w.WriteLine("  Restore points and the component store are usually the two largest items.");
                w.WriteLine("  Run this again as administrator to measure them.");
            }
        }

        w.WriteLine();
        w.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "  {0} probes in {1:F1} s{2}. Nothing was changed.",
            report.Findings.Select(f => f.Id).Distinct().Count(), report.Duration.TotalSeconds,
            report.SnapshotId is { } id ? $" · using scan {id}" : " · no scan yet (some probes need one)"));
        w.WriteLine("  Run 'pathmemo audit --id <name>' for details and every remedy; --copy puts the first command on the clipboard.");
        w.WriteLine();
    }

    private static void Section(TextWriter w, string title, long total)
    {
        w.WriteLine();
        w.WriteLine(string.Format(CultureInfo.InvariantCulture, "  {0,-52} {1,9}", title.Length > 52 ? title[..52] : title, SizeFormat.Bytes(total)));
        w.WriteLine("  " + new string('-', 62));
    }

    private static void PrintFinding(TextWriter w, AuditFinding f, long? amount, bool brief = false)
    {
        w.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "  {0,-42} {1,9}   {2}",
            Fit(f.Title + VolumeSuffix(f), 42),
            amount is { } a ? SizeFormat.Bytes(a) : "?",
            Label(f)));

        foreach (var line in Wrap(f.Explanation, 60)) w.WriteLine("    " + line);
        if (f.Note is { } note) w.WriteLine("    (" + note + ")");

        foreach (var remedy in f.Remedies.Take(brief ? 1 : 2))
            w.WriteLine("    → " + RemedyLine(remedy));

        if (f.Remedies.Count > (brief ? 1 : 2))
            w.WriteLine($"    → {f.Remedies.Count - (brief ? 1 : 2)} more: pathmemo audit --id {f.Id}");

        w.WriteLine();
    }

    private static void PrintDetails(IReadOnlyList<AuditFinding> findings, AuditReport report)
    {
        var w = Console.Out;
        w.WriteLine();

        foreach (var f in findings)
        {
            w.WriteLine($"  {f.Title}{VolumeSuffix(f)}   [{f.Id}]");
            w.WriteLine("  " + new string('-', 62));
            w.WriteLine($"  status         {f.Status.ToString().ToLowerInvariant()}{(f.Note is { } n ? " - " + n : "")}");
            if (f.UsedBytes is { } used) w.WriteLine($"  used           {SizeFormat.Bytes(used)}");
            w.WriteLine($"  reclaimable    {(f.ReclaimableBytes is { } r ? SizeFormat.Bytes(r) : f.Status == FindingStatus.Measured ? "unknown from outside" : "-")}");
            if (f.Status == FindingStatus.Measured) w.WriteLine($"  risk           {Label(f)}");
            w.WriteLine();
            foreach (var line in Wrap(f.Explanation, 66)) w.WriteLine("  " + line);

            if (f.Remedies.Count > 0)
            {
                w.WriteLine();
                w.WriteLine("  REMEDIES");
                var number = 1;
                foreach (var remedy in f.Remedies)
                {
                    w.WriteLine($"  {number++}. {RemedyLine(remedy)}");
                    if (remedy.Caveat is { } caveat) w.WriteLine("     ⚠ " + caveat);
                }
            }

            if (f.Paths.Count > 0)
            {
                w.WriteLine();
                w.WriteLine("  PATHS");
                foreach (var path in f.Paths) w.WriteLine("  " + path);
            }

            w.WriteLine();
        }

        w.WriteLine(string.Format(CultureInfo.InvariantCulture, "  measured in {0:F1} s. Nothing was changed.", report.Duration.TotalSeconds));
        w.WriteLine();
    }

    private static void CopyRemedy(IReadOnlyList<AuditFinding> findings, int index)
    {
        var commands = findings
            .SelectMany(f => f.Remedies)
            .Where(r => r.Kind == RemedyKind.RunCommand)
            .ToList();

        if (commands.Count == 0)
        {
            Console.Error.WriteLine("pathmemo: this finding has no command to copy");
            return;
        }

        var chosen = commands[Math.Clamp(index - 1, 0, commands.Count - 1)];

        if (Clipboard.TrySetText(chosen.Display, out var error))
            Console.Error.WriteLine($"copied: {chosen.Display}{(chosen.NeedsElevation ? "   (run it from an administrator prompt)" : "")}");
        else
            Console.Error.WriteLine($"pathmemo: could not copy - {error}. The command is printed above.");
    }

    private static void WriteJson(AuditReport report, bool single)
    {
        using var stream = Console.OpenStandardOutput();
        using var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });

        json.WriteStartObject();
        json.WriteNumber("schemaVersion", 1);
        json.WriteString("startedUtc", report.StartedUtc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
        json.WriteNumber("durationMs", (long)report.Duration.TotalMilliseconds);
        json.WriteBoolean("elevated", report.Elevated);
        if (report.SnapshotId is { } id) json.WriteNumber("scanId", id); else json.WriteNull("scanId");
        json.WriteNumber("reclaimableBytes", report.ReclaimableTotal);

        json.WriteStartArray("findings");
        foreach (var f in report.Findings)
        {
            json.WriteStartObject();
            json.WriteString("id", f.Id);
            json.WriteString("title", f.Title);
            json.WriteString("status", Camel(f.Status.ToString()));
            json.WriteString("volume", f.Volume);
            if (f.UsedBytes is { } used) json.WriteNumber("usedBytes", used); else json.WriteNull("usedBytes");
            if (f.ReclaimableBytes is { } r) json.WriteNumber("reclaimableBytes", r); else json.WriteNull("reclaimableBytes");
            json.WriteString("risk", Camel(f.Risk.ToString()));
            json.WriteString("recoverability", Camel(f.Recoverability.ToString()));
            json.WriteString("explanation", f.Explanation);
            if (f.Note is { } note) json.WriteString("note", note);

            json.WriteStartArray("remedies");
            foreach (var remedy in f.Remedies)
            {
                json.WriteStartObject();
                json.WriteString("kind", Camel(remedy.Kind.ToString()));
                json.WriteString("display", remedy.Display);
                json.WriteBoolean("needsElevation", remedy.NeedsElevation);
                json.WriteBoolean("needsReboot", remedy.NeedsReboot);
                if (remedy.Caveat is { } caveat) json.WriteString("caveat", caveat);
                json.WriteEndObject();
            }
            json.WriteEndArray();

            json.WriteStartArray("paths");
            foreach (var path in f.Paths) json.WriteStringValue(path);
            json.WriteEndArray();

            json.WriteNumber("elapsedMs", (long)f.Elapsed.TotalMilliseconds);
            json.WriteEndObject();
        }
        json.WriteEndArray();

        json.WriteEndObject();
        json.Flush();
        stream.Write("\n"u8);
    }

    private static string Camel(string pascal) =>
        pascal.Length == 0 ? pascal : char.ToLowerInvariant(pascal[0]) + pascal[1..];

    internal static string Label(AuditFinding f) =>
        f.Risk.ToString().ToLowerInvariant() + " · " + f.Recoverability.ToString().ToLowerInvariant();

    internal static string RemedyLine(Remedy remedy)
    {
        var tags = new StringBuilder();
        if (remedy.NeedsElevation) tags.Append("  [admin]");
        if (remedy.NeedsReboot) tags.Append("  [reboot]");

        var prefix = remedy.Kind switch
        {
            RemedyKind.OpenSettings => "open: ",
            RemedyKind.Manual => "",
            RemedyKind.DeletePaths => "",
            _ => "",
        };

        return prefix + remedy.Display + tags;
    }

    private static string VolumeSuffix(AuditFinding f) => f.Volume.Length > 0 ? "  " + f.Volume : "";

    private static string Fit(string text, int width)
    {
        var actual = PathDisplay.Width(text);
        return actual <= width ? text + new string(' ', width - actual) : text[..(width - 1)] + "~";
    }

    internal static IEnumerable<string> Wrap(string text, int width)
    {
        var line = new StringBuilder();
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length > 0 && line.Length + 1 + word.Length > width)
            {
                yield return line.ToString();
                line.Clear();
            }

            if (line.Length > 0) line.Append(' ');
            line.Append(word);
        }

        if (line.Length > 0) yield return line.ToString();
    }
}

internal sealed record AuditOptions
{
    internal string? Id { get; init; }
    internal bool Json { get; init; }
    internal bool Quiet { get; init; }
    internal bool Copy { get; init; }
    internal int CopyIndex { get; init; } = 1;
}
