using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PathMemo.Platform.Native;

/// <summary>
/// Handle-based file operations. Everything deletion does goes through these
/// (README sections 9.3, 9.5).
/// </summary>
/// <remarks>
/// The distinction from <see cref="Kernel32Extra"/> is deliberate: those calls read, these
/// ones change the disk. A path is resolved once, into a handle, and every later step -
/// verification, rename, delete - names that handle rather than the string it came from.
/// A string can mean something different a millisecond later; a handle cannot.
/// </remarks>
internal static partial class FileApi
{
    private const string Dll = "kernel32.dll";

    // Access rights. DELETE is what both deleting and renaming actually need: a rename
    // is "remove this name here, add it there", and NTFS checks it as such.
    internal const uint Delete = 0x00010000;
    internal const uint Synchronize = 0x00100000;
    internal const uint FileListDirectory = 0x00000001;
    internal const uint FileTraverse = 0x00000020;
    internal const uint FileReadAttributes = 0x00000080;
    internal const uint FileWriteAttributes = 0x00000100;
    internal const uint FileAddFile = 0x00000002;
    internal const uint FileAddSubdirectory = 0x00000004;

    internal const uint ShareAll = 0x00000007;   // READ | WRITE | DELETE

    internal const uint OpenExisting = 3;
    internal const uint FileFlagBackupSemantics = 0x02000000;
    internal const uint FileFlagOpenReparsePoint = 0x00200000;

    internal const uint FileAttributeReadonly = 0x00000001;
    internal const uint FileAttributeDirectory = 0x00000010;
    internal const uint FileAttributeNormal = 0x00000080;
    internal const uint FileAttributeReparsePoint = 0x00000400;
    internal const uint FileAttributeHidden = 0x00000002;
    internal const uint FileAttributeSystem = 0x00000004;

    /// <summary>GetFinalPathNameByHandleW: volume GUID form, which no alias survives.</summary>
    internal const uint VolumeNameGuid = 0x00000001;
    internal const uint VolumeNameDos = 0x00000000;
    internal const uint FileNameNormalized = 0x00000000;

    // FILE_INFO_BY_HANDLE_CLASS members used here.
    internal const int FileBasicInfoClass = 0;
    internal const int FileRenameInfoClass = 3;
    internal const int FileDispositionInfoClass = 4;
    internal const int FileAttributeTagInfoClass = 9;
    internal const int FileFullDirectoryInfoClass = 14;
    internal const int FileFullDirectoryRestartInfoClass = 15;
    internal const int FileDispositionInfoExClass = 21;

    // FILE_DISPOSITION_INFO_EX flags (Win10 1709+ for POSIX, 1809+ for the readonly bit;
    // README section 1 puts the floor at 1809).
    internal const uint DispositionDelete = 0x00000001;
    internal const uint DispositionPosixSemantics = 0x00000002;
    internal const uint DispositionIgnoreReadonly = 0x00000010;

    internal const int ErrorFileNotFound = 2;
    internal const int ErrorPathNotFound = 3;
    internal const int ErrorAccessDenied = 5;
    internal const int ErrorNoMoreFiles = 18;
    internal const int ErrorNotSameDevice = 17;
    internal const int ErrorSharingViolation = 32;
    internal const int ErrorInvalidParameter = 87;
    internal const int ErrorDirNotEmpty = 145;
    internal const int ErrorAlreadyExists = 183;

