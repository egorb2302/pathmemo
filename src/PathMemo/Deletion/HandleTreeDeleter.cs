using System.Buffers;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using PathMemo.Platform.Native;

namespace PathMemo.Deletion;

/// <summary>What a recursive delete managed to do.</summary>
internal sealed class TreeDeleteReport
{
    internal int Files { get; set; }
    internal int Directories { get; set; }
    internal int ReparsePoints { get; set; }
    internal long Bytes { get; set; }

    /// <summary>Per-path failures, capped: a locked directory can produce thousands.</summary>
    internal List<(string Path, string Message)> Errors { get; } = [];

    internal bool Complete => Errors.Count == 0;

    internal void Fail(string path, int error)
    {
        if (Errors.Count < 64) Errors.Add((path, new Win32Exception(error).Message.TrimEnd('.')));
    }
}

/// <summary>
/// Recursive deletion that cannot be walked out of (README section 9.5).
/// </summary>
/// <remarks>
/// <para>
/// The vulnerability this exists to close: between the check and the delete, a
/// subdirectory is replaced by a junction to <c>C:\Windows\System32</c>, and a path-based
/// deleter walks straight into it. Here every child is opened
/// <b>relative to its already-open parent</b> with <c>FILE_OPEN_REPARSE_POINT</c>, so
/// there is no path prefix left for anyone to redirect, and a reparse point is deleted as
/// the link it is rather than entered.
/// </para>
/// <para>
/// Names are collected per directory before anything is removed. Deleting while an
/// enumeration is open is legal on NTFS but the ordering guarantees are murky across
/// filesystems and redirectors, and a directory listing costs 68 bytes per entry - cheap
/// next to being unsure whether an entry was skipped.
/// </para>
/// </remarks>
internal static class HandleTreeDeleter
{
    /// <summary>
    /// Deep enough for anything real (a <c>node_modules</c> chain runs to about 40) and
    /// shallow enough that the recursion cannot exhaust the stack on a crafted tree.
    /// </summary>
    private const int MaxDepth = 256;

    private const uint EnumerateAccess =
        FileApi.Delete | FileApi.FileListDirectory | FileApi.FileTraverse |
        FileApi.FileReadAttributes | FileApi.FileWriteAttributes | FileApi.Synchronize;

    private const uint ChildAccess =
        FileApi.Delete | FileApi.FileListDirectory | FileApi.FileTraverse |
        FileApi.FileReadAttributes | FileApi.FileWriteAttributes | FileApi.Synchronize;

    private const uint OpenOptions =
        NtDll.FileSynchronousIoNonAlert | NtDll.FileOpenForBackupIntent | NtDll.FileOpenReparsePoint;

    /// <summary>
    /// Deletes what the guard approved. The handle is consumed by the caller's
    /// <c>using</c>; this method does not close it.
    /// </summary>
    internal static TreeDeleteReport Delete(GuardedTarget target, CancellationToken ct = default)
    {
        var report = new TreeDeleteReport();
        DeleteEntry(target.Handle, target.DisplayPath, target.IsDirectory, target.IsReparsePoint,
            target.Bytes, target.AllocatedBytes, report, 0, ct);

        return report;
    }

    private static void DeleteEntry(
        SafeFileHandle handle, string path, bool isDirectory, bool isReparsePoint,
        long bytes, long allocated, TreeDeleteReport report, int depth, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // A reparse point is deleted as itself. Entering it would be following a link out
        // of the tree the user asked to remove - the whole bug this design avoids.
        if (isDirectory && !isReparsePoint)
        {
            if (depth >= MaxDepth)
            {
                report.Errors.Add((path, $"nested deeper than {MaxDepth} levels; left alone"));
                return;
            }

            if (!DeleteChildren(handle, path, report, depth, ct)) return;
        }

        if (!FileApi.TryDelete(handle, out var error))
        {
            report.Fail(path, error);
            return;
        }

        if (isReparsePoint) report.ReparsePoints++;
        else if (isDirectory) report.Directories++;
        else
        {
            report.Files++;
            report.Bytes += allocated > 0 ? allocated : bytes;
        }
    }

