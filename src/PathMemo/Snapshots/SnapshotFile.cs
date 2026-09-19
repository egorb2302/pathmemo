using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using PathMemo.Platform;
using PathMemo.Scanning;

namespace PathMemo.Snapshots;

internal enum SectionKind : uint
{
    Names = 1,
    Nodes = 2,
    Volumes = 3,
    Errors = 4,
    Roots = 5,

    /// <summary>
    /// Change journal watermarks (README section 4.5). Added in P9 and optional: a
    /// snapshot written before it simply cannot be the base of an incremental rescan.
    /// </summary>
    Usn = 6,
}

/// <summary>
/// Which parts of a snapshot a reader actually needs (README sections 5.2, 20).
/// </summary>
/// <remarks>
/// Sections are compressed and indexed independently precisely so that a command can skip
/// the ones it will not look at: <c>errors</c> wants a few hundred strings out of a file
/// whose tree is 74 MB, and inflating that tree to print them costs a quarter of a second
/// and a hundred megabytes for nothing.
/// </remarks>
[Flags]
internal enum SnapshotParts
{
    /// <summary>The header alone: scanner, flags, times, node count.</summary>
    Meta = 0,

    /// <summary>The file tree - NODES, NAMES and ROOTS, which are one thing in practice.</summary>
    Tree = 1 << 0,

    Volumes = 1 << 1,
    Errors = 1 << 2,
    Usn = 1 << 3,

    All = Tree | Volumes | Errors | Usn,
}

/// <summary>
/// Reads and writes <c>.pmsnap</c>: one scan's complete file tree.
/// </summary>
/// <remarks>
/// <para>
/// A binary snapshot rather than rows in SQLite. A million files as rows with full paths
/// is 150-250 MB per scan; with any retention policy worth having, a tool whose purpose
/// is freeing disk space would itself consume tens of gigabytes (README section 5.1,
/// threat T15). The same tree here is 15-25 MB.
/// </para>
/// <para>
/// Sections are compressed and indexed independently so a reader can load only what it
/// needs - the tree view never touches attributes or timestamps.
/// </para>
/// </remarks>
internal static class SnapshotFile
{
    private static readonly byte[] Magic = "PMSNAP"u8.ToArray();
    private const ushort FormatVersion = 1;
    private const int HeaderBytes = 40;
    private const int SectionEntryBytes = 32;

    private const uint CompressionNone = 0;
    private const uint CompressionDeflate = 1;

    internal static void Write(string path, ScanResult result)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var tree = result.Tree;
        var sections = new List<(SectionKind Kind, byte[] Raw)>
        {
            (SectionKind.Names, tree.NameBlob),
            (SectionKind.Nodes, PackNodes(tree)),
            (SectionKind.Roots, MemoryMarshal.AsBytes<int>(tree.Roots).ToArray()),
            (SectionKind.Volumes, PackVolumes(result.Volumes)),
            (SectionKind.Errors, PackErrors(result.Errors)),
            (SectionKind.Usn, PackUsn(result.Usn)),
        };

        var stored = new List<(SectionKind Kind, uint Compression, int RawLength, byte[] Bytes)>();
        foreach (var (kind, raw) in sections)
        {
            var compressed = Deflate(raw);

            // Only keep the compressed form if it actually pays: small sections and
            // already-dense data can grow.
            stored.Add(compressed.Length < raw.Length
                ? (kind, CompressionDeflate, raw.Length, compressed)
                : (kind, CompressionNone, raw.Length, raw));
        }

