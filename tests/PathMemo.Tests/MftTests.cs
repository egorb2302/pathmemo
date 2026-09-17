using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using PathMemo.Platform;
using PathMemo.Scanning;
using PathMemo.Scanning.Mft;
using PathMemo.Snapshots;
using Xunit;

namespace PathMemo.Tests;

/// <summary>
/// Builds NTFS file record segments byte by byte, so the parser can be tested without
/// a volume and without administrator rights.
/// </summary>
internal sealed class RecordBuilder
{
    private const int RecordBytes = 1024;
    private const int SectorBytes = 512;

    private readonly List<byte[]> _attributes = [];
    private ushort _flags = 0x0001;
    private ushort _sequence = 7;
    private ushort _links = 1;
    private ulong _baseReference;

    internal RecordBuilder Directory() { _flags |= 0x0002; return this; }
    internal RecordBuilder NotInUse() { _flags &= 0xFFFE; return this; }
    internal RecordBuilder Sequence(ushort value) { _sequence = value; return this; }
    internal RecordBuilder Links(ushort value) { _links = value; return this; }
    internal RecordBuilder ExtensionOf(long baseRecord, ushort baseSequence)
    {
        _baseReference = (ulong)baseRecord | ((ulong)baseSequence << 48);
        return this;
    }

    internal RecordBuilder StandardInformation(uint attributes, DateTime modifiedUtc)
    {
        var value = new byte[48];
        BinaryPrimitives.WriteInt64LittleEndian(value.AsSpan(8), modifiedUtc.ToFileTimeUtc());
        BinaryPrimitives.WriteUInt32LittleEndian(value.AsSpan(32), attributes);
        _attributes.Add(Resident(0x10, value));
        return this;
    }

    internal RecordBuilder FileName(long parent, ushort parentSequence, string name, byte ns = 1)
    {
        var chars = Encoding.Unicode.GetBytes(name);
        var value = new byte[66 + chars.Length];
        BinaryPrimitives.WriteUInt64LittleEndian(value, (ulong)parent | ((ulong)parentSequence << 48));
        value[64] = (byte)name.Length;
        value[65] = ns;
        chars.CopyTo(value, 66);
        _attributes.Add(Resident(0x30, value));
        return this;
    }

    internal RecordBuilder ResidentData(int bytes, string? streamName = null)
    {
        _attributes.Add(Resident(0x80, new byte[bytes], streamName));
        return this;
    }

    internal const int ClusterBytes = 4096;

    /// <summary>
    /// A non-resident stream. Its data runs are generated to match: one real run for
    /// <paramref name="totalAllocated"/> (or <paramref name="allocated"/>) bytes, then
    /// a hole for whatever of <paramref name="allocated"/> is not backed by clusters.
    /// </summary>
    internal RecordBuilder NonResidentData(long allocated, long real, long? totalAllocated = null,
        ushort flags = 0, ulong startVcn = 0, string? streamName = null, long? runBytes = null)
    {
        _attributes.Add(NonResident(0x80, allocated, real, totalAllocated, flags, startVcn, streamName,
            Runs(runBytes ?? totalAllocated ?? allocated, allocated - (runBytes ?? totalAllocated ?? allocated))));
        return this;
    }

    internal RecordBuilder IndexAllocation(long allocated)
    {
        _attributes.Add(NonResident(0xA0, allocated, allocated, null, 0, 0, "$I30", Runs(allocated, 0)));
        return this;
    }

    /// <summary>One real run at LCN 0x20 plus an optional hole, 4-byte fields throughout.</summary>
    internal static byte[] Runs(long realBytes, long holeBytes)
    {
        var bytes = new List<byte>();
        var real = realBytes / ClusterBytes;
        var hole = holeBytes / ClusterBytes;

        if (real > 0)
        {
            bytes.Add(0x44);
            bytes.AddRange(BitConverter.GetBytes((uint)real));
            bytes.AddRange(BitConverter.GetBytes((uint)0x20));
        }
        if (hole > 0)
        {
            bytes.Add(0x04);
            bytes.AddRange(BitConverter.GetBytes((uint)hole));
        }
        bytes.Add(0);
        return [.. bytes];
    }

    internal RecordBuilder RawAttribute(byte[] attribute) { _attributes.Add(attribute); return this; }

