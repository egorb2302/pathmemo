using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace PathMemo.Scanning.Mft;

/// <summary>What one file record segment says about the file it belongs to.</summary>
internal struct MftRecord
{
    /// <summary>Record number this segment describes: its own, or its base's for an extension segment.</summary>
    public long BaseRecord;

    /// <summary>Sequence number the base reference expects. For a base segment, its own.</summary>
    public ushort BaseSequence;

    /// <summary>The segment's own sequence number, from its header.</summary>
    public ushort Sequence;

    public bool InUse;
    public bool IsDirectory;
    public bool IsExtension;
    public bool HasAttributeList;
    public bool HasStandardInformation;

    /// <summary>Hard link count from the record header, valid on the base segment.</summary>
    public ushort LinkCount;

    /// <summary>Sum of every $DATA stream's real size, alternate streams included.</summary>
    public long Logical;

    /// <summary>Clusters actually allocated to the file: data streams plus the directory index.</summary>
    public long Allocated;

    /// <summary>Win32 attributes from $STANDARD_INFORMATION.</summary>
    public uint Attributes;

    /// <summary>Last write time, FILETIME.</summary>
    public long ModifiedFileTime;

    /// <summary>How many names this segment carried; they were appended to the caller's sink.</summary>
    public int NameCount;

    public bool Sparse;
}

/// <summary>One name of a file: which directory it sits in, and what it is called there.</summary>
internal readonly record struct MftName(long ParentRecord, ushort ParentSequence, int NameOffset);

/// <summary>
/// Decodes NTFS file record segments (README section 4.3). Pure: bytes in, facts out.
/// </summary>
/// <remarks>
/// <para>
/// A record is a header followed by attributes. Only five attribute types matter here:
/// $STANDARD_INFORMATION for attributes and the write time, $FILE_NAME for the tree
/// (one per hard link), $DATA for sizes, $INDEX_ALLOCATION for a directory's own
/// clusters, and $ATTRIBUTE_LIST as the sign that more attributes live in other
/// records. Everything else is skipped by length.
/// </para>
/// <para>
/// Sizes come from the non-resident attribute itself, which is why this path needs no
/// handle per file: the logical size is a header field, and the on-disk size is the
/// sum of the real data runs - holes in a sparse or compressed stream count for
/// nothing. Resident streams cost no clusters at all: their bytes are inside the MFT
/// record, which the ntfs.metadata probe already counts.
/// </para>
/// </remarks>
internal static class MftParser
{
    private const uint MagicFile = 0x454C4946;      // "FILE"

    internal const uint AttrStandardInformation = 0x10;
    internal const uint AttrAttributeList = 0x20;
    internal const uint AttrFileName = 0x30;
    internal const uint AttrData = 0x80;
    internal const uint AttrIndexAllocation = 0xA0;
    internal const uint AttrEnd = 0xFFFFFFFF;

    private const ushort RecordInUse = 0x0001;
    private const ushort RecordIsDirectory = 0x0002;

    private const ushort AttrFlagCompressionMask = 0x00FF;
    private const ushort AttrFlagSparse = 0x8000;

    private const byte NamespaceDos = 2;

    /// <summary>
    /// Undoes the update sequence array in place. Returns false for a record that was
    /// never written (zeroed), is not a FILE record, or fails the fixup check - the last
    /// meaning a torn write, which is the only corruption this code can detect.
    /// </summary>
    internal static bool ApplyFixup(Span<byte> record, int bytesPerSector)
    {
        if (record.Length < 42) return false;
        if (BinaryPrimitives.ReadUInt32LittleEndian(record) != MagicFile) return false;

        int usaOffset = BinaryPrimitives.ReadUInt16LittleEndian(record[4..]);
        int usaCount = BinaryPrimitives.ReadUInt16LittleEndian(record[6..]);

        if (usaCount < 2 || usaOffset + usaCount * 2 > record.Length) return false;
        if ((usaCount - 1) * bytesPerSector > record.Length) return false;

        var usn = BinaryPrimitives.ReadUInt16LittleEndian(record[usaOffset..]);

        for (var i = 1; i < usaCount; i++)
        {
            var at = i * bytesPerSector - 2;
            if (BinaryPrimitives.ReadUInt16LittleEndian(record[at..]) != usn) return false;

            record[at] = record[usaOffset + i * 2];
            record[at + 1] = record[usaOffset + i * 2 + 1];
        }

        return true;
    }

