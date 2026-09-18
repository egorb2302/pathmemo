using System.Buffers;

namespace PathMemo.Duplicates;

/// <summary>
/// Stage 4: proving that files which agree on a hash agree on their bytes
/// (README section 8.1).
/// </summary>
/// <remarks>
/// <para>
/// This is what turns a probabilistic answer into a certain one. The cost is real - every
/// finalist is read in full a second time - but by stage 4 only the groups that already
/// survived a partial and a full hash are left, which on a real disk is a few hundred
/// megabytes, not the two hundred gigabytes the earlier stages moved. Paying it is the
/// difference between "these are almost certainly the same" and "these are the same",
/// and the operation on the other side of the answer is deletion.
/// </para>
/// <para>
/// The comparison is against one representative rather than every pair: equality is
/// transitive, so a file that matches the representative matches everything else that
/// does. A file that does not match starts a class of its own, and the same rule applies
/// inside it. N files cost N-1 comparisons in the ordinary case where they really are all
/// the same, instead of the N(N-1)/2 the word "pairwise" would suggest.
/// </para>
/// </remarks>
internal static class ByteComparer
{
    /// <summary>
    /// Splits files that share a hash into the classes that share their contents. Returns
    /// one list per class, largest first; classes of one are the callers' to discard.
    /// </summary>
    internal static List<List<DupeFile>> Partition(
        IReadOnlyList<DupeFile> files, DupeQuery query, DupeStats stats, CancellationToken ct)
    {
        var classes = new List<List<DupeFile>>();
        var remaining = new List<DupeFile>(files);

        while (remaining.Count > 0)
        {
            ct.ThrowIfCancellationRequested();

            var representative = remaining[0];
            var same = new List<DupeFile> { representative };
            var different = new List<DupeFile>();

            for (var i = 1; i < remaining.Count; i++)
            {
                ct.ThrowIfCancellationRequested();

                var other = remaining[i];
                stats.Compared++;

                if (Same(representative.Path, other.Path, representative.Bytes, query.BufferBytes, out var read))
                {
                    same.Add(other);
                }
                else
                {
                    // Unreadable counts as "not proven equal". The alternative is deleting
                    // a file because the copy that was supposed to replace it could not be
                    // opened, which is the one outcome this module exists to prevent.
                    different.Add(other);
                }

                stats.BytesRead += read;
            }

            classes.Add(same);
            remaining = different;
        }

        if (classes.Count > 1) stats.Impostors += files.Count - classes.Max(c => c.Count);

        classes.Sort((a, b) => b.Count.CompareTo(a.Count));
        return classes;
    }

    /// <summary>
    /// Whether two files hold the same bytes. False when either cannot be read: unknown
    /// and equal are not the same answer.
    /// </summary>
    internal static bool Same(string left, string right, long size, int bufferBytes, out long read)
    {
        read = 0;

        using var a = HashPipeline.Open(left, out _);
        if (a is null) return false;

        using var b = HashPipeline.Open(right, out _);
        if (b is null) return false;

        if (HashPipeline.IsElsewhere(a) || HashPipeline.IsElsewhere(b)) return false;

        var block = Math.Max(4096, bufferBytes);
        var first = ArrayPool<byte>.Shared.Rent(block);
        var second = ArrayPool<byte>.Shared.Rent(block);

        try
        {
            long offset = 0;

            while (offset < size)
            {
                var wanted = (int)Math.Min(block, size - offset);

                int gotA, gotB;
                try
                {
                    gotA = RandomAccess.Read(a, first.AsSpan(0, wanted), offset);
                    gotB = RandomAccess.Read(b, second.AsSpan(0, wanted), offset);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return false;
                }

                if (gotA == 0 || gotA != gotB) return false;

                if (!first.AsSpan(0, gotA).SequenceEqual(second.AsSpan(0, gotA))) return false;

                offset += gotA;
                read += gotA * 2L;
            }

            return true;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(first);
            ArrayPool<byte>.Shared.Return(second);
        }
    }
}