    /// <summary>A resident attribute with a raw value.</summary>
    internal static byte[] Resident(uint type, byte[] value, string? name = null)
    {
        var nameBytes = name is null ? [] : Encoding.Unicode.GetBytes(name);
        var valueOffset = Align8(24 + nameBytes.Length);
        var length = Align8(valueOffset + value.Length);
        var a = new byte[length];

        BinaryPrimitives.WriteUInt32LittleEndian(a, type);
        BinaryPrimitives.WriteUInt32LittleEndian(a.AsSpan(4), (uint)length);
        a[8] = 0;
        a[9] = (byte)(name?.Length ?? 0);
        BinaryPrimitives.WriteUInt16LittleEndian(a.AsSpan(10), 24);
        BinaryPrimitives.WriteUInt32LittleEndian(a.AsSpan(16), (uint)value.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(a.AsSpan(20), (ushort)valueOffset);
        nameBytes.CopyTo(a, 24);
        value.CopyTo(a, valueOffset);
        return a;
    }

    internal static byte[] NonResident(uint type, long allocated, long real, long? totalAllocated,
        ushort flags, ulong startVcn, string? name, byte[]? runs = null)
    {
        var nameBytes = name is null ? [] : Encoding.Unicode.GetBytes(name);
        var headerLength = totalAllocated is null ? 64 : 72;
        var runOffset = Align8(headerLength + nameBytes.Length);
        runs ??= [0x11, 0x01, 0x20, 0x00];       // one cluster at LCN 0x20, then end
        var length = Align8(runOffset + runs.Length);
        var a = new byte[length];

        BinaryPrimitives.WriteUInt32LittleEndian(a, type);
        BinaryPrimitives.WriteUInt32LittleEndian(a.AsSpan(4), (uint)length);
        a[8] = 1;
        a[9] = (byte)(name?.Length ?? 0);
        BinaryPrimitives.WriteUInt16LittleEndian(a.AsSpan(10), (ushort)headerLength);
        BinaryPrimitives.WriteUInt16LittleEndian(a.AsSpan(12), flags);
        BinaryPrimitives.WriteUInt64LittleEndian(a.AsSpan(16), startVcn);
        BinaryPrimitives.WriteUInt64LittleEndian(a.AsSpan(24), startVcn + 100);
        BinaryPrimitives.WriteUInt16LittleEndian(a.AsSpan(32), (ushort)runOffset);
        BinaryPrimitives.WriteUInt16LittleEndian(a.AsSpan(34), (ushort)(totalAllocated is null ? 0 : 4));
        BinaryPrimitives.WriteInt64LittleEndian(a.AsSpan(40), allocated);
        BinaryPrimitives.WriteInt64LittleEndian(a.AsSpan(48), real);
        BinaryPrimitives.WriteInt64LittleEndian(a.AsSpan(56), real);
        if (totalAllocated is { } total) BinaryPrimitives.WriteInt64LittleEndian(a.AsSpan(64), total);
        nameBytes.CopyTo(a, headerLength);
        runs.CopyTo(a, runOffset);
        return a;
    }

    /// <summary>The record as it would sit on disk: fixup applied, so the last two bytes of each sector hold the USN.</summary>
    internal byte[] Build(long recordNumber = 100)
    {
        var record = new byte[RecordBytes];
        "FILE"u8.CopyTo(record);

        const int usaOffset = 48;
        const int usaCount = 3;                         // USN + 2 sectors
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(4), usaOffset);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(6), usaCount);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(16), _sequence);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(18), _links);

        var first = Align8(usaOffset + usaCount * 2);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(20), (ushort)first);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(22), _flags);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(28), RecordBytes);
        BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(32), _baseReference);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(44), (uint)recordNumber);

        var at = first;
        foreach (var attribute in _attributes)
        {
            attribute.CopyTo(record, at);
            at += attribute.Length;
        }
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(at), 0xFFFFFFFF);
        at += 8;
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(24), (uint)at);

        // Apply the fixup the way NTFS does: stash the real sector-end words in the
        // array and overwrite them with the USN.
        const ushort usn = 0x1234;
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(usaOffset), usn);
        for (var i = 1; i < usaCount; i++)
        {
            var end = i * SectorBytes - 2;
            record.AsSpan(end, 2).CopyTo(record.AsSpan(usaOffset + i * 2));
            BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(end), usn);
        }

        return record;
    }

    private static int Align8(int value) => (value + 7) & ~7;
}

