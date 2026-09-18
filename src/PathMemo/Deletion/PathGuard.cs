using System.ComponentModel;
using Microsoft.Win32.SafeHandles;
using PathMemo.Platform.Native;

namespace PathMemo.Deletion;

/// <summary>
/// An open handle to something the guard has approved, plus what it turned out to be.
/// </summary>
/// <remarks>
/// The handle is the point. Everything after the check - verification, rename, delete -
/// works through it, so no part of the path can be swapped underneath us between the
/// decision and the act (README sections 9.3, 9.5).
/// </remarks>
internal sealed class GuardedTarget : IDisposable
{
    internal required SafeFileHandle Handle { get; init; }
    internal required string RequestedPath { get; init; }
    internal required string CanonicalPath { get; init; }
    internal required string DisplayPath { get; init; }
    internal required string VolumeRoot { get; init; }
    internal required bool IsDirectory { get; init; }
    internal required bool IsReparsePoint { get; init; }
    internal long Bytes { get; init; }
    internal long AllocatedBytes { get; init; }
    internal DateTime LastWriteUtc { get; init; }

    public void Dispose() => Handle.Dispose();
}

/// <summary>An approved target, or the sentence explaining the refusal. Never both.</summary>
internal sealed record GuardOutcome(GuardedTarget? Target, string? Refusal)
{
    internal bool Allowed => Target is not null;

    internal static GuardOutcome No(string reason) => new(null, reason);
}

/// <summary>
/// The gate every deletion passes through (README section 9.3).
/// </summary>
/// <remarks>
/// String comparison of paths was rejected as unsafe: case, trailing slashes, 8.3 names,
/// <c>\\?\</c> and <c>\\.\</c> prefixes, UNC to the local machine, traversal, the
/// compatibility junctions and mount points give at least twelve spellings of
/// <c>C:\Windows</c>. So the path is opened first and judged afterwards, by the name the
/// kernel gives back for the handle we are holding.
/// </remarks>
internal sealed class PathGuard(ProtectedSet protection)
{
    /// <summary>
    /// Enough to ask what something is, and nothing else. README section 9.3 says to open
    /// with zero access; <c>FILE_READ_ATTRIBUTES</c> is the smallest right that actually
    /// allows the metadata query in step 5, and it still grants no read of contents.
    /// </summary>
    internal const uint QueryAccess = FileApi.FileReadAttributes | FileApi.Synchronize;

    /// <summary>What deleting or renaming by handle needs.</summary>
    internal const uint DeleteAccess =
        FileApi.Delete | FileApi.FileReadAttributes | FileApi.FileWriteAttributes | FileApi.Synchronize;

    private readonly ProtectedSet _protection = protection;

    internal ProtectedSet Protection => _protection;

