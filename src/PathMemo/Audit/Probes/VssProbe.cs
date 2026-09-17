using System.Globalization;
using System.Text.Json;
using static PathMemo.Audit.Probes.ProbeHelpers;

namespace PathMemo.Audit.Probes;

/// <summary>
/// Volume Shadow Copies: restore points and "Previous Versions". Often the single largest
/// invisible consumer, and invisible to every directory walk because it lives inside
/// <c>System Volume Information</c>.
/// </summary>
/// <remarks>
/// Read through CIM (<c>Win32_ShadowStorage</c>, <c>Win32_ShadowCopy</c>) rather than by
/// parsing <c>vssadmin</c>: CIM returns byte counts as integers in every display language,
/// while vssadmin prints "12.5 GB" in English and "12,5 ГБ" in Russian. Both need an
/// elevated process; without one the probe says so instead of guessing.
/// </remarks>
internal sealed class VssProbe : IAuditProbe
{
    public string Id => "vss.shadow-storage";
    public string Title => "Volume Shadow Copies";

    private const string Query = """
        $s = @(Get-CimInstance -ClassName Win32_ShadowStorage -ErrorAction Stop | ForEach-Object {
            [pscustomobject]@{
                volume    = $_.Volume.DeviceID
                diffVolume = $_.DiffVolume.DeviceID
                used      = [uint64]$_.UsedSpace
                allocated = [uint64]$_.AllocatedSpace
                max       = [uint64]$_.MaxSpace
            } });
        $c = @(Get-CimInstance -ClassName Win32_ShadowCopy -ErrorAction SilentlyContinue | ForEach-Object {
            [pscustomobject]@{
                volume  = $_.VolumeName
                created = $_.InstallDate.ToUniversalTime().ToString('o')
            } });
        ConvertTo-Json -Compress -Depth 3 -InputObject @{ storage = $s; copies = $c }
        """;

    public IEnumerable<AuditFinding> Run(AuditContext context)
    {
        if (!context.Elevated)
        {
            yield return AuditFinding.NeedsElevation(Id, Title,
                "Restore points and Previous Versions live inside System Volume Information, " +
                "which only an elevated process can measure.");
            yield break;
        }

        var output = ExternalTool.PowerShellCommand(Query, context.Cancellation);
        if (!output.Succeeded)
        {
            yield return Unknown(output.TimedOut ? output.StdErr : FirstLine(output.StdErr));
            yield break;
        }

        if (!TryParse(output.StdOut, out var state, out var parseError))
        {
            yield return Unknown("unexpected output: " + parseError);
            yield break;
        }

        // Volume GUID paths on both sides; map to letters through the volume list.
        foreach (var volume in context.Volumes)
        {
            var storage = state.Storage.Where(s => SameVolume(s.DiffVolume, volume)).ToList();
            if (storage.Count == 0) continue;

            var used = storage.Sum(s => s.Used);
            var max = storage.Sum(s => s.Max);
            var copies = state.Copies.Where(c => SameVolume(c.Volume, volume)).ToList();
            var oldest = copies.Count > 0 ? copies.Min(c => c.Created) : (DateTime?)null;

            var letter = volume.Letter;
            yield return new AuditFinding
            {
                Id = Id,
                Title = Title,
                Status = FindingStatus.Measured,
                Volume = letter,
                UsedBytes = used,
                ReclaimableBytes = used,
                Risk = Risk.Caution,
                Recoverability = Recoverability.Irreversible,
                Explanation = string.Format(CultureInfo.InvariantCulture,
                    "{0} restore point{1}{2} on {3}. Windows keeps them for System Restore and " +
                    "Previous Versions; the quota is {4}. Deleting the oldest keeps the newest.",
                    copies.Count, copies.Count == 1 ? "" : "s",
                    oldest is { } o ? $", oldest {o:yyyy-MM-dd}" : "",
                    letter,
                    max == long.MaxValue || max == 0 ? "unbounded" : Size(max)),
                Remedies =
                [
                    Elevated($"vssadmin delete shadows /for={letter} /oldest",
                        "Removes one restore point; repeat to remove more"),
                    Elevated($"vssadmin resize shadowstorage /for={letter} /on={letter} /maxsize=10GB",
                        "Windows deletes the oldest copies until the new quota fits"),
                    Elevated($"vssadmin delete shadows /for={letter} /all",
                        "Removes every restore point on the volume"),
                ],
            };
        }
    }

    private AuditFinding Unknown(string note) => new()
    {
        Id = Id, Title = Title, Status = FindingStatus.Unknown,
        Explanation = "Shadow storage could not be read.",
        Note = note,
    };

    private static string FirstLine(string text)
    {
        var trimmed = text.Trim();
        var end = trimmed.IndexOfAny(['\r', '\n']);
        return end < 0 ? trimmed : trimmed[..end];
    }

    private static bool SameVolume(string deviceId, Platform.VolumeInfo volume)
    {
        if (string.IsNullOrEmpty(deviceId)) return false;

        // "\\?\Volume{guid}\" from CIM versus "\\?\Volume{guid}\" from the mount point.
        if (volume.VolumeGuidPath is { } guid &&
            deviceId.TrimEnd('\\').Equals(guid.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            return true;

        // Some CIM providers answer with the drive letter form instead.
        return deviceId.TrimEnd('\\').Equals(volume.Letter, StringComparison.OrdinalIgnoreCase);
    }

    internal sealed record VssStorage(string Volume, string DiffVolume, long Used, long Allocated, long Max);
    internal sealed record VssCopy(string Volume, DateTime Created);
    internal sealed record VssState(IReadOnlyList<VssStorage> Storage, IReadOnlyList<VssCopy> Copies);

    private static bool TryParse(string json, out VssState state, out string error)
    {
        try
        {
            state = Parse(json);
            error = "";
            return true;
        }
        catch (JsonException ex)
        {
            state = null!;
            error = ex.Message;
            return false;
        }
    }

    /// <summary><see cref="JsonDocument"/>, not a serializer: no reflection to trim around.</summary>
    internal static VssState Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        var storage = new List<VssStorage>();
        if (root.TryGetProperty("storage", out var s) && s.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in s.EnumerateArray())
            {
                storage.Add(new VssStorage(
                    Str(item, "volume"),
                    Str(item, "diffVolume"),
                    Num(item, "used"),
                    Num(item, "allocated"),
                    Num(item, "max")));
            }
        }

        var copies = new List<VssCopy>();
        if (root.TryGetProperty("copies", out var c) && c.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in c.EnumerateArray())
            {
                var created = DateTime.TryParse(Str(item, "created"), CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var when)
                    ? when
                    : DateTime.MinValue;
                copies.Add(new VssCopy(Str(item, "volume"), created));
            }
        }

        return new VssState(storage, copies);
    }

    private static string Str(JsonElement element, string name) =>
        element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static long Num(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var v)) return 0;
        if (v.ValueKind == JsonValueKind.Number)
            return v.TryGetInt64(out var l) ? l : v.TryGetUInt64(out var u) ? (long)Math.Min(u, long.MaxValue) : 0;
        if (v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), CultureInfo.InvariantCulture, out var parsed))
            return parsed;
        return 0;
    }
}
