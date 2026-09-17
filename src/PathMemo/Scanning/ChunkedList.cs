namespace PathMemo.Scanning;

/// <summary>
/// Append-only list whose existing elements never move, so one thread can write
/// <c>this[i]</c> while another appends.
/// </summary>
/// <remarks>
/// A plain <c>List&lt;T&gt;</c> cannot be used for this: its growth reallocates the backing
/// array, and an indexed write racing with that reallocation silently lands in the
/// abandoned copy. Chunks are allocated once and never resized, and appends happen under
/// a lock while indexed reads and writes need none.
/// </remarks>
internal sealed class ChunkedList<T>
{
    private const int ChunkShift = 12;               // 4096 elements per chunk
    private const int ChunkSize = 1 << ChunkShift;
    private const int ChunkMask = ChunkSize - 1;

    private readonly Lock _gate = new();
    private T[]?[] _chunks = new T[]?[16];
    private int _count;

    internal int Count => Volatile.Read(ref _count);

    internal T this[int index]
    {
        get => _chunks[index >> ChunkShift]![index & ChunkMask];
        set => _chunks[index >> ChunkShift]![index & ChunkMask] = value;
    }

    /// <summary>Reserves the next index. The caller may write to it immediately.</summary>
    internal int Add(T value)
    {
        lock (_gate)
        {
            var index = _count;
            var chunk = index >> ChunkShift;

            if (chunk >= _chunks.Length)
            {
                var grown = new T[]?[_chunks.Length * 2];
                Array.Copy(_chunks, grown, _chunks.Length);
                Volatile.Write(ref _chunks, grown);
            }

            _chunks[chunk] ??= new T[ChunkSize];
            _chunks[chunk]![index & ChunkMask] = value;

            // Published last: a reader that sees the new count is guaranteed to see the
            // chunk it lives in.
            Volatile.Write(ref _count, index + 1);
            return index;
        }
    }
}