public sealed class MftParserTests
{
    private static readonly DateTime When = new(2024, 5, 6, 7, 8, 9, DateTimeKind.Utc);

    private static (MftRecord Record, List<MftName> Names, NameBlobBuilder Blob) Parse(byte[] record, long number = 100)
    {
        Assert.True(MftParser.ApplyFixup(record, 512), "fixup");
        var blob = new NameBlobBuilder(16);
        var names = new List<MftName>();
        var parsed = MftParser.Parse(record, number, RecordBuilder.ClusterBytes, blob, names);
        return (parsed, names, blob);
    }

    [Fact]
    public void Fixup_restores_sector_ends_and_detects_a_torn_record()
    {
        // A resident $DATA long enough to span the first sector boundary, so that a
        // sector-end word lands inside the payload and must be restored exactly.
        var payload = Enumerable.Range(0, 700).Select(i => (byte)(i * 7 + 3)).ToArray();
        var record = new RecordBuilder().RawAttribute(RecordBuilder.Resident(0x80, payload)).Build();

        var copy = (byte[])record.Clone();
        Assert.True(MftParser.ApplyFixup(copy, 512));

        var data = MftParser.FindAttribute(copy, MftParser.AttrData);
        var valueOffset = BinaryPrimitives.ReadUInt16LittleEndian(data[20..]);
        Assert.True(data.Slice(valueOffset, payload.Length).SequenceEqual(payload));

        // Corrupt one sector-end marker: the record must be rejected, not misread.
        var torn = (byte[])record.Clone();
        torn[1022] ^= 0xFF;
        Assert.False(MftParser.ApplyFixup(torn, 512));

        Assert.False(MftParser.ApplyFixup(new byte[1024], 512));
    }

    [Fact]
    public void Reads_a_plain_file()
    {
        var record = new RecordBuilder()
            .Links(1)
            .StandardInformation(0x20, When)
            .FileName(5, 5, "report.docx")
            .NonResidentData(allocated: 8192, real: 5000)
            .Build();

        var (r, names, blob) = Parse(record);

        Assert.True(r.InUse);
        Assert.False(r.IsDirectory);
        Assert.False(r.IsExtension);
        Assert.Equal(7, r.Sequence);
        Assert.Equal(1, r.LinkCount);
        Assert.Equal(0x20u, r.Attributes);
        Assert.Equal(When.ToFileTimeUtc(), r.ModifiedFileTime);
        Assert.Equal(5000, r.Logical);
        Assert.Equal(8192, r.Allocated);
        Assert.False(r.Sparse);

        var name = Assert.Single(names);
        Assert.Equal(5, name.ParentRecord);
        Assert.Equal(5, name.ParentSequence);
        Assert.Equal("report.docx", Encoding.UTF8.GetString(blob.Read(name.NameOffset)));
    }

    [Fact]
    public void Resident_data_costs_no_clusters_but_counts_logically()
    {
        var record = new RecordBuilder()
            .StandardInformation(0x20, When)
            .FileName(5, 5, "tiny.txt")
            .ResidentData(300)
            .Build();

        var (r, _, _) = Parse(record);
        Assert.Equal(300, r.Logical);
        Assert.Equal(0, r.Allocated);
    }

    [Fact]
    public void Dos_names_are_dropped_and_every_other_name_is_a_link()
    {
        var record = new RecordBuilder()
            .Links(2)
            .StandardInformation(0x20, When)
            .FileName(5, 5, "LONGNA~1.DLL", ns: 2)
            .FileName(5, 5, "longnamedlibrary.dll", ns: 1)
            .FileName(77, 3, "same-bytes-other-dir.dll", ns: 3)
            .FileName(78, 1, "posix:name", ns: 0)
            .NonResidentData(4096, 4000)
            .Build();

        var (r, names, blob) = Parse(record);

        Assert.Equal(2, r.LinkCount);
        Assert.Equal(3, names.Count);
        Assert.Equal(["longnamedlibrary.dll", "same-bytes-other-dir.dll", "posix:name"],
            names.Select(n => Encoding.UTF8.GetString(blob.Read(n.NameOffset))).ToArray());
        Assert.Equal(77, names[1].ParentRecord);
        Assert.Equal(3, names[1].ParentSequence);
    }

