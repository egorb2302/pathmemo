using System.Globalization;
using PathMemo.Platform.Native;
using static PathMemo.Audit.Probes.ProbeHelpers;

namespace PathMemo.Audit.Probes;

/// <summary>
/// The Recycle Bin, per volume, through <c>SHQueryRecycleBin</c>. Nothing in the bin has
/// freed any space yet - the reason quarantine, not the bin, is this tool's default
/// (README section 9.1).
/// </summary>
internal sealed class RecycleBinProbe : IAuditProbe
{
    public string Id => "recyclebin";
    public string Title => "Recycle Bin";

    public IEnumerable<AuditFinding> Run(AuditContext context)
    {
        foreach (var volume in context.Volumes)
        {
            var info = new Shell32.QueryRecycleBinInfo { Size = Shell32.QueryRecycleBinInfo.NativeSize };
            var hr = Shell32.SHQueryRecycleBin(volume.Root, ref info);

            if (hr != 0)
            {
                yield return new AuditFinding
                {
                    Id = Id, Title = Title, Status = FindingStatus.Unknown, Volume = volume.Letter,
                    Explanation = $"The Recycle Bin on {volume.Letter} could not be queried.",
                    Note = $"SHQueryRecycleBin returned 0x{hr:X8}",
                };
                continue;
            }

            if (info.Items == 0) continue;

            var letter = volume.Letter;
            yield return new AuditFinding
            {
                Id = Id,
                Title = Title,
                Status = FindingStatus.Measured,
                Volume = letter,
                UsedBytes = info.Bytes,
                ReclaimableBytes = info.Bytes,
                Risk = Risk.Safe,
                Recoverability = Recoverability.Irreversible,
                Explanation = string.Format(CultureInfo.InvariantCulture,
                    "{0:N0} item{1} waiting in the Recycle Bin on {2}. They still occupy the space " +
                    "until the bin is emptied.",
                    info.Items, info.Items == 1 ? "" : "s", letter),
                Remedies =
                [
                    Command($"powershell -NoProfile -Command Clear-RecycleBin -DriveLetter {letter[0]} -Force"),
                    Manual("Right-click the Recycle Bin on the desktop and choose Empty Recycle Bin"),
                ],
                Paths = [Path.Combine(volume.Root, "$Recycle.Bin")],
            };
        }
    }
}
