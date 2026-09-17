using Microsoft.Win32;
using PathMemo.Snapshots;
using static PathMemo.Audit.Probes.ProbeHelpers;

namespace PathMemo.Audit.Probes;

/// <summary>
/// WSL2 distributions, each an <c>ext4.vhdx</c> that grows and never shrinks. Found
/// through the registry rather than by running <c>wsl.exe</c>, which would boot the
/// virtual machine.
/// </summary>
/// <remarks>
/// How much of a disk is free inside is knowable only from inside the distribution, and
/// starting one to ask is not a read-only act. So the on-disk size is reported and the
/// reclaimable figure is left unknown, with the commands that reclaim it.
/// </remarks>
internal sealed class WslProbe : IAuditProbe
{
    public string Id => "wsl.vhdx";
    public string Title => "WSL2 virtual disks";

    private const string LxssKey = @"Software\Microsoft\Windows\CurrentVersion\Lxss";

    internal static IReadOnlyList<(string Name, string BasePath)> Distributions()
    {
        var result = new List<(string, string)>();

        foreach (var guid in RegistrySubKeys(Registry.CurrentUser, LxssKey))
        {
            var sub = LxssKey + "\\" + guid;
            var name = RegistryString(Registry.CurrentUser, sub, "DistributionName");
            var basePath = RegistryString(Registry.CurrentUser, sub, "BasePath");
            if (name is null || basePath is null) continue;
            result.Add((name, Unprefixed(basePath)));
        }

        return result;
    }