    /// <summary>Empties a directory. False when something inside could not be removed.</summary>
    private static bool DeleteChildren(
        SafeFileHandle directory, string path, TreeDeleteReport report, int depth, CancellationToken ct)
    {
        // The guard's handle carries DELETE but not the right to list; widening it here
        // costs one call and keeps the object identity the guard established.
        using var listing = NtDll.Reopen(directory, EnumerateAccess, OpenOptions, out var reopenError);
        if (listing is null)
        {
            report.Fail(path, reopenError);
            return false;
        }

        List<DirectoryEntry> entries;
        try
        {
            entries = Entries(listing);
        }
        catch (Win32Exception ex)
        {
            report.Errors.Add((path, ex.Message.TrimEnd('.')));
            return false;
        }

        var complete = true;

        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();

            using var child = NtDll.OpenRelative(listing, entry.Name, ChildAccess, OpenOptions, out var error);
            if (child is null)
            {
                report.Fail(Path.Combine(path, entry.Name), error);
                complete = false;
                continue;
            }

            // What the listing said and what we are now holding can differ: the swap this
            // module exists to survive happens exactly in that window. The handle's own
            // attributes decide how the child is treated, so an entry that became a
            // junction between the two calls is deleted as a link, not walked into.
            var isDirectory = entry.IsDirectory;
            var isReparsePoint = entry.IsReparsePoint;

            if (FileApi.TryReadTagInfo(child, out var tag))
            {
                isDirectory = (tag.FileAttributes & FileApi.FileAttributeDirectory) != 0;
                isReparsePoint = (tag.FileAttributes & FileApi.FileAttributeReparsePoint) != 0;
            }

            var before = report.Errors.Count;
            DeleteEntry(child, Path.Combine(path, entry.Name),
                isDirectory, isReparsePoint, entry.Bytes, entry.Allocated,
                report, depth + 1, ct);

            if (report.Errors.Count != before) complete = false;
        }

        return complete;
    }

    private readonly record struct DirectoryEntry(string Name, uint Attributes, long Bytes, long Allocated)
    {
        internal bool IsDirectory => (Attributes & FileApi.FileAttributeDirectory) != 0;
        internal bool IsReparsePoint => (Attributes & FileApi.FileAttributeReparsePoint) != 0;
    }

    /// <summary>
    /// One directory's children, read through the handle with
    /// <c>FILE_FULL_DIR_INFO</c> - the same information the scanner's enumerator uses, so
    /// the sizes reported here are the ones the tree showed (README section 4.4).
    /// </summary>
    private static unsafe List<DirectoryEntry> Entries(SafeFileHandle directory)
    {
        const int bufferSize = 64 * 1024;
        var buffer = ArrayPool<byte>.Shared.Rent(bufferSize);
        var entries = new List<DirectoryEntry>();

        try
        {
            fixed (byte* start = buffer)
            {
                var infoClass = FileApi.FileFullDirectoryRestartInfoClass;

                while (FileApi.GetFileInformationByHandleEx(directory, infoClass, start, (uint)bufferSize))
                {
                    infoClass = FileApi.FileFullDirectoryInfoClass;

                    var p = start;
                    while (true)
                    {
                        var next = *(uint*)p;
                        var attributes = *(uint*)(p + 56);
                        var nameLength = *(uint*)(p + 60);
                        var name = new string((char*)(p + 68), 0, (int)(nameLength / sizeof(char)));

                        if (name is not ("." or ".."))
                        {
                            entries.Add(new DirectoryEntry(
                                name,
                                attributes,
                                Bytes: *(long*)(p + 40),
                                Allocated: *(long*)(p + 48)));
                        }

                        if (next == 0) break;
                        p += next;
                    }
                }

                var error = Marshal.GetLastWin32Error();
                if (error is not (FileApi.ErrorNoMoreFiles or 0)) throw new Win32Exception(error);
            }

            return entries;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