    [Fact]
    public void Sparse_and_compressed_streams_use_the_total_allocated_field()
    {
        var sparse = new RecordBuilder()
            .StandardInformation(0x220, When)                       // ARCHIVE | SPARSE_FILE
            .FileName(5, 5, "ext4.vhdx")
            .NonResidentData(allocated: 80L << 30, real: 80L << 30, totalAllocated: 12L << 30, flags: 0x8000)
            .Build();

        var (r, _, _) = Parse(sparse);
        Assert.Equal(80L << 30, r.Logical);
        Assert.Equal(12L << 30, r.Allocated);
        Assert.True(r.Sparse);

        var compressed = new RecordBuilder()
            .StandardInformation(0x820, When)                       // ARCHIVE | COMPRESSED
            .FileName(5, 5, "log.txt")
            .NonResidentData(allocated: 1 << 20, real: 1_000_000, totalAllocated: 204_800, flags: 0x0001)
            .Build();

        var (c, _, _) = Parse(compressed);
        Assert.Equal(1_000_000, c.Logical);
        Assert.Equal(204_800, c.Allocated);
        Assert.True(c.Sparse);

        // $BadClus:$Bad - the whole volume as one hole, and no sparse flag to warn you.
        var badClus = new RecordBuilder()
            .StandardInformation(0x06, When)
            .FileName(5, 5, "$BadClus")
            .NonResidentData(allocated: 223L << 30, real: 223L << 30, streamName: "$Bad", runBytes: 0)
            .Build();

        var (b, _, _) = Parse(badClus);
        Assert.Equal(0, b.Allocated);
        Assert.True(b.Sparse);
    }

    [Fact]
    public void Alternate_streams_are_summed_and_continuation_pieces_are_not_double_counted()
    {
        // The unnamed stream is 2 MB in two pieces of 1 MB each; the second piece repeats
        // the header (so its logical size must not be counted twice) but owns its runs
        // (so its clusters must be).
        var record = new RecordBuilder()
            .StandardInformation(0x20, When)
            .FileName(5, 5, "download.exe")
            .NonResidentData(1 << 20, 2_000_000)                                     // unnamed, first piece: 1 MB of runs
            .ResidentData(26, "Zone.Identifier")                                       // ADS, resident
            .NonResidentData(1 << 20, 2_000_000, startVcn: 256)                       // continuation: its own 1 MB of runs
            .NonResidentData(65536, 60_000, streamName: "thumb")                     // second ADS
            .Build();

        var (r, _, _) = Parse(record);
        Assert.Equal(2_000_000 + 26 + 60_000, r.Logical);
        Assert.Equal((2 << 20) + 65536, r.Allocated);
        Assert.False(r.Sparse);
    }

    [Fact]
    public void Directories_carry_their_index_clusters_and_the_directory_flag()
    {
        var record = new RecordBuilder()
            .Directory()
            .StandardInformation(0x10, When)
            .FileName(5, 5, "Windows")
            .IndexAllocation(40960)
            .Build();

        var (r, _, _) = Parse(record);
        Assert.True(r.IsDirectory);
        Assert.Equal(40960, r.Allocated);
        Assert.Equal(0, r.Logical);
    }

    [Fact]
    public void Extension_records_point_at_their_base()
    {
        var record = new RecordBuilder()
            .ExtensionOf(baseRecord: 4242, baseSequence: 9)
            .FileName(31, 2, "twelfth-hard-link.dll")
            .Build(recordNumber: 900_000);

        var (r, names, _) = Parse(record, 900_000);
        Assert.True(r.IsExtension);
        Assert.Equal(4242, r.BaseRecord);
        Assert.Equal(9, r.BaseSequence);
        Assert.Single(names);
    }

    [Fact]
    public void Unused_records_are_reported_as_such_without_parsing_attributes()
    {
        var record = new RecordBuilder()
            .NotInUse()
            .StandardInformation(0x20, When)
            .FileName(5, 5, "deleted.tmp")
            .NonResidentData(4096, 4096)
            .Build();

        var (r, names, _) = Parse(record);
        Assert.False(r.InUse);
        Assert.Empty(names);
        Assert.Equal(0, r.Allocated);
    }