        using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);

        var header = new byte[HeaderBytes + stored.Count * SectionEntryBytes];
        Magic.CopyTo(header, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(8), FormatVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(10), (ushort)stored.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), (uint)tree.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16), (uint)result.Flags);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(20),
            new DateTimeOffset(result.StartedUtc, TimeSpan.Zero).ToUnixTimeSeconds());
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(28), (long)result.Duration.TotalMilliseconds);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(36), (uint)result.Scanner);

        var offset = (long)header.Length;
        for (var i = 0; i < stored.Count; i++)
        {
            var at = HeaderBytes + i * SectionEntryBytes;
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(at), (uint)stored[i].Kind);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(at + 4), stored[i].Compression);
            BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(at + 8), stored[i].RawLength);
            BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(at + 16), stored[i].Bytes.Length);
            BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(at + 24), offset);
            offset += stored[i].Bytes.Length;
        }

        file.Write(header);
        foreach (var section in stored) file.Write(section.Bytes);
    }

    /// <summary>
    /// Whether this file still looks like a snapshot this build can open, by its header
    /// alone. Cheap enough to check on every reconciliation, which is what lets a
    /// truncated or foreign file be marked unavailable instead of failing later
    /// (README section 5.4, threat T14).
    /// </summary>
    internal static bool IsReadable(string path)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, HeaderBytes);
            var head = new byte[HeaderBytes];
            file.ReadExactly(head);

            return head.AsSpan(0, 6).SequenceEqual(Magic)
                && BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(8)) == FormatVersion;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Opens a snapshot, decompressing only the sections <paramref name="parts"/> asks for.
    /// </summary>
    /// <remarks>
    /// Every section is inflated <em>straight into its destination</em> - the node arrays,
    /// the name blob - rather than into a byte[] that is then copied. Materialising the
    /// packed form first doubled the peak: a 1.58M-node tree is 68 MB of arrays plus a
    /// 68 MB buffer plus the compressed bytes, which is how an open snapshot came to hold
    /// 200 MB against a 150 MB budget (README section 20).
    /// </remarks>
    internal static SnapshotContents Read(string path, SnapshotParts parts = SnapshotParts.All)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);

        var head = new byte[HeaderBytes];
        file.ReadExactly(head);

        if (!head.AsSpan(0, 6).SequenceEqual(Magic))
            throw new InvalidDataException($"not a pathmemo snapshot: {path}");

        var version = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(8));
        if (version != FormatVersion)
            throw new InvalidDataException($"snapshot format v{version} is not supported by this build");

        var sectionCount = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(10));
        var nodeCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(12));
        var flags = (ScanFlags)BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(16));
        var startedUtc = DateTimeOffset.FromUnixTimeSeconds(
            BinaryPrimitives.ReadInt64LittleEndian(head.AsSpan(20))).UtcDateTime;
        var duration = TimeSpan.FromMilliseconds(BinaryPrimitives.ReadInt64LittleEndian(head.AsSpan(28)));
        var scanner = (ScannerKind)BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(36));

        var table = new byte[sectionCount * SectionEntryBytes];
        file.ReadExactly(table);

        var sections = new Dictionary<SectionKind, Section>(sectionCount);
        for (var i = 0; i < sectionCount; i++)
        {
            var at = i * SectionEntryBytes;
            sections[(SectionKind)BinaryPrimitives.ReadUInt32LittleEndian(table.AsSpan(at))] = new Section(
                Compression: BinaryPrimitives.ReadUInt32LittleEndian(table.AsSpan(at + 4)),
                RawLength: (int)BinaryPrimitives.ReadInt64LittleEndian(table.AsSpan(at + 8)),
                Offset: BinaryPrimitives.ReadInt64LittleEndian(table.AsSpan(at + 24)));
        }

        var tree = NodeStore.Empty;
        if ((parts & SnapshotParts.Tree) != 0)
        {
            var names = Payload(file, sections[SectionKind.Names]);
            var roots = MemoryMarshal.Cast<byte, int>(Payload(file, sections[SectionKind.Roots])).ToArray();
            tree = Inflating(file, sections[SectionKind.Nodes],
                stream => UnpackNodes(stream, nodeCount, names, roots));
        }

        return new SnapshotContents
        {
            Parts = parts,
            Tree = tree,
            Volumes = (parts & SnapshotParts.Volumes) != 0
                ? UnpackVolumes(Payload(file, sections[SectionKind.Volumes])) : [],
            Errors = (parts & SnapshotParts.Errors) != 0
                ? UnpackErrors(Payload(file, sections[SectionKind.Errors])) : [],

            // Looked up rather than indexed: every section added after v1 has to be
            // optional, or opening yesterday's snapshot would throw instead of simply
            // meaning "no incremental rescan from this one" (README section 5.2).
            Usn = (parts & SnapshotParts.Usn) != 0 && sections.TryGetValue(SectionKind.Usn, out var usn)
                ? UnpackUsn(Payload(file, usn)) : [],
            Scanner = scanner,
            Flags = flags,
            StartedUtc = startedUtc,
            Duration = duration,
        };
    }

    /// <summary>
    /// One section's entry in the index: where it is and how it is stored.
    /// </summary>
    /// <remarks>
    /// The stored (compressed) length is in the file but not here: the reader either inflates
    /// until it has <c>RawLength</c> bytes or reads exactly that many, so it never needs to
    /// know how much of the file that took. A field read and ignored would only invite a
    /// check that does not exist.
    /// </remarks>
    private readonly record struct Section(uint Compression, int RawLength, long Offset);

    /// <summary>
    /// Runs <paramref name="read"/> over the section's decompressed bytes as a stream.
    /// </summary>
    /// <remarks>
    /// The deflate stream reads through the file directly and may buffer past the end of
    /// its own data, which is harmless: every section seeks before it reads, and nothing
    /// reads two of them at once.
    /// </remarks>
    private static T Inflating<T>(FileStream file, Section section, Func<Stream, T> read)
    {
        file.Position = section.Offset;
        if (section.Compression != CompressionDeflate) return read(file);

        using var deflate = new DeflateStream(file, CompressionMode.Decompress, leaveOpen: true);
        return read(deflate);
    }

    private static byte[] Payload(FileStream file, Section section) =>
        Inflating(file, section, stream =>
        {
            var raw = new byte[section.RawLength];
            stream.ReadExactly(raw);
            return raw;
        });

    // Arrays are written back to back in a fixed order. Every one is blittable, so this is
    // a memcpy per array rather than a per-element loop.
    private static byte[] PackNodes(NodeStore tree)
    {
        var n = tree.Count;
        var size = n * (4 + 4 + 4 + 4 + 8 + 8 + 4 + 4 + 4 + 1 + 1 + 1);
        var buffer = new byte[size];
        var at = 0;

        void Put<T>(T[] array) where T : unmanaged
        {
            var bytes = MemoryMarshal.AsBytes(array.AsSpan());
            bytes.CopyTo(buffer.AsSpan(at));
            at += bytes.Length;
        }

        Put(tree.Parent);
        Put(tree.NameOffset);
        Put(tree.FirstChild);
        Put(tree.ChildCount);
        Put(tree.Allocated);
        Put(tree.Logical);
        Put(tree.FileCount);
        Put(tree.Mtime);
        Put(tree.Attributes);
        Put(tree.Flags);
        Put(tree.LinkCount);
        Put(tree.VolumeIndex);

        return buffer;
    }

    /// <summary>
    /// Reads the node arrays straight out of the section's stream, in the order
    /// <see cref="PackNodes"/> wrote them.
    /// </summary>
    /// <remarks>
    /// Object initialisers are evaluated in source order, which is what makes the
    /// positional reads below correct - the same thing <see cref="PackNodes"/> relies on.
    /// </remarks>
    private static NodeStore UnpackNodes(Stream packed, int n, byte[] names, int[] roots)
    {
        T[] Take<T>() where T : unmanaged
        {
            var array = new T[n];

            // AsSpan() first, deliberately. AsBytes has a Span and a ReadOnlySpan overload,
            // and an array argument picks the writable one only up to C# 13; C# 14's
            // first-class span conversions make it choose ReadOnlySpan, and the read below
            // stops compiling. Naming the span keeps this building on any toolchain.
            var bytes = MemoryMarshal.AsBytes(array.AsSpan());
            packed.ReadExactly(bytes);
            return array;
        }

        return new NodeStore
        {
            Parent = Take<int>(),
            NameOffset = Take<int>(),
            FirstChild = Take<int>(),
            ChildCount = Take<int>(),
            Allocated = Take<long>(),
            Logical = Take<long>(),
            FileCount = Take<int>(),
            Mtime = Take<uint>(),
            Attributes = Take<uint>(),
            Flags = Take<NodeFlags>(),
            LinkCount = Take<byte>(),
            VolumeIndex = Take<byte>(),
            NameBlob = names,
            Roots = roots,
        };
    }

    private static byte[] PackVolumes(IReadOnlyList<VolumeInfo> volumes)
    {
        using var memory = new MemoryStream();
        using var w = new BinaryWriter(memory, Encoding.UTF8, leaveOpen: true);

        w.Write(volumes.Count);
        foreach (var v in volumes)
        {
            w.Write(v.Root);
            w.Write(v.Letter);
            w.Write(v.Label ?? "");
            w.Write(v.FileSystem);
            w.Write(v.Serial);
            w.Write(v.VolumeGuidPath ?? "");
            w.Write(v.ClusterBytes);
            w.Write(v.TotalBytes);
            w.Write(v.FreeBytes);
            w.Write((int)v.DriveType);
        }

        w.Flush();
        return memory.ToArray();
    }

    private static IReadOnlyList<VolumeInfo> UnpackVolumes(byte[] bytes)
    {
        using var memory = new MemoryStream(bytes);
        using var r = new BinaryReader(memory, Encoding.UTF8);

        var count = r.ReadInt32();
        var volumes = new List<VolumeInfo>(count);
        for (var i = 0; i < count; i++)
        {
            var root = r.ReadString();
            var letter = r.ReadString();
            var label = r.ReadString();
            var fs = r.ReadString();
            var serial = r.ReadUInt32();
            var guid = r.ReadString();
            var cluster = r.ReadInt32();
            var total = r.ReadUInt64();
            var free = r.ReadUInt64();
            var driveType = (DriveType)r.ReadInt32();

            volumes.Add(new VolumeInfo(root, letter,
                label.Length == 0 ? null : label, fs, serial,
                guid.Length == 0 ? null : guid,
                cluster, total, free, driveType));
        }
        return volumes;
    }

    private static byte[] PackUsn(IReadOnlyList<UsnState> states)
    {
        using var memory = new MemoryStream();
        using var w = new BinaryWriter(memory, Encoding.UTF8, leaveOpen: true);

        w.Write(states.Count);
        foreach (var state in states)
        {
            w.Write(state.Letter);
            w.Write(state.Serial);
            w.Write(state.JournalId);
            w.Write(state.NextUsn);
        }

        w.Flush();
        return memory.ToArray();
    }

    private static IReadOnlyList<UsnState> UnpackUsn(byte[] bytes)
    {
        if (bytes.Length < sizeof(int)) return [];

        using var memory = new MemoryStream(bytes);
        using var r = new BinaryReader(memory, Encoding.UTF8);

        var count = r.ReadInt32();
        var states = new List<UsnState>(count);
        for (var i = 0; i < count; i++)
            states.Add(new UsnState(r.ReadString(), r.ReadUInt32(), r.ReadUInt64(), r.ReadInt64()));

        return states;
    }

    private static byte[] PackErrors(IReadOnlyList<ScanError> errors)
    {
        using var memory = new MemoryStream();
        using var w = new BinaryWriter(memory, Encoding.UTF8, leaveOpen: true);

        w.Write(errors.Count);
        foreach (var e in errors)
        {
            w.Write(e.Path);
            w.Write((int)e.Kind);
            w.Write(e.Win32Code);
            w.Write(e.Message);
        }

        w.Flush();
        return memory.ToArray();
    }

    private static IReadOnlyList<ScanError> UnpackErrors(byte[] bytes)
    {
        using var memory = new MemoryStream(bytes);
        using var r = new BinaryReader(memory, Encoding.UTF8);

        var count = r.ReadInt32();
        var errors = new List<ScanError>(count);
        for (var i = 0; i < count; i++)
            errors.Add(new ScanError(r.ReadString(), (ScanErrorKind)r.ReadInt32(), r.ReadInt32(), r.ReadString()));

        return errors;
    }

    private static byte[] Deflate(byte[] raw)
    {
        using var output = new MemoryStream(raw.Length / 3 + 64);
        using (var deflate = new DeflateStream(output, CompressionLevel.Fastest, leaveOpen: true))
            deflate.Write(raw);
        return output.ToArray();
    }

}

internal sealed record SnapshotContents
{
    /// <summary>
    /// What was actually loaded. A part that was not asked for is empty rather than
    /// absent, so a caller that reads it gets nothing instead of an exception - and this
    /// says which of the two happened.
    /// </summary>
    internal SnapshotParts Parts { get; init; } = SnapshotParts.All;

    internal required NodeStore Tree { get; init; }
    internal required IReadOnlyList<VolumeInfo> Volumes { get; init; }
    internal required IReadOnlyList<ScanError> Errors { get; init; }

    /// <summary>
    /// Journal watermarks, or empty for a snapshot written before P9 or on a volume
    /// without a journal (README section 4.5).
    /// </summary>
    internal IReadOnlyList<UsnState> Usn { get; init; } = [];

    internal required ScannerKind Scanner { get; init; }
    internal required ScanFlags Flags { get; init; }
    internal required DateTime StartedUtc { get; init; }
    internal required TimeSpan Duration { get; init; }
}