    /// <summary>
    /// Reads a fixed-up record. Names are appended to <paramref name="names"/> as
    /// offsets into <paramref name="blob"/>; DOS 8.3 aliases are dropped, since they
    /// name the same link as the Win32 name beside them.
    /// </summary>
    internal static MftRecord Parse(
        ReadOnlySpan<byte> record, long recordNumber, long bytesPerCluster,
        Snapshots.NameBlobBuilder blob, List<MftName> names)
    {
        var result = new MftRecord
        {
            BaseRecord = recordNumber,
            Sequence = BinaryPrimitives.ReadUInt16LittleEndian(record[16..]),
            LinkCount = BinaryPrimitives.ReadUInt16LittleEndian(record[18..]),
        };
        result.BaseSequence = result.Sequence;

        var flags = BinaryPrimitives.ReadUInt16LittleEndian(record[22..]);
        result.InUse = (flags & RecordInUse) != 0;
        result.IsDirectory = (flags & RecordIsDirectory) != 0;

        var baseReference = BinaryPrimitives.ReadUInt64LittleEndian(record[32..]);
        if (baseReference != 0)
        {
            result.IsExtension = true;
            result.BaseRecord = (long)(baseReference & 0x0000FFFFFFFFFFFF);
            result.BaseSequence = (ushort)(baseReference >> 48);
        }

        if (!result.InUse) return result;

        int offset = BinaryPrimitives.ReadUInt16LittleEndian(record[20..]);
        var used = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(record[24..]), (uint)record.Length);

        while (offset + 16 <= used)
        {
            var type = BinaryPrimitives.ReadUInt32LittleEndian(record[offset..]);
            if (type == AttrEnd) break;

            var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(record[(offset + 4)..]);
            if (length < 24 || offset + length > used) break;

            var attribute = record.Slice(offset, length);
            var nonResident = attribute[8] != 0;
            var attrFlags = BinaryPrimitives.ReadUInt16LittleEndian(attribute[12..]);

            switch (type)
            {
                case AttrStandardInformation when !nonResident:
                    ReadStandardInformation(attribute, ref result);
                    break;

                case AttrAttributeList:
                    result.HasAttributeList = true;
                    break;

                case AttrFileName when !nonResident:
                    if (TryReadFileName(attribute, blob, out var name))
                    {
                        names.Add(name);
                        result.NameCount++;
                    }
                    break;

                case AttrData:
                case AttrIndexAllocation:
                    AddStream(attribute, nonResident, attrFlags, type == AttrData, bytesPerCluster, ref result);
                    break;
            }

            offset += length;
        }