    [Fact]
    public void Decodes_data_runs_including_negative_offsets_and_holes()
    {
        // Run 1 = 0x18 clusters at LCN 0x5634 (1-byte length, 2-byte offset);
        // run 2 = 0x0114 clusters at LCN 0x5634 - 0x0200 (2-byte length, 2-byte negative offset);
        // run 3 = a hole of 0x10 clusters (no offset); then the terminator.
        byte[] runs = [0x21, 0x18, 0x34, 0x56, 0x22, 0x14, 0x01, 0x00, 0xFE, 0x01, 0x10, 0x00];
        var attribute = RecordBuilder.NonResident(0x80, 0, 0, null, 0, 0, null, runs);

        var decoded = new List<(long Lcn, long Clusters)>();
        Assert.True(MftParser.TryReadRuns(attribute, decoded));

        Assert.Equal([(0x5634L, 0x18L), (0x5634L - 0x200L, 0x114L), (-1L, 0x10L)], decoded);
    }

    [Fact]
    public void Finds_an_attribute_by_type_and_starting_vcn()
    {
        var record = new RecordBuilder()
            .StandardInformation(0x20, When)
            .FileName(5, 5, "a")
            .NonResidentData(100, 100)
            .NonResidentData(200, 200, startVcn: 50)
            .Build();

        Assert.True(MftParser.ApplyFixup(record, 512));

        var first = MftParser.FindAttribute(record, MftParser.AttrData);
        var second = MftParser.FindAttribute(record, MftParser.AttrData, 50);
        Assert.Equal(100, BinaryPrimitives.ReadInt64LittleEndian(first[40..]));
        Assert.Equal(200, BinaryPrimitives.ReadInt64LittleEndian(second[40..]));
        Assert.True(MftParser.FindAttribute(record, MftParser.AttrAttributeList).IsEmpty);
    }
}

public sealed class MftAttributeTests
{
    [Fact]
    public void Extended_attribute_bit_is_not_mistaken_for_a_cloud_placeholder()
    {
        // C:\Windows on disk: DIRECTORY-less STD_INFO flags 0x30 plus the EA bit.
        var windows = MftScanner.Win32Attributes(0x30 | 0x40000, isDirectory: true);
        Assert.Equal(0x30u | (uint)FileAttributes.Directory, windows);
        Assert.False(MftScanner.IsCloudPlaceholder(windows));

        // A OneDrive placeholder: reparse point + recall on data access.
        var placeholder = MftScanner.Win32Attributes(0x20 | 0x400 | 0x400000, isDirectory: false);
        Assert.True(MftScanner.IsCloudPlaceholder(placeholder));

        // Recall-on-open survives only on a reparse point.
        Assert.True(MftScanner.IsCloudPlaceholder(MftScanner.Win32Attributes(0x400 | 0x40000, false)));
        Assert.True(MftScanner.IsCloudPlaceholder(MftScanner.Win32Attributes(0x1000, false)));   // offline
        Assert.False(MftScanner.IsCloudPlaceholder(MftScanner.Win32Attributes(0x400, false)));   // a plain junction
    }
}

public sealed class TreeAssemblyTests
{
    [Fact]
    public void Concat_shifts_indexes_and_keeps_every_tree_navigable()
    {
        var a = Tree(("C:\\", 0), ("x", 1), ("y", 1));
        var b = Tree(("D:\\", 0), ("z", 1));

        var joined = TreeAssembly.Concat([a, b]);

        Assert.Equal(5, joined.Count);
        Assert.Equal([0, 3], joined.Roots);
        Assert.Equal(1, joined.VolumeIndex[4]);
        Assert.Equal(3, joined.Parent[4]);
        Assert.Equal("D:\\z", joined.GetPath(4));
        Assert.Equal("C:\\y", joined.GetPath(2));
        Assert.Equal(1..3, joined.Children(0));
        Assert.Equal(4..5, joined.Children(3));
        Assert.Equal(2, joined.FileCount[0]);
        Assert.Equal(1, joined.FileCount[3]);
    }

