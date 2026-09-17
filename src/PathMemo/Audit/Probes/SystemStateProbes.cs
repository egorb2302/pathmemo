using System.Runtime.InteropServices;
using Microsoft.Win32;
using PathMemo.Platform.Native;
using static PathMemo.Audit.Probes.ProbeHelpers;

namespace PathMemo.Audit.Probes;

/// <summary>
/// Reserved storage: the ~7 GB Windows 10 1903+ holds back so updates always have room.
/// The exact figure is not exposed anywhere a process can read; the state is.
/// </summary>
internal sealed class ReservedStorageProbe : IAuditProbe
{
    public string Id => "fastboot.reserved";
    public string Title => "Reserved storage";

    private const string Key = @"SOFTWARE\Microsoft\Windows\CurrentVersion\ReserveManager";

    public IEnumerable<AuditFinding> Run(AuditContext context)
    {
        var shipped = RegistryInt(Registry.LocalMachine, Key, "ShippedWithReserves");
        if (shipped is null)
        {
            yield return AuditFinding.NotApplicable(Id, Title, "This Windows build has no reserved storage.");
            yield break;
        }

        var enabled = shipped == 1;

        if (context.Elevated)
        {
            // DISM is authoritative when it is available: a registry value of 1 with the
            // feature turned off through DISM has been seen in the wild.
            var output = ExternalTool.Run("Dism.exe",
                ["/English", "/Online", "/Get-ReservedStorageState"], context.Cancellation);
            if (output.Succeeded)
                enabled = !output.StdOut.Contains("disabled", StringComparison.OrdinalIgnoreCase);
        }

        if (!enabled)
        {
            yield return AuditFinding.NotApplicable(Id, Title, "Reserved storage is disabled.");
            yield break;
        }

        yield return new AuditFinding
        {
            Id = Id,
            Title = Title,
            Status = FindingStatus.Measured,
            Volume = AuditContext.Letter(context.WindowsDirectory),
            UsedBytes = null,
            ReclaimableBytes = null,
            Risk = Risk.Caution,
            Recoverability = Recoverability.Instant,
            Explanation = "Reserved storage is enabled: Windows sets aside roughly 7 GB so an update can never " +
                          "fail for lack of space. Settings > Storage > System & reserved shows the exact figure. " +
                          "Disabling it hands the space back; the next feature update may then need the room.",
            Remedies =
            [
                Elevated("dism /Online /Set-ReservedStorageState /State:Disabled",
                    "Refused while an update is pending", reboot: false),
                Settings("Settings > System > Storage > System & reserved"),
            ],
        };
    }
}

/// <summary>
/// NTFS metadata: the <c>$MFT</c> and friends. Never reclaimable; reported so the gap
/// between "sum of files" and "used space" has a name (README section 3.5).
/// </summary>
internal sealed class NtfsMetadataProbe : IAuditProbe
{
    public string Id => "ntfs.metadata";
    public string Title => "NTFS metadata";

    public IEnumerable<AuditFinding> Run(AuditContext context)
    {
        foreach (var volume in context.Volumes)
        {
            if (!volume.IsNtfs) continue;

            if (!TryQuery(volume.Root, out var data, out var error))
            {
                yield return new AuditFinding
                {
                    Id = Id, Title = Title, Status = FindingStatus.Unknown, Volume = volume.Letter,
                    Explanation = $"NTFS volume data on {volume.Letter} could not be read.",
                    Note = $"error {error}",
                };
                continue;
            }

            var mft = data.MftValidDataLength;
            var reserved = data.TotalReserved * data.BytesPerCluster;
            var records = data.BytesPerFileRecordSegment == 0 ? 0 : mft / data.BytesPerFileRecordSegment;

            yield return new AuditFinding
            {
                Id = Id,
                Title = Title,
                Status = FindingStatus.Measured,
                Volume = volume.Letter,
                UsedBytes = mft,
                ReclaimableBytes = 0,
                Risk = Risk.Danger,
                Recoverability = Recoverability.Irreversible,
                Explanation = $"$MFT on {volume.Letter} is {Size(mft)} ({records:N0} file records of " +
                              $"{data.BytesPerFileRecordSegment} bytes){(reserved > 0 ? $", plus {Size(reserved)} reserved for metadata growth" : "")}. " +
                              "It is the file system itself and never shrinks; only a smaller number of files shrinks it.",
            };
        }
    }

    /// <summary>
    /// Sends the FSCTL through a handle to the volume's root directory, not to
    /// <c>\\.\C:</c>. A standard user may open the device only without read access, and
    /// such a handle bypasses the file system entirely - NTFS never sees the request and
    /// the answer is ERROR_INVALID_FUNCTION. Any directory handle on the volume reaches
    /// NTFS, and opening one needs no privilege at all.
    /// </summary>
    internal static bool TryQuery(string root, out NtfsVolumeData data, out int error)
    {
        data = default;

        var handle = Kernel32Extra.CreateFile(root, Kernel32Extra.FileReadAttributes,
            Kernel32Extra.FileShareRead | Kernel32Extra.FileShareWrite | Kernel32Extra.FileShareDelete,
            0, Kernel32Extra.OpenExisting, Kernel32Extra.FileFlagBackupSemantics, 0);

        if (handle == -1 || handle == 0)
        {
            error = Marshal.GetLastWin32Error();
            return false;
        }

        try
        {
            // The error is read inside TryQuery, before CloseHandle resets it.
            return NtfsVolumeData.TryQuery(handle, out data, out error);
        }
        finally
        {
            Kernel32Extra.CloseHandle(handle);
        }
    }
}
