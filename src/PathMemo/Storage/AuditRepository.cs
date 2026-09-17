using System.Text;
using System.Text.Json;
using PathMemo.Audit;

namespace PathMemo.Storage;

/// <summary>
/// Stores what the Space Audit found, so "when did the component store start growing"
/// has an answer (README sections 6, 11).
/// </summary>
/// <remarks>
/// Only measured findings are worth rows. A probe that returned <c>NotApplicable</c>
/// because this machine has no WSL is not a data point, and thousands of them would
/// bury the ones that are.
/// </remarks>
internal sealed class AuditRepository(Database database)
{
    private readonly Database _db = database;

    internal int Save(AuditReport report)
    {
        // The audit reads the newest snapshot, which may have no row yet if the database
        // was created after it. Attaching the findings to a scan that does not exist
        // would fail the foreign key, so an unknown id becomes no id.
        var scanId = report.SnapshotId is { } candidate && new ScanRepository(_db).Find(candidate) is not null
            ? candidate
            : (long?)null;

        using var transaction = _db.Begin();

        using var command = _db.Command("""
            INSERT INTO audit_findings (scan_id, probed_at, finding_id, volume, used_bytes,
                                        reclaimable_bytes, risk, recoverability, status, detail_json)
            VALUES ($scan, $probed, $finding, $volume, $used, $reclaimable, $risk, $recoverability,
                    $status, $detail)
            """);
        command.Transaction = transaction;

        var written = 0;
        foreach (var finding in report.Findings)
        {
            if (finding.Status is FindingStatus.NotApplicable or FindingStatus.NoSnapshot) continue;

            command.Parameters.Clear();
            command.Parameters.AddWithValue("$scan", scanId ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("$probed", Iso.Of(report.StartedUtc));
            command.Parameters.AddWithValue("$finding", finding.Id);
            command.Parameters.AddWithValue("$volume",
                finding.Volume.Length == 0 ? (object)DBNull.Value : finding.Volume);
            command.Parameters.AddWithValue("$used", finding.UsedBytes ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("$reclaimable", finding.ReclaimableBytes ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("$risk", Camel(finding.Risk.ToString()));
            command.Parameters.AddWithValue("$recoverability", Camel(finding.Recoverability.ToString()));
            command.Parameters.AddWithValue("$status", Camel(finding.Status.ToString()));
            command.Parameters.AddWithValue("$detail", Detail(finding));
            command.ExecuteNonQuery();
            written++;
        }

        transaction.Commit();
        return written;
    }

    /// <summary>
    /// The parts of a finding that do not deserve columns: its title, its explanation,
    /// the paths behind the number and the remedies that were offered.
    /// </summary>
    private static string Detail(AuditFinding finding)
    {
        using var stream = new MemoryStream(512);
        using (var json = new Utf8JsonWriter(stream))
        {
            json.WriteStartObject();
            json.WriteString("title", finding.Title);
            json.WriteString("explanation", finding.Explanation);
            if (finding.Note is { } note) json.WriteString("note", note);
            json.WriteNumber("elapsedMs", (long)finding.Elapsed.TotalMilliseconds);

            json.WriteStartArray("paths");
            foreach (var path in finding.Paths.Take(64)) json.WriteStringValue(path);
            json.WriteEndArray();

            json.WriteStartArray("remedies");
            foreach (var remedy in finding.Remedies)
            {
                json.WriteStartObject();
                json.WriteString("kind", Camel(remedy.Kind.ToString()));
                json.WriteString("display", remedy.Display);
                json.WriteBoolean("needsElevation", remedy.NeedsElevation);
                json.WriteEndObject();
            }
            json.WriteEndArray();

            json.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string Camel(string pascal) =>
        pascal.Length == 0 ? pascal : char.ToLowerInvariant(pascal[0]) + pascal[1..];
}
