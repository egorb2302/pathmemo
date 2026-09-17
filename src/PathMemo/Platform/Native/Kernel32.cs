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
