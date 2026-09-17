using System.Globalization;
using PathMemo.Analysis;
using static PathMemo.Audit.Probes.ProbeHelpers;

namespace PathMemo.Audit.Probes;

/// <summary>
/// The component store (<c>WinSxS</c>): what DISM would reclaim from it.
/// </summary>
/// <remarks>
/// The directory's apparent size is a lie twice over - most of it is hard links into
/// <c>System32</c>, and none of the linked part is reclaimable - so only DISM's own
/// analysis is reported as reclaimable. It runs with <c>/English</c>, which pins the
/// output format regardless of display language; the parser still works by line
/// position and number shape, and answers Unknown rather than 0 when the shape is off
/// (README section 6.3).
/// </remarks>
internal sealed class WinSxsProbe : IAuditProbe
{
    public string Id => "winsxs.component-store";
    public string Title => "Component store (WinSxS)";

    public IEnumerable<AuditFinding> Run(AuditContext context)
    {
        var directory = Path.Combine(context.WindowsDirectory, "WinSxS");
        var apparent = ApparentSize(context, directory);

        if (!context.Elevated)
        {
            yield return new AuditFinding
            {
                Id = Id, Title = Title, Status = FindingStatus.NeedsElevation,
                Volume = AuditContext.Letter(directory),
                UsedBytes = apparent,
                Explanation = apparent is { } a
                    ? $"The last scan saw {Size(a)} under WinSxS, but most of that is hard links " +
                      "shared with System32. Only DISM can say how much is actually superseded."
                    : "Only DISM can say how much of the component store is superseded.",
                Note = "run as administrator to measure",
                Paths = [directory],
            };
            yield break;
        }

        var output = ExternalTool.Run("Dism.exe",
            ["/English", "/Online", "/Cleanup-Image", "/AnalyzeComponentStore"],
            context.Cancellation,
            TimeSpan.FromSeconds(60));

        if (!output.Succeeded)
        {
            yield return Unknown(apparent, directory,
                output.TimedOut ? output.StdErr : $"DISM exited with {output.ExitCode}");
            yield break;
        }

        if (!DismAnalysis.TryParse(output.StdOut, out var analysis))
        {
            yield return Unknown(apparent, directory, "DISM output could not be parsed");
            yield break;
        }

        var reclaimable = analysis.BackupsAndDisabledFeatures + analysis.CacheAndTemporary;

        yield return new AuditFinding
        {
            Id = Id,
            Title = Title,
            Status = FindingStatus.Measured,
            Volume = AuditContext.Letter(directory),
            UsedBytes = analysis.ActualSize,
            ReclaimableBytes = reclaimable,
            Risk = Risk.Caution,
            Recoverability = Recoverability.Redownload,
            Explanation = string.Format(CultureInfo.InvariantCulture,
                "DISM reports {0} actually used ({1} shown by Explorer): {2} of superseded " +
                "component backups and {3} of cache. {4} package{5} can be reclaimed; cleanup is {6}.",
                Size(analysis.ActualSize), Size(analysis.ExplorerSize),
                Size(analysis.BackupsAndDisabledFeatures), Size(analysis.CacheAndTemporary),
                analysis.ReclaimablePackages, analysis.ReclaimablePackages == 1 ? "" : "s",
                analysis.CleanupRecommended ? "recommended" : "not recommended by DISM"),
            Remedies =
            [
                Elevated("dism /Online /Cleanup-Image /StartComponentCleanup",
                    "Superseded versions stay available for 30 days after an update; this removes them now"),
                Elevated("dism /Online /Cleanup-Image /StartComponentCleanup /ResetBase",
                    "Also blocks uninstalling every currently installed update"),
            ],
            Paths = [directory],
        };
    }

    private AuditFinding Unknown(long? apparent, string directory, string note) => new()
    {
        Id = Id, Title = Title, Status = FindingStatus.Unknown,
        Volume = AuditContext.Letter(directory),
        UsedBytes = apparent,
        Explanation = "DISM did not report a usable component store analysis.",
        Note = note,
        Paths = [directory],
    };

    private static long? ApparentSize(AuditContext context, string directory)
    {
        if (context.Snapshot is not { } snapshot) return null;
        var node = TreeQuery.Find(snapshot.Tree, directory);
        return node == Snapshots.NodeStore.NoNode ? null : snapshot.Tree.Allocated[node];
    }
}

/// <summary>Parsed <c>DISM /AnalyzeComponentStore</c> output.</summary>
internal sealed record DismAnalysis(
    long ExplorerSize,
    long ActualSize,
    long SharedWithWindows,
    long BackupsAndDisabledFeatures,
    long CacheAndTemporary,
    int ReclaimablePackages,
    bool CleanupRecommended)
{
    /// <summary>
    /// The report is a fixed sequence of "label : value" lines whose order DISM has kept
    /// since Windows 8.1. The first five values are sizes, the sixth is a date, the seventh
    /// a count and the eighth yes/no. Labels are not consulted.
    /// </summary>
    internal static bool TryParse(string text, out DismAnalysis analysis)
    {
        analysis = null!;

        var values = new List<string>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            var colon = line.IndexOf(" : ", StringComparison.Ordinal);
            if (colon <= 0) continue;
            values.Add(line[(colon + 3)..].Trim());
        }

        if (values.Count < 8) return false;

        var sizes = new long[5];
        for (var i = 0; i < 5; i++)
            if (!TryParseSize(values[i], out sizes[i])) return false;

        if (!int.TryParse(values[6], NumberStyles.Integer, CultureInfo.InvariantCulture, out var packages))
            return false;

        var recommended = values[7].StartsWith("Y", StringComparison.OrdinalIgnoreCase);

        analysis = new DismAnalysis(sizes[0], sizes[1], sizes[2], sizes[3], sizes[4], packages, recommended);
        return true;
    }

    /// <summary>"9.65 GB", "306.03 MB", and the localized "9,65 ГБ" if /English is ignored.</summary>
    internal static bool TryParseSize(string text, out long bytes)
    {
        bytes = 0;
        var trimmed = text.Trim();

        var digits = 0;
        while (digits < trimmed.Length && (char.IsAsciiDigit(trimmed[digits]) || trimmed[digits] is '.' or ','))
            digits++;

        if (digits == 0) return false;

        if (!double.TryParse(trimmed[..digits].Replace(',', '.'), NumberStyles.Float,
                CultureInfo.InvariantCulture, out var number))
            return false;

        var unit = trimmed[digits..].Trim();
        var first = unit.Length > 0 ? char.ToUpperInvariant(unit[0]) : 'B';

        var multiplier = first switch
        {
            'K' or 'К' => 1L << 10,
            'M' or 'М' => 1L << 20,
            'G' or 'Г' => 1L << 30,
            'T' or 'Т' => 1L << 40,
            'B' or 'Б' => 1L,
            _ => 0L,
        };

        if (multiplier == 0) return false;

        bytes = (long)(number * multiplier);
        return true;
    }
}
