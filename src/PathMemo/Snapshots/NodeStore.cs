using System.Text;

namespace PathMemo.Snapshots;

/// <summary>
/// The file tree of one scan, as Struct-of-Arrays (README section 5.3).
/// </summary>
/// <remarks>
/// <para>
/// There is deliberately no per-file class and no per-file path string: a million
/// <c>FileEntry</c> objects with string paths would cost 400-800 MB, against a 250 MB
/// budget (README section 17.3). 47 bytes per node keeps a million files at ~47 MB.
/// </para>
/// <para>
/// Children of a node occupy a contiguous index range, so descending into a directory
/// with 200k entries is a slice, not a search.
/// </para>
/// </remarks>
internal sealed class NodeStore
{
    internal const int NoNode = -1;

    internal required int[] Parent { get; init; }
    internal required int[] NameOffset { get; init; }
    internal required int[] FirstChild { get; init; }
    internal required int[] ChildCount { get; init; }

    /// <summary>On-disk bytes. For a directory, the unique-allocated subtree total.</summary>
    internal required long[] Allocated { get; init; }

    /// <summary>Data-stream bytes. For a directory, the subtree total.</summary>
    internal required long[] Logical { get; init; }

    /// <summary>For a directory, files in the whole subtree. For a file, always 0.</summary>
    internal required int[] FileCount { get; init; }

    /// <summary>Seconds since 2000-01-01 UTC (covers to 2136 in 4 bytes).</summary>
    internal required uint[] Mtime { get; init; }

    internal required uint[] Attributes { get; init; }
    internal required NodeFlags[] Flags { get; init; }

    /// <summary>Hard link count; 255 means "255 or more".</summary>
    internal required byte[] LinkCount { get; init; }

    internal required byte[] VolumeIndex { get; init; }

    /// <summary>UTF-8 segment names, deduplicated. Indexed by <see cref="NameOffset"/>.</summary>
    internal required byte[] NameBlob { get; init; }

    /// <summary>Node index of each volume's root, parallel to the snapshot's volume list.</summary>
    internal required int[] Roots { get; init; }

    internal int Count => Parent.Length;

    internal bool IsDirectory(int node) => (Flags[node] & NodeFlags.Directory) != 0;

    internal ReadOnlySpan<byte> NameUtf8(int node) => NameBlobBuilder.Read(NameBlob, NameOffset[node]);

    internal string Name(int node) => Encoding.UTF8.GetString(NameUtf8(node));

    /// <summary>Contiguous index range of this node's direct children.</summary>
    internal Range Children(int node) =>
        ChildCount[node] == 0 ? default : FirstChild[node]..(FirstChild[node] + ChildCount[node]);

    internal DateTime ModifiedUtc(int node) => SnapshotTime.ToDateTime(Mtime[node]);

    /// <summary>
    /// Rebuilds the full path by walking to the volume root. Microseconds; paths are
    /// materialised only when displayed, copied or acted on.
    /// </summary>
    internal string GetPath(int node)
    {
        var depth = 0;
        for (var i = node; i != NoNode; i = Parent[i]) depth++;

        var segments = new int[depth];
        var n = 0;
        for (var i = node; i != NoNode; i = Parent[i]) segments[n++] = i;

        var sb = new StringBuilder(128);
        for (var i = n - 1; i >= 0; i--)
        {
            var name = Name(segments[i]);
            sb.Append(name);

            // A volume root already carries its separator ("C:\"); anything else needs one
            // between it and the next segment.
            if (i > 0 && !name.EndsWith(Path.DirectorySeparatorChar)) sb.Append(Path.DirectorySeparatorChar);
        }
        return sb.ToString();
    }

    /// <summary>Depth from the volume root, where the root itself is 0.</summary>
    internal int Depth(int node)
    {
        var d = 0;
        for (var i = Parent[node]; i != NoNode; i = Parent[i]) d++;
        return d;
    }
}

internal static class SnapshotTime
{
    /// <summary>Snapshot timestamps are seconds from this epoch, stored in a uint.</summary>
    internal static readonly DateTime Epoch = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    internal static uint FromDateTime(DateTime utc)
    {
        var seconds = (utc - Epoch).TotalSeconds;
        return seconds <= 0 ? 0 : seconds >= uint.MaxValue ? uint.MaxValue : (uint)seconds;
    }

    internal static DateTime ToDateTime(uint seconds) => Epoch.AddSeconds(seconds);
}
