using System.Runtime.InteropServices;

namespace PathMemo.Platform.Native;

/// <summary>
/// kernel32 imports. Source-generated (<see cref="LibraryImportAttribute"/>) rather than
/// <c>DllImport</c> so the NativeAOT path stays open (README section 19.3).
/// </summary>
internal static partial class Kernel32
{
    private const string Dll = "kernel32.dll";

    [LibraryImport(Dll, EntryPoint = "GetDiskFreeSpaceExW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetDiskFreeSpaceEx(
        string lpDirectoryName,
        out ulong lpFreeBytesAvailableToCaller,
        out ulong lpTotalNumberOfBytes,
        out ulong lpTotalNumberOfFreeBytes);

    [LibraryImport(Dll, EntryPoint = "GetDiskFreeSpaceW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetDiskFreeSpace(
        string lpRootPathName,
        out uint lpSectorsPerCluster,
        out uint lpBytesPerSector,
        out uint lpNumberOfFreeClusters,
        out uint lpTotalNumberOfClusters);

    [LibraryImport(Dll, EntryPoint = "GetVolumeInformationW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetVolumeInformation(
        string lpRootPathName,
        Span<char> lpVolumeNameBuffer,
        int nVolumeNameSize,
        out uint lpVolumeSerialNumber,
        out uint lpMaximumComponentLength,
        out uint lpFileSystemFlags,
        Span<char> lpFileSystemNameBuffer,
        int nFileSystemNameSize);

    [LibraryImport(Dll, EntryPoint = "GetVolumeNameForVolumeMountPointW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetVolumeNameForVolumeMountPoint(
        string lpszVolumeMountPoint,
        Span<char> lpszVolumeName,
        int cchBufferLength);
}

internal static partial class Kernel32Extra
{
    private const string Dll = "kernel32.dll";

    internal const uint InvalidFileSize = 0xFFFFFFFF;

    /// <summary>
    /// Physical bytes on the volume: cluster-rounded, and reduced for sparse or
    /// NTFS-compressed files. This is the number that adds up to "used space"
    /// (README section 3.1).
    /// </summary>
    [LibraryImport(Dll, EntryPoint = "GetCompressedFileSizeW", SetLastError = true)]
    internal static unsafe partial uint GetCompressedFileSize(char* lpFileName, out uint lpFileSizeHigh);

    [LibraryImport(Dll, EntryPoint = "CreateFileW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        nint lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        nint hTemplateFile);

    [LibraryImport(Dll, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseHandle(nint hObject);

    [LibraryImport(Dll, EntryPoint = "DeviceIoControl", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool DeviceIoControl(
        nint hDevice,
        uint dwIoControlCode,
        void* lpInBuffer,
        uint nInBufferSize,
        void* lpOutBuffer,
        uint nOutBufferSize,
        out uint lpBytesReturned,
        nint lpOverlapped);

    internal const uint GenericRead = 0x80000000;
    internal const uint FileReadAttributes = 0x00000080;
    internal const uint FileShareRead = 0x00000001;
    internal const uint FileShareWrite = 0x00000002;
    internal const uint FileShareDelete = 0x00000004;
    internal const uint OpenExisting = 3;
    internal const uint FileFlagBackupSemantics = 0x02000000;
    internal const uint FileFlagOpenReparsePoint = 0x00200000;

    internal const uint IoctlStorageQueryProperty = 0x002D1400;

    /// <summary>NTFS_VOLUME_DATA_BUFFER: $MFT size and cluster geometry, no elevation needed.</summary>
    internal const uint FsctlGetNtfsVolumeData = 0x00090064;

    internal const uint GmemMoveable = 0x0002;

    [LibraryImport(Dll, SetLastError = true)]
    internal static partial nint GlobalAlloc(uint uFlags, nuint dwBytes);

    [LibraryImport(Dll, SetLastError = true)]
    internal static partial nint GlobalLock(nint hMem);

    [LibraryImport(Dll, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GlobalUnlock(nint hMem);

    [LibraryImport(Dll, SetLastError = true)]
    internal static partial nint GlobalFree(nint hMem);

    [LibraryImport(Dll)]
    internal static partial uint GetConsoleOutputCP();

    /// <summary>Expands 8.3 segments (<c>MIXPC~1</c>) so two spellings of one directory compare equal.</summary>
    [LibraryImport(Dll, EntryPoint = "GetLongPathNameW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    internal static partial uint GetLongPathName(string lpszShortPath, Span<char> lpszLongPath, uint cchBuffer);
}