        return result;
    }

    private static void ReadStandardInformation(ReadOnlySpan<byte> attribute, ref MftRecord result)
    {
        var value = ResidentValue(attribute);
        if (value.Length < 36) return;

        result.HasStandardInformation = true;
        result.ModifiedFileTime = BinaryPrimitives.ReadInt64LittleEndian(value[8..]);
        result.Attributes = BinaryPrimitives.ReadUInt32LittleEndian(value[32..]);
    }

    private static bool TryReadFileName(
        ReadOnlySpan<byte> attribute, Snapshots.NameBlobBuilder blob, out MftName name)
    {
        name = default;
        var value = ResidentValue(attribute);
        if (value.Length < 66) return false;

        var ns = value[65];
        if (ns == NamespaceDos) return false;

        int chars = value[64];
        if (66 + chars * 2 > value.Length) return false;

        var parent = BinaryPrimitives.ReadUInt64LittleEndian(value);
        var utf16 = MemoryMarshal.Cast<byte, char>(value.Slice(66, chars * 2));

        name = new MftName(
            (long)(parent & 0x0000FFFFFFFFFFFF),
            (ushort)(parent >> 48),
            blob.Intern(utf16));
        return true;
    }

    private static void AddStream(
        ReadOnlySpan<byte> attribute, bool nonResident, ushort attrFlags, bool isData,
        long bytesPerCluster, ref MftRecord result)
    {
        if (!nonResident)
        {
            // Resident: the bytes live inside this MFT record and cost no cluster.
            if (isData)
            {
                var value = ResidentValue(attribute);
                result.Logical += value.Length;
            }
            return;
        }

        if (attribute.Length < 64) return;

        // On-disk bytes come from the data runs: real runs count, holes do not. That is
        // the same answer for every kind of stream - plain, sparse, compressed, and
        // $BadClus:$Bad, which spans the whole volume without a sparse flag - and it
        // is right for every piece of a stream split across records, since each piece
        // carries only its own runs.
        var startVcn = BinaryPrimitives.ReadUInt64LittleEndian(attribute[16..]);
        var thin = (attrFlags & (AttrFlagCompressionMask | AttrFlagSparse)) != 0;

        if (TrySumRuns(attribute, out var clusters, out var holes))
        {
            result.Allocated += clusters * bytesPerCluster;
            if (holes > 0) result.Sparse = true;
        }
        else if (startVcn == 0)
        {
            // Unreadable run list: fall back to the header. TotalAllocated is the honest
            // figure for a sparse or compressed stream when it is present.
            var allocated = thin && attribute.Length >= 72
                ? BinaryPrimitives.ReadInt64LittleEndian(attribute[64..])
                : BinaryPrimitives.ReadInt64LittleEndian(attribute[40..]);
            if (allocated > 0) result.Allocated += allocated;
        }

        if (thin) result.Sparse = true;

        // Only the first piece of a stream carries a meaningful logical size.
        if (startVcn != 0 || !isData) return;

        var real = BinaryPrimitives.ReadInt64LittleEndian(attribute[48..]);
        if (real > 0) result.Logical += real;
    }

    /// <summary>Clusters backed by real runs versus holes, without materialising the run list.</summary>
    private static bool TrySumRuns(ReadOnlySpan<byte> attribute, out long clusters, out long holes)
    {
        clusters = 0;
        holes = 0;

        int runOffset = BinaryPrimitives.ReadUInt16LittleEndian(attribute[32..]);
        if (runOffset < 64 || runOffset >= attribute.Length) return false;

        var at = runOffset;
        while (at < attribute.Length)
        {
            var header = attribute[at++];
            if (header == 0) return true;

            var lengthSize = header & 0x0F;
            var offsetSize = header >> 4;
            if (lengthSize == 0 || lengthSize > 8 || offsetSize > 8) return false;
            if (at + lengthSize + offsetSize > attribute.Length) return false;

            long length = 0;
            for (var i = lengthSize - 1; i >= 0; i--) length = (length << 8) | attribute[at + i];
            at += lengthSize + offsetSize;

            if (offsetSize == 0) holes += length;
            else clusters += length;
        }

        return true;
    }

    private static ReadOnlySpan<byte> ResidentValue(ReadOnlySpan<byte> attribute)
    {
        if (attribute.Length < 24) return default;

        var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(attribute[16..]);
        int offset = BinaryPrimitives.ReadUInt16LittleEndian(attribute[20..]);

        if (offset < 24 || offset > attribute.Length) return default;
        length = Math.Min(length, attribute.Length - offset);
        return attribute.Slice(offset, length);
    }

    /// <summary>
    /// Decodes the data runs of a non-resident attribute into (LCN, cluster count)
    /// extents. Used for the $MFT itself, whose $DATA runs say where every other record
    /// lives. Returns false on a malformed run list.
    /// </summary>
    internal static bool TryReadRuns(ReadOnlySpan<byte> attribute, List<(long Lcn, long Clusters)> runs)
    {
        if (attribute.Length < 40 || attribute[8] == 0) return false;

        int runOffset = BinaryPrimitives.ReadUInt16LittleEndian(attribute[32..]);
        if (runOffset >= attribute.Length) return false;

        long lcn = 0;
        var at = runOffset;

        while (at < attribute.Length)
        {
            var header = attribute[at++];
            if (header == 0) break;

            var lengthSize = header & 0x0F;
            var offsetSize = header >> 4;
            if (lengthSize == 0 || lengthSize > 8 || offsetSize > 8) return false;
            if (at + lengthSize + offsetSize > attribute.Length) return false;

            long length = 0;
            for (var i = lengthSize - 1; i >= 0; i--) length = (length << 8) | attribute[at + i];
            at += lengthSize;

            if (offsetSize == 0)
            {
                // A hole. Legal for sparse files, never for the $MFT: report it as a run
                // at LCN -1 so the caller can refuse.
                runs.Add((-1, length));
                continue;
            }

            long delta = (sbyte)attribute[at + offsetSize - 1];       // sign-extend the top byte
            for (var i = offsetSize - 2; i >= 0; i--) delta = (delta << 8) | attribute[at + i];
            at += offsetSize;

            lcn += delta;
            runs.Add((lcn, length));
        }

        return runs.Count > 0;
    }

    /// <summary>
    /// Finds the first attribute of <paramref name="type"/> in a fixed-up record and
    /// returns it, or an empty span.
    /// </summary>
    internal static ReadOnlySpan<byte> FindAttribute(ReadOnlySpan<byte> record, uint type, ulong startVcn = 0)
    {
        if (record.Length < 42) return default;

        int offset = BinaryPrimitives.ReadUInt16LittleEndian(record[20..]);
        var used = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(record[24..]), (uint)record.Length);

        while (offset + 16 <= used)
        {
            var t = BinaryPrimitives.ReadUInt32LittleEndian(record[offset..]);
            if (t == AttrEnd) break;

            var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(record[(offset + 4)..]);
            if (length < 24 || offset + length > used) break;

            if (t == type)
            {
                var attribute = record.Slice(offset, length);
                var vcn = attribute[8] != 0 && attribute.Length >= 24
                    ? BinaryPrimitives.ReadUInt64LittleEndian(attribute[16..])
                    : 0;
                if (vcn == startVcn) return attribute;
            }

            offset += length;
        }

        return default;
    }

    /// <summary>
    /// Entries of a resident $ATTRIBUTE_LIST: which record holds which piece. Only the
    /// (type, startVcn, record) triple is needed, to follow a fragmented $MFT.
    /// </summary>
    internal static List<(uint Type, ulong StartVcn, long Record)> ReadAttributeList(ReadOnlySpan<byte> attribute)
    {
        var entries = new List<(uint, ulong, long)>();
        if (attribute.Length < 24 || attribute[8] != 0) return entries;

        var value = ResidentValue(attribute);
        var at = 0;
        while (at + 26 <= value.Length)
        {
            var type = BinaryPrimitives.ReadUInt32LittleEndian(value[at..]);
            int length = BinaryPrimitives.ReadUInt16LittleEndian(value[(at + 4)..]);
            if (length < 26) break;

            var startVcn = BinaryPrimitives.ReadUInt64LittleEndian(value[(at + 8)..]);
            var reference = BinaryPrimitives.ReadUInt64LittleEndian(value[(at + 16)..]);
            entries.Add((type, startVcn, (long)(reference & 0x0000FFFFFFFFFFFF)));

            at += length;
        }

        return entries;
    }
}
