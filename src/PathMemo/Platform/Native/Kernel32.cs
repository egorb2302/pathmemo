using System.Runtime.InteropServices;

namespace PathMemo.Platform.Native;

/// <summary>
/// kernel32 imports. Source-generated (<see cref="LibraryImportAttribute"/>) rather than
/// <c>DllImport</c> so the NativeAOT path stays open (README section 19.4).
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

    /// <summary>Tells the cache this file is read front to back, which is what hashing does.</summary>
    internal const uint FileFlagSequentialScan = 0x08000000;

    /// <summary>
    /// Opens a cloud placeholder without hydrating it. The second barrier around
    /// threat T10: a read then fails instead of downloading the file (README section 8.4).
    /// </summary>
    internal const uint FileFlagOpenNoRecall = 0x00100000;

    // Attributes that mean "the bytes are not on this disk".
    internal const uint FileAttributeOffline = 0x00001000;
    internal const uint FileAttributeRecallOnOpen = 0x00040000;
    internal const uint FileAttributeRecallOnDataAccess = 0x00400000;

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

    internal const int StdInputHandle = -10;
    internal const int StdOutputHandle = -11;

    /// <summary>ENABLE_PROCESSED_OUTPUT: required for anything escape-driven.</summary>
    internal const uint EnableProcessedOutput = 0x0001;

    /// <summary>
    /// ENABLE_VIRTUAL_TERMINAL_PROCESSING: makes conhost interpret ANSI sequences.
    /// Off by default for a console application, which is why the TUI has to ask.
    /// </summary>
    internal const uint EnableVirtualTerminalProcessing = 0x0004;

    [LibraryImport(Dll, SetLastError = true)]
    internal static partial nint GetStdHandle(int nStdHandle);

    [LibraryImport(Dll, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetConsoleMode(nint hConsoleHandle, out uint lpMode);

    [LibraryImport(Dll, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetConsoleMode(nint hConsoleHandle, uint dwMode);

    /// <summary>
    /// ENABLE_QUICK_EDIT_MODE: a click in the window starts a selection and the next
    /// write blocks until it is cleared. Fine for a shell, fatal for a redrawing screen.
    /// </summary>
    internal const uint EnableQuickEditMode = 0x0040;

    /// <summary>ENABLE_EXTENDED_FLAGS: without it the quick-edit bit is ignored.</summary>
    internal const uint EnableExtendedFlags = 0x0080;

    /// <summary>TMPF_TRUETYPE: raster fonts cannot draw box-drawing or block characters.</summary>
    internal const uint TrueTypeFontFamily = 0x04;

    /// <summary>CONSOLE_FONT_INFOEX. <c>cbSize</c> must be set or the call fails.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct ConsoleFontInfoEx
    {
        internal uint Size;
        internal uint FontIndex;
        internal short WidthPixels;
        internal short HeightPixels;
        internal uint FontFamily;
        internal uint FontWeight;
        internal fixed char FaceName[32];
    }

    [LibraryImport(Dll, EntryPoint = "GetCurrentConsoleFontEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool GetCurrentConsoleFontEx(
        nint hConsoleOutput, [MarshalAs(UnmanagedType.Bool)] bool bMaximumWindow, ConsoleFontInfoEx* lpConsoleCurrentFontEx);

    /// <summary>Expands 8.3 segments (<c>MIXPC~1</c>) so two spellings of one directory compare equal.</summary>
    [LibraryImport(Dll, EntryPoint = "GetLongPathNameW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    internal static partial uint GetLongPathName(string lpszShortPath, Span<char> lpszLongPath, uint cchBuffer);

    /// <summary>
    /// Positioned read. A synchronous handle honours the offset in the OVERLAPPED, which
    /// is how a raw volume is read at cluster offsets without a seek call per read.
    /// </summary>
    [LibraryImport(Dll, EntryPoint = "ReadFile", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool ReadFile(
        nint hFile,
        byte* lpBuffer,
        uint nNumberOfBytesToRead,
        out uint lpNumberOfBytesRead,
        NativeOverlapped* lpOverlapped);

    [LibraryImport(Dll, EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool GetFileInformationByHandleEx(
        nint hFile,
        int fileInformationClass,
        void* lpFileInformation,
        uint dwBufferSize);

    /// <summary>FILE_INFO_BY_HANDLE_CLASS values used here.</summary>
    internal const int FileStandardInfo = 1;
    internal const int FileIdInfo = 18;

    /// <summary>Opens a path with the same rules as <see cref="CreateFile(string, uint, uint, nint, uint, uint, nint)"/>, from a span.</summary>
    [LibraryImport(Dll, EntryPoint = "CreateFileW", SetLastError = true)]
    internal static unsafe partial nint CreateFile(
        char* lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        nint lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        nint hTemplateFile);

    /// <summary>
    /// The running image, for reading a resource out of it. A null name means this executable,
    /// which is the only sensible way to reach the icon of a single-file build: there is no
    /// separate file on disk to name (README section 19.1).
    /// </summary>
    [LibraryImport(Dll, EntryPoint = "GetModuleHandleW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint GetModuleHandle(string? lpModuleName);
}

/// <summary>FILE_STANDARD_INFO (GetFileInformationByHandleEx class 1).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct FileStandardInfo
{
    public long AllocationSize;
    public long EndOfFile;
    public uint NumberOfLinks;
    [MarshalAs(UnmanagedType.U1)] public bool DeletePending;
    [MarshalAs(UnmanagedType.U1)] public bool Directory;
}

/// <summary>FILE_ID_INFO (class 18): volume serial plus a 128-bit file id, stable across renames.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct FileIdInfo
{
    public ulong VolumeSerialNumber;
    public ulong FileIdLow;
    public ulong FileIdHigh;
}