    public IEnumerable<AuditFinding> Run(AuditContext context)
    {
        var distributions = Distributions();
        if (distributions.Count == 0)
        {
            yield return AuditFinding.NotApplicable(Id, Title, "No WSL distributions are registered for this user.");
            yield break;
        }

        // Docker Desktop's own distributions are reported by the Docker probe, once.
        var own = distributions
            .Where(d => !d.Name.StartsWith("docker-desktop", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (own.Count == 0)
        {
            yield return AuditFinding.NotApplicable(Id, Title, "Only Docker Desktop's internal distributions are registered.");
            yield break;
        }

        foreach (var (name, basePath) in own)
        {

            var disk = Path.Combine(basePath, "ext4.vhdx");
            var measured = DirectoryMeasure.File_(disk);
            if (measured.Files == 0) continue;

            var letter = AuditContext.Letter(disk);
            var other = context.RoomiestOtherVolume(letter);

            var remedies = new List<Remedy>
            {
                Command($"wsl --manage {name} --set-sparse true",
                    "Space freed inside the distribution is returned to Windows from then on; needs WSL 2.0+ and a stopped distribution (wsl --shutdown)"),
                Elevated($"Optimize-VHD -Path \"{disk}\" -Mode Full",
                    "Needs the Hyper-V PowerShell module; run wsl --shutdown first"),
                Manual($"Inside the distribution: free space (apt clean, docker system prune, remove build trees), then compact the disk"),
            };

            if (other is not null)
                remedies.Add(Manual(
                    $"Move it to {other.Letter}: wsl --export {name} <file.tar>, wsl --unregister {name}, " +
                    $"wsl --import {name} {other.Letter}\\WSL\\{name} <file.tar>",
                    "Export first and verify the tar; unregister deletes the disk"));

            yield return new AuditFinding
            {
                Id = Id,
                Title = Title,
                Status = FindingStatus.Measured,
                Volume = letter,
                UsedBytes = measured.Allocated,
                ReclaimableBytes = null,
                Risk = Risk.Caution,
                Recoverability = Recoverability.Rebuild,
                Explanation = $"{name}: {Size(measured.Allocated)} on disk" +
                              (measured.Allocated < measured.Logical - (measured.Logical >> 4)
                                  ? $" ({Size(measured.Logical)} logical, already sparse)"
                                  : "") +
                              ". WSL disks grow as files are written and never shrink by themselves; " +
                              "how much is free inside is only visible from inside.",
                Remedies = remedies,
                Paths = [disk],
            };
        }
    }
}

/// <summary>
/// Docker Desktop's disks: the WSL2 backend's <c>ext4.vhdx</c> files and the Hyper-V
/// backend's <c>DockerDesktop.vhdx</c>. Images, build cache and volumes all live inside.
/// </summary>
internal sealed class DockerProbe : IAuditProbe
{
    public string Id => "docker.vhdx";
    public string Title => "Docker Desktop disks";

    public IEnumerable<AuditFinding> Run(AuditContext context)
    {
        var candidates = new List<string>();

        // Registered distributions first: Docker's disks can be relocated to another drive,
        // and the registry knows where. The data disk sits beside the distribution
        // (DockerDesktopWSL\disk\docker_data.vhdx), so the parent directory is searched.
        var directories = new List<string>();
        foreach (var (name, basePath) in WslProbe.Distributions())
        {
            if (!name.StartsWith("docker-desktop", StringComparison.OrdinalIgnoreCase)) continue;
            candidates.Add(Path.Combine(basePath, "ext4.vhdx"));
            if (Path.GetDirectoryName(basePath) is { } parent) directories.Add(parent);
        }

        var local = context.LocalAppData;
        directories.Add(Path.Combine(local, "Docker", "wsl"));
        directories.Add(Path.Combine(local, "Docker", "DockerDesktopWSL"));
        directories.Add(Path.Combine(context.ProgramData, "DockerDesktop", "vm-data"));

        foreach (var directory in directories.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(directory)) continue;
            try
            {
                candidates.AddRange(Directory.EnumerateFiles(directory, "*.vhdx", SearchOption.AllDirectories));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }

        var disks = candidates
            .Select(LongPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(p => (Path: p, Measured: DirectoryMeasure.File_(p)))
            .Where(d => d.Measured.Files > 0)
            .ToList();

        if (disks.Count == 0)
        {
            yield return AuditFinding.NotApplicable(Id, Title, "Docker Desktop is not installed, or has no disk image.");
            yield break;
        }

        var used = disks.Sum(d => d.Measured.Allocated);
        var largest = disks.MaxBy(d => d.Measured.Allocated).Path;
        var letter = AuditContext.Letter(largest);
        var other = context.RoomiestOtherVolume(letter);

        var remedies = new List<Remedy>
        {
            Command("docker system prune -a --volumes",
                "Removes every image, container, build cache entry and volume not in use - they download or rebuild"),
            Command("docker builder prune -a", "Build cache only"),
            Settings("Docker Desktop > Settings > Resources > Advanced > Disk image size / location"),
        };

        if (other is not null && !letter.Equals(other.Letter, StringComparison.OrdinalIgnoreCase))
            remedies.Add(Manual($"Move the disk image to {other.Letter} from that same Settings page ({Size((long)other.FreeBytes)} free there)"));

        yield return new AuditFinding
        {
            Id = Id,
            Title = Title,
            Status = FindingStatus.Measured,
            Volume = letter,
            UsedBytes = used,
            ReclaimableBytes = null,
            Risk = Risk.Caution,
            Recoverability = Recoverability.Redownload,
            Explanation = $"{disks.Count} disk image{(disks.Count == 1 ? "" : "s")}, {Size(used)} on disk. " +
                          "Docker's images, build cache and volumes live inside; 'docker system df' shows how much " +
                          "of it is still referenced. The file shrinks only after pruning and a compaction.",
            Remedies = remedies,
            Paths = disks.Select(d => d.Path).ToList(),
        };
    }
}

/// <summary>
/// Other virtual disks found by the last scan: Hyper-V machines, VirtualBox, mounted
/// images. Not measured live; the point is to list what the scan already knows.
/// </summary>
internal sealed class HyperVDisksProbe : IAuditProbe
{
    public string Id => "hyperv.vhdx";
    public string Title => "Other virtual disks";

    private static readonly string[] Extensions = [".vhdx", ".vhd", ".avhdx", ".vdi", ".vmdk", ".qcow2"];

    public IEnumerable<AuditFinding> Run(AuditContext context)
    {
        if (context.Snapshot is not { } snapshot)
        {
            yield return new AuditFinding
            {
                Id = Id, Title = Title, Status = FindingStatus.NoSnapshot,
                Explanation = "Virtual disks are found in the last scan; run 'pathmemo scan' first.",
            };
            yield break;
        }

        var tree = snapshot.Tree;
        var known = WslProbe.Distributions()
            .Select(d => d.Name.StartsWith("docker-desktop", StringComparison.OrdinalIgnoreCase)
                ? Path.GetDirectoryName(d.BasePath) ?? d.BasePath
                : d.BasePath)
            .ToList();
        known.Add(Path.Combine(context.LocalAppData, "Docker"));
        known.Add(Path.Combine(context.ProgramData, "DockerDesktop"));

        var found = new List<(int Node, long Bytes)>();

        for (var i = 0; i < tree.Count; i++)
        {
            if (tree.IsDirectory(i)) continue;
            if (tree.Allocated[i] < 64L * 1024 * 1024) continue;
            if (!HasExtension(tree.NameUtf8(i))) continue;

            var path = tree.GetPath(i);
            if (known.Any(k => path.StartsWith(k, StringComparison.OrdinalIgnoreCase))) continue;

            found.Add((i, tree.Allocated[i]));
        }

        if (found.Count == 0)
        {
            yield return AuditFinding.NotApplicable(Id, Title, "The last scan found no virtual disks outside WSL and Docker.");
            yield break;
        }

        found.Sort((a, b) => b.Bytes.CompareTo(a.Bytes));
        var total = found.Sum(f => f.Bytes);

        yield return new AuditFinding
        {
            Id = Id,
            Title = Title,
            Status = FindingStatus.Measured,
            Volume = AuditContext.Letter(tree.GetPath(found[0].Node)),
            UsedBytes = total,
            ReclaimableBytes = null,
            Risk = Risk.Caution,
            Recoverability = Recoverability.Irreversible,
            Explanation = $"{found.Count} virtual disk image{(found.Count == 1 ? "" : "s")} totalling {Size(total)} " +
                          $"(as of scan {context.SnapshotId}). A dynamically expanding disk keeps space freed inside " +
                          "it until compacted; an unused machine's disk is simply large.",
            Remedies =
            [
                Elevated("Optimize-VHD -Path <disk.vhdx> -Mode Full", "Hyper-V module; the machine must be off"),
                Manual("Delete checkpoints in Hyper-V Manager to merge the .avhdx chain back into the parent"),
            ],
            Paths = found.Take(20).Select(f => tree.GetPath(f.Node)).ToList(),
        };
    }

    private static bool HasExtension(ReadOnlySpan<byte> name)
    {
        var dot = name.LastIndexOf((byte)'.');
        if (dot < 0) return false;

        Span<char> ext = stackalloc char[16];
        var tail = name[dot..];
        if (tail.Length > ext.Length) return false;

        for (var i = 0; i < tail.Length; i++) ext[i] = char.ToLowerInvariant((char)tail[i]);
        var candidate = ext[..tail.Length];

        foreach (var extension in Extensions)
            if (candidate.SequenceEqual(extension)) return true;

        return false;
    }
}