    [LibraryImport(Dll, EntryPoint = "CreateFileW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    internal static partial SafeFileHandle CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        nint lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        nint hTemplateFile);

    /// <summary>
    /// The canonical name of whatever the handle refers to. With
    /// <see cref="VolumeNameGuid"/> the answer carries no drive letter, no 8.3 segment
    /// and no junction, which is the whole point (README section 9.3, step 2).
    /// </summary>
    [LibraryImport(Dll, EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    internal static partial uint GetFinalPathNameByHandle(
        SafeFileHandle hFile, Span<char> lpszFilePath, uint cchFilePath, uint dwFlags);

    [LibraryImport(Dll, EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool GetFileInformationByHandleEx(
        SafeFileHandle hFile, int fileInformationClass, void* lpFileInformation, uint dwBufferSize);

    [LibraryImport(Dll, EntryPoint = "SetFileInformationByHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool SetFileInformationByHandle(
        SafeFileHandle hFile, int fileInformationClass, void* lpFileInformation, uint dwBufferSize);

    [LibraryImport(Dll, EntryPoint = "GetVolumePathNameW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetVolumePathName(string lpszFileName, Span<char> lpszVolumePathName, uint cchBufferLength);

    [LibraryImport(Dll, EntryPoint = "SetFileAttributesW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetFileAttributes(string lpFileName, uint dwFileAttributes);

    /// <summary>
    /// Opens a path, or returns null with the Win32 error. No exception: "cannot open"
    /// is an ordinary answer here, and the guard turns it into a refusal
    /// (README section 9.3, step 1).
    /// </summary>
    internal static SafeFileHandle? TryOpen(string path, uint access, out int error)
    {
        var handle = CreateFile(path, access, ShareAll, 0, OpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint, 0);

        if (!handle.IsInvalid)
        {
            error = 0;
            return handle;
        }

        error = Marshal.GetLastWin32Error();
        handle.Dispose();
        return null;
    }

    /// <summary>The canonical volume-GUID path of an open handle, or null.</summary>
    internal static string? FinalPath(SafeFileHandle handle, uint flags = VolumeNameGuid)
    {
        Span<char> buffer = stackalloc char[600];
        var length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Length, flags | FileNameNormalized);

        if (length == 0) return null;

        if (length >= buffer.Length)
        {
            var heap = new char[length + 1];
            length = GetFinalPathNameByHandle(handle, heap, (uint)heap.Length, flags | FileNameNormalized);
            return length == 0 ? null : new string(heap, 0, (int)length);
        }

        return new string(buffer[..(int)length]);
    }

    internal static unsafe bool TryReadBasicInfo(SafeFileHandle handle, out FileBasicInfo info)
    {
        fixed (FileBasicInfo* p = &info)
            return GetFileInformationByHandleEx(handle, FileBasicInfoClass, p, (uint)sizeof(FileBasicInfo));
    }

    internal static unsafe bool TryReadStandardInfo(SafeFileHandle handle, out FileStandardInfo info)
    {
        fixed (FileStandardInfo* p = &info)
            return GetFileInformationByHandleEx(handle, Kernel32Extra.FileStandardInfo, p, (uint)sizeof(FileStandardInfo));
    }

    internal static unsafe bool TryReadTagInfo(SafeFileHandle handle, out FileAttributeTagInfo info)
    {
        fixed (FileAttributeTagInfo* p = &info)
            return GetFileInformationByHandleEx(handle, FileAttributeTagInfoClass, p, (uint)sizeof(FileAttributeTagInfo));
    }

    /// <summary>
    /// Marks the open file for deletion.
    /// </summary>
    /// <remarks>
    /// <c>FILE_DISPOSITION_POSIX_SEMANTICS</c> unlinks the name immediately even when
    /// another process holds the file open - the data goes when the last handle closes.
    /// Caches inside a running application are exactly that case (README section 9.5).
    /// Filesystems that do not implement the Ex class (FAT32, some network redirectors)
    /// answer <c>ERROR_INVALID_PARAMETER</c>, and the plain disposition is used instead.
    /// </remarks>
    internal static unsafe bool TryDelete(SafeFileHandle handle, out int error)
    {
        var flags = DispositionDelete | DispositionPosixSemantics | DispositionIgnoreReadonly;

        if (SetFileInformationByHandle(handle, FileDispositionInfoExClass, &flags, sizeof(uint)))
        {
            error = 0;
            return true;
        }

        error = Marshal.GetLastWin32Error();
        if (error is not (ErrorInvalidParameter or ErrorAccessDenied)) return false;

        // Old path: clear the read-only bit by hand, then the boolean disposition.
        if (error == ErrorAccessDenied) TryClearReadOnly(handle);

        byte delete = 1;
        if (SetFileInformationByHandle(handle, FileDispositionInfoClass, &delete, sizeof(byte)))
        {
            error = 0;
            return true;
        }

        error = Marshal.GetLastWin32Error();
        return false;
    }

    private static unsafe void TryClearReadOnly(SafeFileHandle handle)
    {
        if (!TryReadBasicInfo(handle, out var info)) return;
        if ((info.FileAttributes & FileAttributeReadonly) == 0) return;

        info.FileAttributes &= ~FileAttributeReadonly;
        if (info.FileAttributes == 0) info.FileAttributes = FileAttributeNormal;

        // Times of 0 mean "leave them alone", which is what we want: clearing a bit is
        // not a reason to rewrite the timestamps of something we may yet refuse to delete.
        info.CreationTime = 0;
        info.LastAccessTime = 0;
        info.LastWriteTime = 0;
        info.ChangeTime = 0;

        SetFileInformationByHandle(handle, FileBasicInfoClass, &info, (uint)sizeof(FileBasicInfo));
    }

    /// <summary>
    /// Renames the open file into <paramref name="destinationDirectory"/> under
    /// <paramref name="newName"/>, naming the destination by handle.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the move behind quarantine (README section 9.4). Both ends are handles, so
    /// neither the source path nor the destination path is resolved again between the
    /// guard's checks and the operation - the window README section 9.5 is about does not
    /// exist here. A rename cannot cross volumes, so a quarantine on another volume comes
    /// back as <c>ERROR_NOT_SAME_DEVICE</c> rather than silently turning into a copy.
    /// </para>
    /// <para>
    /// Through <c>NtSetInformationFile</c> rather than <c>SetFileInformationByHandle</c>:
    /// the Win32 wrapper rejects a non-null <c>RootDirectory</c> outright with
    /// <c>ERROR_INVALID_PARAMETER</c> (measured, on every buffer shape), so a
    /// destination-by-handle rename is only expressible at the NT layer. The structure is
    /// the same one either way.
    /// </para>
    /// </remarks>
    internal static unsafe bool TryRename(
        SafeFileHandle handle, SafeFileHandle destinationDirectory, string newName, out int error)
    {
        // FILE_RENAME_INFORMATION on x64: ReplaceIfExists (1, padded to 8),
        // RootDirectory (8), FileNameLength (4), then the name at offset 20.
        // FileNameLength counts bytes, not characters, and the name is not terminated.
        const int nameOffset = 20;
        var nameBytes = newName.Length * sizeof(char);
        var buffer = new byte[nameOffset + nameBytes];

        var sourceAdded = false;
        var destinationAdded = false;
        handle.DangerousAddRef(ref sourceAdded);
        destinationDirectory.DangerousAddRef(ref destinationAdded);

        try
        {
            fixed (byte* p = buffer)
            fixed (char* name = newName)
            {
                *(byte*)p = 0;                                          // do not replace
                *(nint*)(p + 8) = destinationDirectory.DangerousGetHandle();
                *(uint*)(p + 16) = (uint)nameBytes;
                Buffer.MemoryCopy(name, p + nameOffset, nameBytes, nameBytes);

                var iosb = default(IoStatusBlock);
                var status = NtDll.NtSetInformationFile(handle.DangerousGetHandle(), &iosb, p,
                    (uint)buffer.Length, NtDll.FileRenameInformation);

                if (status == NtDll.StatusSuccess)
                {
                    error = 0;
                    return true;
                }

                error = NtDll.RtlNtStatusToDosError(status);
                return false;
            }
        }
        finally
        {
            if (sourceAdded) handle.DangerousRelease();
            if (destinationAdded) destinationDirectory.DangerousRelease();
        }
    }
}

/// <summary>FILE_BASIC_INFO (class 0). A time of 0 means "do not change" on write.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct FileBasicInfo
{
    public long CreationTime;
    public long LastAccessTime;
    public long LastWriteTime;
    public long ChangeTime;
    public uint FileAttributes;

    public readonly bool IsDirectory => (FileAttributes & FileApi.FileAttributeDirectory) != 0;
    public readonly bool IsReparsePoint => (FileAttributes & FileApi.FileAttributeReparsePoint) != 0;
}

/// <summary>FILE_ATTRIBUTE_TAG_INFO (class 9): attributes plus the reparse tag.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct FileAttributeTagInfo
{
    public uint FileAttributes;
    public uint ReparseTag;
}
