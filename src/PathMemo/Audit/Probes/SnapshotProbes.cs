using Microsoft.Win32;
using PathMemo.Analysis;
using PathMemo.Snapshots;
using static PathMemo.Audit.Probes.ProbeHelpers;

namespace PathMemo.Audit.Probes;

/// <summary>
/// <c>Windows.old</c>, the previous installation kept for ten days after an upgrade.
/// Mostly unreadable without elevation, so the last scan's figure is preferred and a
/// live measurement is the fallback.
/// </summary>
internal sealed class WindowsOldProbe : IAuditProbe
{
    public string Id => "windows.old";
    public string Title => "Previous Windows installation";

    public IEnumerable<AuditFinding> Run(AuditContext context)
    {
        var root = Path.GetPathRoot(context.WindowsDirectory) ?? @"C:\";
        var directory = Path.Combine(root, "Windows.old");

        if (!Directory.Exists(directory))
        {
            yield return AuditFinding.NotApplicable(Id, Title, "There is no Windows.old directory.");
            yield break;
        }

        long? bytes = null;
        string? note = null;

        if (context.Snapshot is { } snapshot)
        {
            var node = TreeQuery.Find(snapshot.Tree, directory);
            if (node != NodeStore.NoNode)
            {
                bytes = snapshot.Tree.Allocated[node];
                if ((snapshot.Tree.Flags[node] & NodeFlags.Incomplete) != 0)
                    note = "parts were unreadable during the scan; the figure is a lower bound";
            }
        }

        if (bytes is null)
        {
            var measured = DirectoryMeasure.Directory_(directory, context.Cancellation);
            bytes = measured.Allocated;
            note = IncompleteNote(measured, context.Elevated);
        }

        yield return new AuditFinding
        {
            Id = Id,
            Title = Title,
            Status = FindingStatus.Measured,
            Volume = AuditContext.Letter(directory),
            UsedBytes = bytes,
            ReclaimableBytes = bytes,
            Risk = Risk.Caution,
            Recoverability = Recoverability.Irreversible,
            Explanation = $"Windows.old holds the previous installation, {Size(bytes.Value)}. It exists so the " +
                          "upgrade can be rolled back; Windows removes it by itself after ten days.",
            Remedies =
            [
                Settings("Settings > System > Storage > Temporary files > Previous Windows installation(s)"),
                Elevated("cleanmgr /d " + AuditContext.Letter(directory), "Tick 'Previous Windows installation(s)'"),
            ],
            Paths = [directory],
            Note = note,
        };
    }
}

/// <summary>
/// OneDrive files that are fully present on disk but not pinned "Always keep on this
/// device": the ones "Free up space" would turn back into placeholders.
/// </summary>
/// <remarks>
/// Reads the attributes the last scan stored: hydrated means no <c>RECALL_ON_DATA_ACCESS</c>
/// and no <c>OFFLINE</c>; pinned is <c>FILE_ATTRIBUTE_PINNED</c>. Files are never opened,
/// which matters - touching a placeholder downloads it (threat T10).
/// </remarks>
internal sealed class OneDriveProbe : IAuditProbe
{
    public string Id => "onedrive.local";
    public string Title => "OneDrive files kept locally";

    private const uint Pinned = 0x00080000;
    private const uint RecallOnDataAccess = 0x00400000;
    private const uint RecallOnOpen = 0x00040000;
    private const uint Offline = 0x00001000;

    public IEnumerable<AuditFinding> Run(AuditContext context)
    {
        var folders = new List<string>();
        foreach (var account in RegistrySubKeys(Registry.CurrentUser, @"Software\Microsoft\OneDrive\Accounts"))
        {
            var folder = RegistryString(Registry.CurrentUser, $@"Software\Microsoft\OneDrive\Accounts\{account}", "UserFolder");
            if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder)) folders.Add(folder);
        }

        if (folders.Count == 0)
        {
            yield return AuditFinding.NotApplicable(Id, Title, "OneDrive is not set up for this user.");
            yield break;
        }

        if (context.Snapshot is not { } snapshot)
        {
            yield return new AuditFinding
            {
                Id = Id, Title = Title, Status = FindingStatus.NoSnapshot,
                Explanation = "OneDrive placeholders are classified from the last scan; run 'pathmemo scan' first.",
                Paths = folders,
            };
            yield break;
        }

        var tree = snapshot.Tree;
        long hydrated = 0, pinned = 0, cloudOnly = 0;
        var files = 0;

        foreach (var folder in folders)
        {
            var root = TreeQuery.Find(tree, folder);
            if (root == NodeStore.NoNode) continue;

            var stack = new Stack<int>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var node = stack.Pop();
                var children = tree.Children(node);
                for (var i = children.Start.Value; i < children.End.Value; i++)
                {
                    if (tree.IsDirectory(i)) { stack.Push(i); continue; }

                    var attributes = tree.Attributes[i];
                    if ((attributes & (RecallOnDataAccess | RecallOnOpen | Offline)) != 0) { cloudOnly++; continue; }
                    if ((attributes & Pinned) != 0) { pinned += tree.Allocated[i]; continue; }

                    hydrated += tree.Allocated[i];
                    files++;
                }
            }
        }

        if (hydrated < 64L * 1024 * 1024)
        {
            yield return AuditFinding.NotApplicable(Id, Title,
                "Almost everything in OneDrive is already a placeholder or pinned.");
            yield break;
        }

        yield return new AuditFinding
        {
            Id = Id,
            Title = Title,
            Status = FindingStatus.Measured,
            Volume = AuditContext.Letter(folders[0]),
            UsedBytes = hydrated + pinned,
            ReclaimableBytes = hydrated,
            Risk = Risk.Caution,
            Recoverability = Recoverability.Redownload,
            Explanation = $"{Size(hydrated)} in {Files(files)} are synced to the cloud and also fully on disk " +
                          $"without being pinned; {Size(pinned)} more is pinned 'Always keep on this device'. " +
                          "'Free up space' turns unpinned files into placeholders that download on open.",
            Remedies =
            [
                Manual("Right-click a folder in Explorer > Free up space"),
                Command($"attrib +U -P /S /D \"{folders[0]}\\*\"", "Marks everything unpinned; OneDrive dehydrates it in the background"),
            ],
            Paths = folders,
        };
    }
}
