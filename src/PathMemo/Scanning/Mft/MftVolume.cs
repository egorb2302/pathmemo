using System.ComponentModel;
using System.Runtime.InteropServices;
using PathMemo.Platform.Native;

namespace PathMemo.Scanning.Mft;

/// <summary>
/// A raw NTFS volume opened for reading its $MFT (README section 4.3).
/// </summary>
/// <remarks>
/// <para>
/// Opening <c>\\.\C:</c> with <c>GENERIC_READ</c> is the one step that needs an
/// elevated token. From there the $MFT is located from the volume data buffer and its
/// own record 0 - the $MFT describes where the $MFT lives - and read as a stream of
/// file record segments, cluster-aligned as a volume handle demands.
/// </para>
/// <para>
/// Nothing here writes, and nothing opens a file: the whole scan is one handle and a
/// few hundred sequential reads of a 1-2 GB file. Any surprise (odd record size,
/// locked BitLocker volume, unreadable run) throws <see cref="MftUnavailableException"/>
/// and the caller falls back to the directory walk.
/// </para>
/// </remarks>
internal sealed unsafe class MftVolume : IDisposable
{
    private readonly nint _handle;
    private readonly List<(long Lcn, long Clusters)> _runs = [];

    internal NtfsVolumeData Data { get; }
    internal int BytesPerRecord => (int)Data.BytesPerFileRecordSegment;
    internal int BytesPerSector => (int)Data.BytesPerSector;
    internal long ClusterBytes => Data.BytesPerCluster;

    /// <summary>Records to read: what the file system says is valid data in the $MFT.</summary>
    internal long RecordCount { get; }

    private MftVolume(nint handle, NtfsVolumeData data)
    {
        _handle = handle;
        Data = data;
        RecordCount = data.RecordCount;
    }

    internal static MftVolume Open(string letter)
    {
        var path = @"\\.\" + letter.TrimEnd('\\');

        var handle = Kernel32Extra.CreateFile(path, Kernel32Extra.GenericRead,
            Kernel32Extra.FileShareRead | Kernel32Extra.FileShareWrite,
            0, Kernel32Extra.OpenExisting, 0, 0);

        if (handle == -1 || handle == 0)
        {
            var error = Marshal.GetLastWin32Error();
            throw new MftUnavailableException(
                $"cannot open {path} for reading: {new Win32Exception(error).Message} (error {error})", error);
        }

        try
        {
            if (!NtfsVolumeData.TryQuery(handle, out var data, out var queryError))
                throw new MftUnavailableException(
                    $"{letter} did not answer FSCTL_GET_NTFS_VOLUME_DATA (error {queryError})", queryError);

            if (data.BytesPerFileRecordSegment is < 512 or > 65536 || data.BytesPerSector < 256 ||
                data.BytesPerCluster < data.BytesPerSector || data.MftValidDataLength <= 0)
                throw new MftUnavailableException($"{letter} reports an unexpected NTFS geometry");

            var volume = new MftVolume(handle, data);
            volume.LocateMft();
            return volume;
        }
        catch
        {
            Kernel32Extra.CloseHandle(handle);
            throw;
        }
    }

    /// <summary>
    /// Reads record 0 straight from MftStartLcn and decodes its $DATA runs. A very
    /// fragmented $MFT keeps later runs in extension records named by its
    /// $ATTRIBUTE_LIST; those are followed as long as they fall in a run already known.
    /// </summary>
    private void LocateMft()
    {
        var record = new byte[BytesPerRecord];
        ReadRaw(Data.MftStartLcn * ClusterBytes, record);

        if (!MftParser.ApplyFixup(record, BytesPerSector))
            throw new MftUnavailableException("$MFT record 0 is not a valid FILE record");

        var data = MftParser.FindAttribute(record, MftParser.AttrData);
        if (data.IsEmpty || !MftParser.TryReadRuns(data, _runs))
            throw new MftUnavailableException("$MFT record 0 has no readable $DATA runs");

        var list = MftParser.FindAttribute(record, MftParser.AttrAttributeList);
        if (!list.IsEmpty)
        {
            foreach (var (type, startVcn, extension) in MftParser.ReadAttributeList(list))
            {
                if (type != MftParser.AttrData || startVcn == 0) continue;

                var piece = new byte[BytesPerRecord];
                if (!TryReadRecord(extension, piece) || !MftParser.ApplyFixup(piece, BytesPerSector))
                    throw new MftUnavailableException($"$MFT extension record {extension} is unreadable");

                var more = MftParser.FindAttribute(piece, MftParser.AttrData, startVcn);
                if (more.IsEmpty || !MftParser.TryReadRuns(more, _runs))
                    throw new MftUnavailableException($"$MFT extension record {extension} has no $DATA runs");
            }
        }

        foreach (var run in _runs)
            if (run.Lcn < 0) throw new MftUnavailableException("$MFT has a sparse run");

        long covered = 0;
        foreach (var run in _runs) covered += run.Clusters * ClusterBytes;
        if (covered < Data.MftValidDataLength)
            throw new MftUnavailableException(
                $"$MFT runs cover {covered} bytes but valid data is {Data.MftValidDataLength}");
    }

