using System.Globalization;
using System.Text.Json;
using PathMemo.Deletion;
using PathMemo.Storage;

namespace PathMemo.Cli.Output;

/// <summary>
/// How a deletion is described to the user, before and after (README sections 9.2, 9.8).
/// </summary>
/// <remarks>
/// The rule this follows: say what will happen without euphemism. "Moved aside, disk space
/// freed after purge" is longer than "deleted" and it is the difference between a user who
/// knows why their free space did not change and one who thinks the tool is broken.
/// </remarks>
internal static class DeleteReport
{
    internal static void PrintPlan(DeletePlan plan, TextWriter w)
    {
        w.WriteLine();

        if (plan.IsEmpty)
        {
            w.WriteLine(plan.Refusals.Count == 0 ? "Nothing to delete." : "Nothing will be deleted.");
            PrintRefusals(plan, w);
            return;
        }

        w.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"Delete {plan.Items.Count} item{(plan.Items.Count == 1 ? "" : "s")} · {SizeFormat.Bytes(plan.TotalBytes)}"));
        w.WriteLine();

        w.WriteLine($"  Mode:   {DeleteModes.Name(plan.Mode)}  ({Explain(plan.Mode)})");
        if (plan.ModeReason is { } reason) w.WriteLine($"          {reason}");

        w.WriteLine($"  Frees now:      {(plan.FreesNow == 0 ? "0 bytes" : SizeFormat.Bytes(plan.FreesNow))}");

        if (plan.Mode == DeleteMode.Quarantine)
            w.WriteLine($"  Frees on purge: {SizeFormat.Bytes(plan.TotalBytes)}");
        else if (plan.Mode == DeleteMode.Recycle)
            w.WriteLine("  Frees on empty: " + SizeFormat.Bytes(plan.TotalBytes));

        w.WriteLine("  Undo:   " + plan.Mode switch
        {
            DeleteMode.Quarantine => "pathmemo restore <op-id>   (until purged)",
            DeleteMode.Recycle => "Explorer -> Recycle Bin -> Restore",
            _ => "none",
        });

        if (plan.Items.Any(i => i.MeasureErrors > 0))
            w.WriteLine("  Note:   part of a directory could not be read; its size is a lower bound");

        w.WriteLine();

        foreach (var item in plan.Items.OrderByDescending(i => i.Bytes).Take(20))
        {
            w.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  {SizeFormat.Bytes(item.Bytes),9}  {Kind(item)}  {PathDisplay.Shorten(item.DisplayPath, 58)}"));
        }

        if (plan.Items.Count > 20)
            w.WriteLine($"  … and {plan.Items.Count - 20} more");

        PrintRefusals(plan, w);
    }

    private static string Kind(PlanItem item) =>
        item.IsReparsePoint ? "link" : item.IsDirectory ? "dir " : "file";

    private static void PrintRefusals(DeletePlan plan, TextWriter w)
    {
        if (plan.Refusals.Count == 0) return;

        w.WriteLine();
        w.WriteLine($"  Refused ({plan.Refusals.Count}):");

        foreach (var refusal in plan.Refusals.Take(20))
            w.WriteLine($"    {PathDisplay.Shorten(refusal.Path, 48)}  -  {refusal.Reason}");

        if (plan.Refusals.Count > 20)
            w.WriteLine($"    … and {plan.Refusals.Count - 20} more");
    }

    private static string Explain(DeleteMode mode) => mode switch
    {
        DeleteMode.Quarantine => "moved aside, disk space freed after purge",
        DeleteMode.Recycle => "into the Recycle Bin, on the same volume - frees nothing now",
        _ => "unlinked now, nothing brings it back",
    };

    internal static void PrintOutcome(DeletePlan plan, OpOutcome outcome, TextWriter w)
    {
        w.WriteLine();
        w.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Done in {outcome.Elapsed.TotalSeconds:F1} s"));

        var verb = outcome.Mode switch
        {
            DeleteMode.Quarantine => "moved to quarantine",
            DeleteMode.Recycle => "sent to the Recycle Bin",
            _ => "deleted",
        };

        w.WriteLine($"  {outcome.Succeeded} item{(outcome.Succeeded == 1 ? "" : "s")} {verb}"
                  + (outcome.Failed > 0 ? $", {outcome.Failed} failed" : "")
                  + (outcome.Skipped > 0 ? $", {outcome.Skipped} skipped" : ""));

        if (outcome.MeasuredDelta is { } freed)
        {
            var expected = outcome.Mode == DeleteMode.Permanent ? outcome.DoneBytes : 0;
            w.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  Predicted:  {SizeFormat.Bytes(expected),-10}  Actual free space change:  {SizeFormat.Bytes(freed)}  {Verdict(expected, freed)}"));
        }

        foreach (var item in outcome.Items.Where(i => i.Result != ItemResult.Ok).Take(20))
            w.WriteLine($"  ! {PathDisplay.Shorten(item.Item.DisplayPath, 48)}  -  {item.Message}");

        if (outcome.Mode == DeleteMode.Quarantine && outcome.Succeeded > 0)
        {
            w.WriteLine();
            w.WriteLine($"  pathmemo restore {outcome.OpId}    to put it back"
                      + (outcome.PurgeAfter is { } due ? $" (until {due:yyyy-MM-dd})" : ""));
            w.WriteLine($"  pathmemo purge {outcome.OpId}      to reclaim {SizeFormat.Bytes(outcome.DoneBytes)}");
        }
    }

    /// <summary>
    /// Compares what was predicted with what the volume actually reports. A divergence over
    /// a tenth is the only way to notice that the size model is lying (README section 9.8).
    /// </summary>
    private static string Verdict(long expected, long actual)
    {
        if (expected == 0) return actual is > -(1 << 20) and < (1 << 20) ? "(as expected)" : "(other activity on the volume)";

        var ratio = (double)actual / expected;
        return ratio is > 0.9 and < 1.1 ? "(as expected)" : "(!) differs from the prediction by more than 10%";
    }

    internal static void WritePlanJson(DeletePlan plan, TextWriter w)
    {
        using var stream = new MemoryStream(1024);
        using (var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            json.WriteStartObject();
            json.WriteNumber("schemaVersion", 1);
            json.WriteString("mode", DeleteModes.Name(plan.Mode));
            if (plan.ModeReason is { } reason) json.WriteString("modeReason", reason);
            json.WriteString("token", plan.Token);
            json.WriteNumber("itemCount", plan.Items.Count);
            json.WriteNumber("fileCount", plan.TotalFiles);
            json.WriteNumber("totalBytes", plan.TotalBytes);
            json.WriteNumber("freesNowBytes", plan.FreesNow);

            WriteItems(json, plan);
            json.WriteEndObject();
        }

        w.WriteLine(System.Text.Encoding.UTF8.GetString(stream.ToArray()));
    }

    private static void WriteItems(Utf8JsonWriter json, DeletePlan plan)
    {
        json.WriteStartArray("items");
        foreach (var item in plan.Items)
        {
            json.WriteStartObject();
            json.WriteString("path", item.DisplayPath);
            json.WriteNumber("bytes", item.Bytes);
            json.WriteNumber("files", item.FileCount);
            json.WriteBoolean("directory", item.IsDirectory);
            json.WriteEndObject();
        }
        json.WriteEndArray();

        json.WriteStartArray("refused");
        foreach (var refusal in plan.Refusals)
        {
            json.WriteStartObject();
            json.WriteString("path", refusal.Path);
            json.WriteString("reason", refusal.Reason);
            json.WriteEndObject();
        }
        json.WriteEndArray();
    }

    internal static void WriteOutcomeJson(DeletePlan plan, OpOutcome outcome, TextWriter w)
    {
        using var stream = new MemoryStream(1024);
        using (var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            json.WriteStartObject();
            json.WriteNumber("schemaVersion", 1);
            json.WriteNumber("opId", outcome.OpId);
            json.WriteString("mode", DeleteModes.Name(outcome.Mode));
            json.WriteString("status", outcome.Status);
            json.WriteNumber("succeeded", outcome.Succeeded);
            json.WriteNumber("failed", outcome.Failed);
            json.WriteNumber("predictedBytes", outcome.PredictedBytes);

            if (outcome.ActualFreedBytes is { } freed) json.WriteNumber("actualFreedBytes", freed);
            if (outcome.QuarantinePath is { } path) json.WriteString("quarantinePath", path);
            if (outcome.PurgeAfter is { } due) json.WriteString("purgeAfterUtc", due.ToString("u", CultureInfo.InvariantCulture));

            json.WriteStartArray("results");
            foreach (var item in outcome.Items)
            {
                json.WriteStartObject();
                json.WriteString("path", item.Item.DisplayPath);
                json.WriteNumber("bytes", item.Item.Bytes);
                json.WriteString("result", item.Result.ToString().ToLowerInvariant());
                if (item.Message is { } message) json.WriteString("message", message);
                json.WriteEndObject();
            }
            json.WriteEndArray();

            json.WriteStartArray("refused");
            foreach (var refusal in plan.Refusals)
            {
                json.WriteStartObject();
                json.WriteString("path", refusal.Path);
                json.WriteString("reason", refusal.Reason);
                json.WriteEndObject();
            }
            json.WriteEndArray();

            json.WriteEndObject();
        }

        w.WriteLine(System.Text.Encoding.UTF8.GetString(stream.ToArray()));
    }

    /// <summary>The journal, as <c>pathmemo ops</c> prints it (README section 9.7).</summary>
    internal static void PrintOps(IReadOnlyList<DeleteOpRow> rows, TextWriter w)
    {
        if (rows.Count == 0)
        {
            w.WriteLine("No deletions recorded yet.");
            return;
        }

        w.WriteLine();
        foreach (var row in rows)
        {
            w.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"op-{row.Id:D6}  {row.StartedAt:yyyy-MM-ddTHH:mm:ssZ}  {row.Mode,-10}  "
                + $"{row.ItemCount,4} item{(row.ItemCount == 1 ? " " : "s")}  {SizeFormat.Bytes(row.PredictedBytes),9}  "
                + $"reclaimed {SizeFormat.Bytes(row.ActualFreedBytes ?? 0)}"));

            var line = $"  status: {row.Status}";
            if (row is { Status: "completed", Mode: "quarantine", PurgeAfter: { } due })
                line += $"   ·   restorable until {due:yyyy-MM-dd}";
            if (row.Reason is { } reason) line += $"   ·   {reason}";

            w.WriteLine(line);
        }

        w.WriteLine();
        w.WriteLine($"{rows.Count} operation{(rows.Count == 1 ? "" : "s")}. 'pathmemo ops <id>' lists the items of one.");
    }

    internal static void PrintOpDetail(DeleteOpRow row, IReadOnlyList<DeleteItemRow> items, TextWriter w)
    {
        PrintOps([row], w);

        w.WriteLine();
        foreach (var item in items)
        {
            w.WriteLine($"  {item.Result,-8} {SizeFormat.Bytes(item.SizeBytes),9}  "
                      + PathDisplay.Shorten(item.OriginalPath, 52)
                      + (item.Message is { } message ? $"  -  {message}" : ""));
        }
    }
}
