using System.Globalization;
using Microsoft.Win32;
using static PathMemo.Audit.Probes.ProbeHelpers;

namespace PathMemo.Audit.Probes;

/// <summary>
/// <c>hiberfil.sys</c>. Its size is readable from the root directory listing without
/// opening it, and the hibernation configuration comes from the registry, so the probe
/// needs neither elevation nor <c>powercfg</c>.
/// </summary>
internal sealed class HibernationProbe : IAuditProbe
{
    public string Id => "hiberfil";
    public string Title => "Hibernation file";

    public IEnumerable<AuditFinding> Run(AuditContext context)
    {
        var root = Path.GetPathRoot(context.WindowsDirectory) ?? @"C:\";
        var path = Path.Combine(root, "hiberfil.sys");
        var measured = DirectoryMeasure.File_(path);

        if (measured.Files == 0)
        {
            yield return AuditFinding.NotApplicable(Id, Title, "Hibernation is off; there is no hiberfil.sys.");
            yield break;
        }

        // HiberFileType: 1 = reduced (Fast Startup only), 2 = full (hibernate as well).
        var type = RegistryInt(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Power", "HiberFileType");
        var reduced = type == 1;

        yield return new AuditFinding
        {
            Id = Id,
            Title = Title,
            Status = FindingStatus.Measured,
            Volume = AuditContext.Letter(root),
            UsedBytes = measured.Allocated,
            ReclaimableBytes = measured.Allocated,
            Risk = Risk.Caution,
            Recoverability = Recoverability.Instant,
            Explanation = reduced
                ? $"hiberfil.sys is {Size(measured.Allocated)}, the reduced size used only by Fast Startup. " +
                  "Turning it off makes boot a few seconds slower and nothing else."
                : $"hiberfil.sys is {Size(measured.Allocated)}. It backs Hibernate and Fast Startup; " +
                  "turning it off removes both. Reducing it keeps Fast Startup only.",
            Remedies = reduced
                ?
                [
                    Elevated("powercfg /hibernate off", "Disables Fast Startup"),
                ]
                :
                [
                    Elevated("powercfg /hibernate /type reduced", "Keeps Fast Startup, removes Hibernate; roughly halves the file"),
                    Elevated("powercfg /hibernate off", "Disables Hibernate and Fast Startup"),
                ],
            Paths = [path],
        };
    }
}

/// <summary>
/// <c>pagefile.sys</c>. Nothing is reclaimable by deleting it, and the size figure is
/// here so the user is not left wondering where 12 GB went. The useful remedy when the
/// system drive is full and another is not: move it.
/// </summary>
internal sealed class PageFileProbe : IAuditProbe
{
    public string Id => "pagefile";
    public string Title => "Page file";

    public IEnumerable<AuditFinding> Run(AuditContext context)
    {
        var configured = ConfiguredPagingFiles();

        foreach (var volume in context.Volumes)
        {
            var path = Path.Combine(volume.Root, "pagefile.sys");
            var measured = DirectoryMeasure.File_(path);
            if (measured.Files == 0) continue;

            var automatic = configured.Any(c => c.StartsWith("?:", StringComparison.Ordinal))
                         || configured.Count == 0;

            var other = context.RoomiestOtherVolume(volume.Letter);

            var remedies = new List<Remedy>
            {
                Settings("System Properties > Advanced > Performance Settings > Advanced > Virtual memory"),
                Elevated("SystemPropertiesAdvanced.exe", "Opens the same dialog"),
            };

            if (other is not null)
            {
                remedies.Insert(0, Manual(
                    $"Move the page file to {other.Letter} ({Size((long)other.FreeBytes)} free): " +
                    $"set {volume.Letter} to \"No paging file\" and {other.Letter} to \"System managed size\"",
                    "Takes effect after a reboot"));
            }

            yield return new AuditFinding
            {
                Id = Id,
                Title = Title,
                Status = FindingStatus.Measured,
                Volume = volume.Letter,
                UsedBytes = measured.Allocated,
                ReclaimableBytes = other is null ? 0 : measured.Allocated,
                Risk = Risk.Caution,
                Recoverability = Recoverability.Instant,
                Explanation = string.Format(CultureInfo.InvariantCulture,
                    "pagefile.sys on {0} is {1}{2}. Windows needs a page file somewhere; {3}",
                    volume.Letter, Size(measured.Allocated),
                    automatic ? " (system managed)" : "",
                    other is null
                        ? "with no other roomy volume, leave it."
                        : $"it does not have to be on {volume.Letter}."),
                Remedies = remedies,
                Paths = [path],
            };
        }
    }

    private static IReadOnlyList<string> ConfiguredPagingFiles()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management");
            return key?.GetValue("PagingFiles") as string[] ?? [];
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return [];
        }
    }
}

/// <summary>
/// <c>swapfile.sys</c>: the page file for Store apps. Small, and goes away with the
/// page file. Reported so the number on the root is accounted for; nothing to reclaim.
/// </summary>
internal sealed class SwapFileProbe : IAuditProbe
{
    public string Id => "swapfile";
    public string Title => "Swap file";

    public IEnumerable<AuditFinding> Run(AuditContext context)
    {
        var root = Path.GetPathRoot(context.WindowsDirectory) ?? @"C:\";
        var path = Path.Combine(root, "swapfile.sys");
        var measured = DirectoryMeasure.File_(path);

        if (measured.Files == 0) yield break;

        yield return new AuditFinding
        {
            Id = Id,
            Title = Title,
            Status = FindingStatus.Measured,
            Volume = AuditContext.Letter(root),
            UsedBytes = measured.Allocated,
            ReclaimableBytes = 0,
            Risk = Risk.Caution,
            Recoverability = Recoverability.Instant,
            Explanation = $"swapfile.sys is {Size(measured.Allocated)}, used by Store apps when suspended. " +
                          "It follows the page file: disabling that on this volume removes it too.",
            Paths = [path],
        };
    }
}
