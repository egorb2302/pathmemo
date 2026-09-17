using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;

namespace PathMemo.Snapshots;

/// <summary>
/// Append-only UTF-8 blob of path <em>segments</em> (never full paths), with exact-byte
/// deduplication. "Microsoft", "bin" and "node_modules" occur tens of thousands of times
/// on a real disk, so interning is what keeps a million-node snapshot around 15 MB of
/// names instead of 150 MB (README section 5.3).
/// </summary>
/// <remarks>
/// Layout at each returned offset: <c>ushort byteLength</c> followed by that many UTF-8 bytes.
/// Not thread-safe: the snapshot is assembled on a single thread after the parallel scan.
/// </remarks>
internal sealed class NameBlobBuilder
{
    private const int MaxSegmentBytes = ushort.MaxValue;

    private byte[] _blob;
    private int _length;

    // Open-addressing intern table. Keys are offsets into _blob; 0 means "empty slot",
    // which is safe because offset 0 is reserved by the sentinel written in the constructor.
    private int[] _slots;
    private int _slotMask;
    private int _count;

    internal NameBlobBuilder(int expectedSegments = 1 << 16)
    {
        _blob = new byte[Math.Max(1 << 16, expectedSegments * 8)];

        var capacity = 1;
        while (capacity < expectedSegments * 2) capacity <<= 1;
        _slots = new int[capacity];
        _slotMask = capacity - 1;

        // Reserve offset 0 so it can double as the "empty slot" marker.
        WriteRaw(""u8);
    }

    internal int Length => _length;
    internal int SegmentCount => _count;

    /// <summary>
    /// Returns the offset of <paramref name="name"/> in the blob, appending it if new.
    /// </summary>
    internal int Intern(ReadOnlySpan<char> name)
    {
        // Path segments are at most 255 chars on NTFS; 4 bytes per char covers any encoding.
        Span<byte> scratch = stackalloc byte[1024];
        var encoded = Encoding.UTF8.GetByteCount(name) <= scratch.Length
            ? scratch[..Encoding.UTF8.GetBytes(name, scratch)]
            : Encoding.UTF8.GetBytes(name.ToString());

        return Intern(encoded);
    }

    internal int Intern(ReadOnlySpan<byte> utf8)
    {
        if (utf8.Length > MaxSegmentBytes)
            utf8 = utf8[..MaxSegmentBytes];

        var hash = Hash(utf8);
        var slot = hash & _slotMask;

        while (true)
        {
            var existing = _slots[slot];
            if (existing == 0) break;
            if (Read(_blob, existing).SequenceEqual(utf8)) return existing;
            slot = (slot + 1) & _slotMask;
        }

        var offset = WriteRaw(utf8);
        _slots[slot] = offset;
        _count++;

        // Keep the load factor under 0.6; above that, linear probing degrades sharply.
        if (_count * 10 > _slots.Length * 6) Rehash();

        return offset;
    }

    internal byte[] ToBlob() => _blob.AsSpan(0, _length).ToArray();

    /// <summary>The bytes of a previously interned segment.</summary>
    internal ReadOnlySpan<byte> Read(int offset) => Read(_blob, offset);

    private int WriteRaw(ReadOnlySpan<byte> utf8)
    {
        var needed = _length + 2 + utf8.Length;
        if (needed > _blob.Length)
        {
            var size = _blob.Length;
            while (size < needed) size *= 2;
            Array.Resize(ref _blob, size);
        }

        var offset = _length;
        _blob[offset] = (byte)(utf8.Length & 0xFF);
        _blob[offset + 1] = (byte)(utf8.Length >> 8);
        utf8.CopyTo(_blob.AsSpan(offset + 2));
        _length = needed;
        return offset;
    }

    private void Rehash()
    {
        var old = _slots;
        _slots = new int[old.Length * 2];
        _slotMask = _slots.Length - 1;

        foreach (var offset in old)
        {
            if (offset == 0) continue;
            var slot = Hash(Read(_blob, offset)) & _slotMask;
            while (_slots[slot] != 0) slot = (slot + 1) & _slotMask;
            _slots[slot] = offset;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Hash(ReadOnlySpan<byte> data)
    {
        // FNV-1a: cheap, good enough for short segment names, no allocation.
        uint h = 2166136261;
        foreach (var b in data)
        {
            h ^= b;
            h *= 16777619;
        }
        return (int)(h & 0x7FFFFFFF);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ReadOnlySpan<byte> Read(byte[] blob, int offset)
    {
        Debug.Assert(offset >= 0 && offset + 2 <= blob.Length);
        var length = blob[offset] | (blob[offset + 1] << 8);
        return blob.AsSpan(offset + 2, length);
    }
}