    internal GuardOutcome Open(string requestedPath, uint access = QueryAccess,
        GuardOperation operation = GuardOperation.Delete)
    {
        string full;
        try
        {
            full = Path.GetFullPath(requestedPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return GuardOutcome.No($"not a usable path: {ex.Message}");
        }

        // Step 1: open it, or refuse. We never delete what we could not open - an
        // unopenable path is one we cannot identify, and identity is the whole check.
        var handle = FileApi.TryOpen(full, access, out var error);
        if (handle is null)
        {
            return GuardOutcome.No(error switch
            {
                FileApi.ErrorFileNotFound or FileApi.ErrorPathNotFound => "does not exist",
                FileApi.ErrorAccessDenied => "access denied - run as administrator, or the file is in use",
                FileApi.ErrorSharingViolation => "held open by another process without sharing delete",
                _ => $"cannot be opened: {new Win32Exception(error).Message.TrimEnd('.')}",
            });
        }

        try
        {
            // Step 2: the one name that survives every alias.
            var canonical = FileApi.FinalPath(handle);
            if (canonical is null)
            {
                var dos = FileApi.FinalPath(handle, FileApi.VolumeNameDos);
                handle.Dispose();

                return GuardOutcome.No(dos is not null && dos.Contains(@"\UNC\", StringComparison.OrdinalIgnoreCase)
                    ? "a network location - pathmemo deletes on local volumes only"
                    : "could not be resolved to a volume");
            }

            if (!FileApi.TryReadBasicInfo(handle, out var basic))
            {
                var failure = Marshal32();
                handle.Dispose();
                return GuardOutcome.No($"its attributes could not be read: {failure}");
            }

            var display = Canonical.Display(handle, canonical);
            var volumeRoot = VolumeRootOf(full) ?? VolumeRootOf(display) ?? "";

            // Step 4: the path asked for and the object opened must live on the same
            // volume. A junction that jumps volumes is caught here, before any rule
            // about which directory is sacred gets a chance to be right for the wrong disk.
            if (!SameVolume(volumeRoot, canonical))
            {
                handle.Dispose();
                return GuardOutcome.No($"resolves onto another volume than {volumeRoot}");
            }

            if (basic.IsDirectory && IsVolumeMountPoint(display))
            {
                handle.Dispose();
                return GuardOutcome.No("a volume mount point - it is another disk, not a directory");
            }

            // Step 3: the protected set, over canonical names only.
            var verdict = _protection.Check(canonical, display, operation);
            if (verdict.IsProtected)
            {
                handle.Dispose();
                return GuardOutcome.No($"protected: {verdict.Reason}");
            }

            // A link is judged by where it points as well as by where it is. Deleting the
            // junction C:\Users\All Users removes nothing but a name - and that name is
            // what a decade of installers resolve ProgramData through
            // (README section 9.3, the compatibility junctions).
            if (basic.IsReparsePoint && TargetOf(full) is { } target)
            {
                var beyond = _protection.Check(target.Canonical, target.Display, operation);
                if (beyond.IsProtected)
                {
                    handle.Dispose();
                    return GuardOutcome.No($"a link to {target.Display} - protected: {beyond.Reason}");
                }
            }

            FileApi.TryReadStandardInfo(handle, out var standard);

            return new GuardOutcome(new GuardedTarget
            {
                Handle = handle,
                RequestedPath = requestedPath,
                CanonicalPath = canonical,
                DisplayPath = display,
                VolumeRoot = volumeRoot,
                IsDirectory = basic.IsDirectory,
                IsReparsePoint = basic.IsReparsePoint,
                Bytes = standard.EndOfFile,
                AllocatedBytes = standard.AllocationSize,
                LastWriteUtc = FromFileTime(basic.LastWriteTime),
            }, null);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Step 5: the object must still be what the scan said it was.
    /// </summary>
    /// <remarks>
    /// A file that changed between the scan and the deletion is not the file the user
    /// looked at when they decided. Read through the handle we already hold, so this
    /// compares the snapshot against the object we are about to delete rather than against
    /// whatever currently answers to that name.
    /// </remarks>
    internal static string? Verify(GuardedTarget target, long expectedBytes, DateTime? expectedWriteUtc)
    {
        if (!target.IsDirectory && expectedBytes >= 0 && target.Bytes != expectedBytes)
            return $"size changed since the scan ({Cli.Output.SizeFormat.Bytes(expectedBytes)} -> "
                 + $"{Cli.Output.SizeFormat.Bytes(target.Bytes)}); rescan first";

        if (expectedWriteUtc is { } expected && target.LastWriteUtc != default)
        {
            // Snapshots keep whole seconds (README section 5.3), so compare at that
            // resolution rather than by ticks.
            var difference = (target.LastWriteUtc - expected).Duration();
            if (difference > TimeSpan.FromSeconds(2))
                return $"modified since the scan ({expected:u} -> {target.LastWriteUtc:u}); rescan first";
        }

        return null;
    }

    /// <summary>
    /// Where a reparse point leads, by opening it the ordinary way - following the link -
    /// rather than parsing the reparse data ourselves. Null when it leads nowhere usable,
    /// which is not an error: a dangling junction is just a name.
    /// </summary>
    private static (string Canonical, string Display)? TargetOf(string path)
    {
        var handle = FileApi.CreateFile(path, FileApi.FileReadAttributes | FileApi.Synchronize,
            FileApi.ShareAll, 0, FileApi.OpenExisting, FileApi.FileFlagBackupSemantics, 0);

        try
        {
            if (handle.IsInvalid) return null;

            var canonical = FileApi.FinalPath(handle);
            return canonical is null ? null : (canonical, Canonical.Display(handle, canonical));
        }
        finally
        {
            handle.Dispose();
        }
    }

    private static string Marshal32() =>
        new Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error()).Message.TrimEnd('.');

    internal static DateTime FromFileTime(long fileTime) =>
        fileTime <= 0 ? default : DateTime.FromFileTimeUtc(fileTime);

    /// <summary>The mount root of a path: "C:\", or "D:\mounted\here\" for a mount point.</summary>
    internal static string? VolumeRootOf(string path)
    {
        Span<char> buffer = stackalloc char[520];
        return FileApi.GetVolumePathName(path, buffer, (uint)buffer.Length)
            ? new string(buffer[..buffer.IndexOf('\0')])
            : null;
    }

    private static bool SameVolume(string volumeRoot, string canonical)
    {
        if (volumeRoot.Length == 0) return true;

        var expected = VolumeGuidOf(volumeRoot);
        if (expected is null) return true;    // no GUID for it: nothing to contradict

        var actual = Canonical.VolumePrefix(canonical);
        return actual.Length == 0
            || Canonical.TrimSlash(actual).Equals(Canonical.TrimSlash(expected), StringComparison.OrdinalIgnoreCase);
    }

    private static string? VolumeGuidOf(string mountPoint)
    {
        Span<char> buffer = stackalloc char[64];
        if (!Kernel32.GetVolumeNameForVolumeMountPoint(mountPoint, buffer, buffer.Length)) return null;

        var end = buffer.IndexOf('\0');
        return new string(buffer[..(end < 0 ? buffer.Length : end)]);
    }

    /// <summary>
    /// True when the directory is where another volume is mounted. Deleting one is
    /// deleting a whole disk through a hole in the tree (README section 9.3).
    /// </summary>
    private static bool IsVolumeMountPoint(string directory)
    {
        var withSlash = directory.EndsWith('\\') ? directory : directory + '\\';

        // A drive root answers to this too, and is not what we mean: it is handled by the
        // depth rule in ProtectedSet, with a message that fits.
        if (withSlash.Length <= 3) return false;

        return VolumeGuidOf(withSlash) is not null;
    }
}