    [Fact]
    public void Aggregate_counts_a_hard_link_alias_once()
    {
        var tree = Tree(("C:\\", 0), ("owner.bin", 1), ("alias.bin", 1));
        tree.Flags[2] |= NodeFlags.HardlinkAlias;
        tree.Allocated[1] = 1000;
        tree.Allocated[2] = 1000;
        tree.Logical[1] = 900;
        tree.Logical[2] = 900;

        Array.Clear(tree.FileCount);
        TreeAssembly.Aggregate(tree);

        Assert.Equal(1000, tree.Allocated[0]);
        Assert.Equal(900, tree.Logical[0]);
        Assert.Equal(2, tree.FileCount[0]);          // two names, one file's worth of bytes
        Assert.Equal(1000, tree.Allocated[2]);        // the alias keeps its own size for display
    }

    /// <summary>A tiny tree: (name, depth) in breadth-first order, root first.</summary>
    private static NodeStore Tree(params (string Name, int Depth)[] nodes)
    {
        var n = nodes.Length;
        var names = new NameBlobBuilder(8);
        var store = new NodeStore
        {
            Parent = new int[n], NameOffset = new int[n], FirstChild = new int[n], ChildCount = new int[n],
            Allocated = new long[n], Logical = new long[n], FileCount = new int[n], Mtime = new uint[n],
            Attributes = new uint[n], Flags = new NodeFlags[n], LinkCount = new byte[n], VolumeIndex = new byte[n],
            NameBlob = [], Roots = [0],
        };

        store.Parent[0] = NodeStore.NoNode;
        store.Flags[0] = NodeFlags.Directory;
        store.FirstChild[0] = 1;
        store.ChildCount[0] = n - 1;
        for (var i = 0; i < n; i++)
        {
            store.NameOffset[i] = names.Intern(nodes[i].Name);
            if (i > 0) store.Parent[i] = 0;
        }

        var built = new NodeStore
        {
            Parent = store.Parent, NameOffset = store.NameOffset, FirstChild = store.FirstChild,
            ChildCount = store.ChildCount, Allocated = store.Allocated, Logical = store.Logical,
            FileCount = store.FileCount, Mtime = store.Mtime, Attributes = store.Attributes,
            Flags = store.Flags, LinkCount = store.LinkCount, VolumeIndex = store.VolumeIndex,
            NameBlob = names.ToBlob(), Roots = store.Roots,
        };
        TreeAssembly.Aggregate(built);
        return built;
    }
}

