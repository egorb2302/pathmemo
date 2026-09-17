using Microsoft.Win32;
using static PathMemo.Audit.Probes.ProbeHelpers;

namespace PathMemo.Audit.Probes;

/// <summary>
/// <c>%WINDIR%\Installer</c>: cached MSI and MSP packages. Windows Installer needs the
/// referenced ones to repair or uninstall; the unreferenced ones are left over from
/// uninstalled products and upgrades.
/// </summary>
/// <remarks>
/// Report only. Deleting a cached package that is still referenced breaks uninstalling
/// that product in a way that is painful to repair, and the registry mapping is not
/// complete for every installer, so this finding is <see cref="Risk.Danger"/> and its
/// remedy is a human with a second opinion (README section 6.1).
/// </remarks>
internal sealed class InstallerOrphansProbe : IAuditProbe
{
    public string Id => "windows.installer-orphans";
    public string Title => "Windows Installer cache";

    private const string UserData = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\UserData";

    public IEnumerable<AuditFinding> Run(AuditContext context)
    {
        var directory = Path.Combine(context.WindowsDirectory, "Installer");
        if (!Directory.Exists(directory))
        {
            yield return AuditFinding.NotApplicable(Id, Title, "There is no Windows Installer cache directory.");
            yield break;
        }

        var packages = TryListPackages(directory, out var listError);
        if (packages is null)
        {
            yield return new AuditFinding
            {
                Id = Id, Title = Title, Status = FindingStatus.NeedsElevation,
                Volume = AuditContext.Letter(directory),
                Explanation = "The installer cache could not be listed.",
                Note = listError,
            };
            yield break;
        }

        var referenced = ReferencedPackages();
        var (orphans, orphanBytes, totalBytes) = Classify(packages, referenced);

        yield return new AuditFinding
        {
            Id = Id,
            Title = Title,
            Status = FindingStatus.Measured,
            Volume = AuditContext.Letter(directory),
            UsedBytes = totalBytes,
            ReclaimableBytes = orphanBytes,
            Risk = Risk.Danger,
            Recoverability = Recoverability.Irreversible,
            Explanation = $"{Size(totalBytes)} of cached MSI/MSP packages; {orphans.Count} of {packages.Count} " +
                          $"({Size(orphanBytes)}) are not referenced by any installed product or patch. " +
                          "Windows Installer needs the referenced ones to uninstall or repair software. " +
                          "Verify with a dedicated tool before removing anything.",
            Remedies =
            [
                Manual("Check the unreferenced packages with a tool that understands the installer database " +
                       "(e.g. PatchCleaner) before deleting; move them, do not delete, the first time",
                       "A wrongly deleted package makes its product impossible to uninstall or update"),
            ],
            Paths = orphans.Take(30).ToList(),
        };
    }

    private static List<string>? TryListPackages(string directory, out string? error)
    {
        try
        {
            error = null;
            return Directory.EnumerateFiles(directory)
                .Where(f => f.EndsWith(".msi", StringComparison.OrdinalIgnoreCase)
                         || f.EndsWith(".msp", StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = ex.Message;
            return null;
        }
    }

    internal static (List<string> Orphans, long OrphanBytes, long TotalBytes) Classify(
        IEnumerable<string> packages, IReadOnlySet<string> referenced)
    {
        var orphans = new List<string>();
        long orphanBytes = 0, total = 0;

        foreach (var package in packages)
        {
            var measured = DirectoryMeasure.File_(package);
            total += measured.Allocated;

            if (referenced.Contains(package)) continue;

            orphans.Add(package);
            orphanBytes += measured.Allocated;
        }

        return (orphans, orphanBytes, total);
    }

    /// <summary>
    /// Every <c>LocalPackage</c> value under the per-SID product and patch tables. Readable
    /// without elevation; the values are full paths into the cache directory.
    /// </summary>
    private static HashSet<string> ReferencedPackages()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var sid in RegistrySubKeys(Registry.LocalMachine, UserData))
        {
            foreach (var product in RegistrySubKeys(Registry.LocalMachine, $@"{UserData}\{sid}\Products"))
                Add(set, $@"{UserData}\{sid}\Products\{product}\InstallProperties");

            foreach (var patch in RegistrySubKeys(Registry.LocalMachine, $@"{UserData}\{sid}\Patches"))
                Add(set, $@"{UserData}\{sid}\Patches\{patch}");
        }

        return set;
    }

    private static void Add(HashSet<string> set, string key)
    {
        var package = RegistryString(Registry.LocalMachine, key, "LocalPackage");
        if (!string.IsNullOrEmpty(package)) set.Add(package);
    }
}
