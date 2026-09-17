using System.Runtime.InteropServices;

namespace PathMemo.Platform.Native;

/// <summary>NTFS_VOLUME_DATA_BUFFER, the answer to FSCTL_GET_NTFS_VOLUME_DATA. 96 bytes.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct NtfsVolumeData
{
    public long VolumeSerialNumber;
    public long NumberSectors;
    public long TotalClusters;
    public long FreeClusters;
    public long TotalReserved;
    public uint BytesPerSector;
    public uint BytesPerCluster;
    public uint BytesPerFileRecordSegment;
    public uint ClustersPerFileRecordSegment;
    public long MftValidDataLength;
    public long MftStartLcn;
    public long Mft2StartLcn;
    public long MftZoneStart;
    public long MftZoneEnd;

    /// <summary>Number of file record segments the $MFT currently holds.</summary>
    public readonly long RecordCount =>
        BytesPerFileRecordSegment == 0 ? 0 : MftValidDataLength / BytesPerFileRecordSegment;

    /// <summary>
    /// Sends the FSCTL through any handle that reaches the file system: a directory on
    /// the volume, or the volume itself opened for reading.
    /// </summary>
    internal static unsafe bool TryQuery(nint handle, out NtfsVolumeData data, out int error)
    {
        data = default;
        bool ok;
        fixed (NtfsVolumeData* p = &data)
        {
            ok = Kernel32Extra.DeviceIoControl(handle, Kernel32Extra.FsctlGetNtfsVolumeData,
                null, 0, p, (uint)sizeof(NtfsVolumeData), out _, 0);
        }

        error = ok ? 0 : Marshal.GetLastWin32Error();
        return ok;
    }
}