public sealed class HardlinkTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "pathmemo-hl-" + Guid.NewGuid().ToString("N")[..12]);

    public HardlinkTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static VolumeInfo VolumeFor(string root)
    {
        var owner = VolumeInfo.Enumerate().First(v => root.StartsWith(v.Root, StringComparison.OrdinalIgnoreCase));
        return owner with { Root = root + Path.DirectorySeparatorChar };
    }

    [Fact]
    public async Task Walk_scanner_counts_a_hard_linked_file_once()
    {
        var size = (int)DirectoryLister.HardlinkThreshold + 4096;
        var original = Path.Combine(_root, "a", "big.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(original)!);
        File.WriteAllBytes(original, new byte[size]);

        var link = Path.Combine(_root, "b", "deep", "big-link.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        if (!TryHardLink(link, original)) return;          // FAT or a refusing volume: nothing to test

        var small = Path.Combine(_root, "small.bin");
        File.WriteAllBytes(small, new byte[10_000]);

        var volume = VolumeFor(_root);
        var result = await new WalkScanner().ScanAsync(
            new ScanRequest { Roots = [volume.Root], Parallelism = 2 }, [volume], null, CancellationToken.None);

        var tree = result.Tree;
        var root = tree.Roots[0];

        Assert.Equal(3, tree.FileCount[root]);

        var aliases = Enumerable.Range(0, tree.Count).Where(i => (tree.Flags[i] & NodeFlags.HardlinkAlias) != 0).ToArray();
        var alias = Assert.Single(aliases);

        // Breadth-first: the shallower name (depth 2) owns, the deeper one (depth 3) is the alias.
        Assert.Equal("big-link.bin", tree.Name(alias));
        Assert.Equal(2, tree.LinkCount[alias]);

        var owner = Analysis.TreeQuery.Find(tree, original);
        Assert.Equal(2, tree.LinkCount[owner]);
        Assert.True((tree.Flags[owner] & NodeFlags.HardlinkAlias) == 0);

        // Unique total = one big file + the small one, not two big files.
        var bigOnDisk = tree.Allocated[owner];
        var smallOnDisk = tree.Allocated[Analysis.TreeQuery.Find(tree, small)];
        Assert.Equal(bigOnDisk + smallOnDisk, tree.Allocated[root]);
        Assert.Equal(bigOnDisk, tree.Allocated[Analysis.TreeQuery.Find(tree, Path.Combine(_root, "a"))]);
        Assert.Equal(0, tree.Allocated[Analysis.TreeQuery.Find(tree, Path.Combine(_root, "b"))]);

        Assert.Equal(0, Analysis.TreeQuery.Size(tree, alias, Analysis.SizeMode.Unique));
        Assert.Equal(bigOnDisk, Analysis.TreeQuery.Size(tree, alias, Analysis.SizeMode.Allocated));
    }

    /// <summary>
    /// Only runs elevated - the $MFT cannot be opened otherwise - and then compares the
    /// MFT scanner against the walk on the same directory tree.
    /// </summary>
    [Fact]
    public async Task Mft_scanner_agrees_with_the_walk_when_elevated()
    {
        var volume = VolumeFor(_root);
        if (!volume.MftScanAvailability.Available) return;

        File.WriteAllBytes(Path.Combine(_root, "one.bin"), new byte[300_000]);
        Directory.CreateDirectory(Path.Combine(_root, "sub", "deeper"));
        File.WriteAllBytes(Path.Combine(_root, "sub", "two.bin"), new byte[1_500_000]);
        File.WriteAllBytes(Path.Combine(_root, "sub", "deeper", "three.bin"), new byte[70_000]);
        File.WriteAllBytes(Path.Combine(_root, "sub", "deeper", "tiny.txt"), new byte[10]);   // resident
        TryHardLink(Path.Combine(_root, "two-link.bin"), Path.Combine(_root, "sub", "two.bin"));

        var request = new ScanRequest { Roots = [volume.Root], Parallelism = 2 };
        var mft = await new MftScanner().ScanAsync(request, [volume], null, CancellationToken.None);
        var walk = await new WalkScanner().ScanAsync(request, [volume], null, CancellationToken.None);

        Assert.Equal(ScannerKind.Mft, mft.Scanner);
        Assert.Equal(walk.FileCount, mft.FileCount);
        Assert.Equal(walk.DirectoryCount, mft.DirectoryCount);
        Assert.Equal(walk.LogicalBytes, mft.LogicalBytes);

        // On disk: the walk rounds the resident file up to a cluster, the MFT knows it
        // occupies none; everything else must agree exactly.
        var cluster = volume.ClusterBytes;
        Assert.InRange(walk.AllocatedBytes - mft.AllocatedBytes, 0, cluster);

        for (var i = 0; i < walk.Tree.Count; i++)
        {
            var path = walk.Tree.GetPath(i);
            var node = Analysis.TreeQuery.Find(mft.Tree, path);
            Assert.NotEqual(NodeStore.NoNode, node);
            Assert.Equal(walk.Tree.Logical[i], mft.Tree.Logical[node]);
            Assert.Equal(walk.Tree.IsDirectory(i), mft.Tree.IsDirectory(node));
        }

        var alias = Enumerable.Range(0, mft.Tree.Count).Where(i => (mft.Tree.Flags[i] & NodeFlags.HardlinkAlias) != 0).ToArray();
        Assert.Single(alias);
        Assert.Equal("two.bin", mft.Tree.Name(alias[0]));      // the deeper name is the alias
    }

    private static bool TryHardLink(string link, string target)
    {
        var psi = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        psi.ArgumentList.Add("/c");
        psi.ArgumentList.Add("mklink");
        psi.ArgumentList.Add("/H");
        psi.ArgumentList.Add(link);
        psi.ArgumentList.Add(target);

        using var process = Process.Start(psi);
        if (process is null) return false;
        process.WaitForExit(10_000);
        return process.ExitCode == 0 && File.Exists(link);
    }
}