    /// <summary>
    /// Fills <paramref name="into"/> with whole records starting at record
    /// <paramref name="firstRecord"/>. Returns how many records were read; fewer than
    /// requested only at the end of the valid data.
    /// </summary>
    internal int ReadRecords(long firstRecord, Span<byte> into)
    {
        var wanted = Math.Min(into.Length / BytesPerRecord, RecordCount - firstRecord);
        if (wanted <= 0) return 0;

        var offset = firstRecord * BytesPerRecord;
        var remaining = wanted * BytesPerRecord;
        var at = 0;

        while (remaining > 0)
        {
            var (lcn, clusterOffset, available) = Locate(offset);
            if (available <= 0) throw new MftUnavailableException($"$MFT offset {offset} is outside every run");

            var chunk = (int)Math.Min(remaining, available);
            ReadRaw(lcn * ClusterBytes + clusterOffset, into.Slice(at, chunk));

            at += chunk;
            offset += chunk;
            remaining -= chunk;
        }

        return (int)wanted;
    }

    private bool TryReadRecord(long recordNumber, Span<byte> into)
    {
        if (recordNumber < 0 || recordNumber >= RecordCount) return false;
        return ReadRecords(recordNumber, into) == 1;
    }

    /// <summary>Maps a logical $MFT byte offset to (LCN, offset within that run, bytes left in the run).</summary>
    private (long Lcn, long Offset, long Available) Locate(long offset)
    {
        long start = 0;
        foreach (var (lcn, clusters) in _runs)
        {
            var length = clusters * ClusterBytes;
            if (offset < start + length)
                return (lcn, offset - start, start + length - offset);
            start += length;
        }
        return (0, 0, 0);
    }

    /// <summary>
    /// One positioned read. Volume handles insist on sector-aligned offsets and lengths,
    /// so the request is widened to whole sectors and the wanted bytes copied out.
    /// </summary>
    private void ReadRaw(long volumeOffset, Span<byte> into)
    {
        var sector = BytesPerSector;
        var alignedStart = volumeOffset / sector * sector;
        var lead = (int)(volumeOffset - alignedStart);
        var alignedLength = (into.Length + lead + sector - 1) / sector * sector;

        var direct = lead == 0 && alignedLength == into.Length;
        var buffer = direct ? into : new byte[alignedLength];

        var overlapped = new NativeOverlapped
        {
            OffsetLow = (int)(alignedStart & 0xFFFFFFFF),
            OffsetHigh = (int)(alignedStart >> 32),
        };

        fixed (byte* p = buffer)
        {
            if (!Kernel32Extra.ReadFile(_handle, p, (uint)alignedLength, out var read, &overlapped))
            {
                var error = Marshal.GetLastWin32Error();
                throw new MftUnavailableException(
                    $"read of {alignedLength} bytes at {alignedStart} failed: {new Win32Exception(error).Message} (error {error})", error);
            }

            if (read < alignedLength)
                throw new MftUnavailableException($"short read at {alignedStart}: {read} of {alignedLength} bytes");
        }

        if (!direct) buffer.Slice(lead, into.Length).CopyTo(into);
    }

    public void Dispose() => Kernel32Extra.CloseHandle(_handle);
}

/// <summary>The MFT path cannot be used on this volume right now; the caller falls back to the walk.</summary>
internal sealed class MftUnavailableException(string message, int win32Code = 0) : Exception(message)
{
    internal int Win32Code { get; } = win32Code;
}
