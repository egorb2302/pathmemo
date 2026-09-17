using System.Text;
using PathMemo.Snapshots;

namespace PathMemo.Analysis;

internal sealed record CategoryTotal(FileCategory Category, long AllocatedBytes, int FileCount);

internal sealed record ExtensionTotal(string Extension, long AllocatedBytes, int FileCount);

internal sealed record ScanAggregateSet(
    IReadOnlyList<CategoryTotal> Categories,
    IReadOnlyList<ExtensionTotal> Extensions);

/// <summary>
/// Per-scan totals by category and by extension, stored in SQLite so the charts still
/// work after retention has removed the snapshot itself (README section 11).
/// </summary>
/// <remarks>
/// <para>
/// One top-down pass over the tree. Top-down rather than a flat loop because the
/// category of a file depends on the directory that owns it: everything under
/// <c>C:\Windows</c> is system, and a cache full of archives is still a cache.
/// </para>
/// <para>
/// Names are read as UTF-8 spans and transcoded into stack buffers, never into strings:
/// one string per file is 1.2 million allocations on a full drive, which cost 600 ms and
/// ~80 MB of garbage when measured - on top of a scan already budgeted to 400 MB
/// (README section 17.3). A string is allocated only the first time an extension is seen.
/// </para>
/// </remarks>
internal static class ScanAggregates
{
    /// <summary>
    /// Extension rows kept per scan, largest first. A full drive has tens of thousands
    /// of distinct suffixes, nearly all of them one junk file; keeping them all would
    /// grow the database faster than the snapshots it is supposed to outlive.
    /// </summary>
    internal const int MaxExtensionRows = 250;

    /// <summary>Row that carries everything below the <see cref="MaxExtensionRows"/> cut.</summary>
    internal const string RestExtension = "(rest)";

    /// <summary>Row for files with no extension at all.</summary>
    internal const string NoExtension = "(none)";

    /// <summary>
    /// Longest suffix still treated as an extension. Past this it is part of the name
    /// ("report.backup-2026-01-01" has no extension worth counting).
    /// </summary>
    private const int MaxExtensionLength = 12;

    /// <summary>
    /// Enough for every directory name that can claim a category; the longest is
    /// "System Volume Information". A longer name cannot match one, so it needs no buffer.
    /// </summary>
    private const int MaxClaimingNameLength = 32;

    internal static ScanAggregateSet Compute(NodeStore tree)
    {
        var categoryBytes = new long[8];
        var categoryFiles = new int[8];

        var extensions = new Dictionary<string, (long Bytes, int Files)>(4096, StringComparer.Ordinal);
        var bySpan = extensions.GetAlternateLookup<ReadOnlySpan<char>>();

        // Allocated once for the whole pass, not per node.
        Span<char> transcoded = stackalloc char[MaxClaimingNameLength];
        Span<char> lowered = stackalloc char[MaxExtensionLength];

        // (node, inherited category, depth). An explicit stack, because the emission
        // order of the snapshot is an implementation detail of the scanner and this pass
        // must not depend on parents preceding children.
        var stack = new Stack<(int Node, FileCategory? Inherited, int Depth)>();
        foreach (var root in tree.Roots) stack.Push((root, null, 0));

        while (stack.Count > 0)
        {
            var (node, inherited, depth) = stack.Pop();
            var name = tree.NameUtf8(node);

            if (tree.IsDirectory(node))
            {
                var claim = inherited ?? Claim(name, depth, transcoded);
                var children = tree.Children(node);
                for (var i = children.Start.Value; i < children.End.Value; i++)
                    stack.Push((i, claim, depth + 1));
                continue;
            }

            // Unique accounting: a second hard link to a file already counted elsewhere
            // adds a name, not bytes (README section 3.2).
            if ((tree.Flags[node] & NodeFlags.HardlinkAlias) != 0) continue;

            var bytes = tree.Allocated[node];
            var extension = LowerExtension(name, lowered);

            // NTFS metafiles sit at the volume root and have no extension: the 1.7 GB
            // $MFT the MFT scanner reports is system, not "other". Only at depth 1,
            // because a user file may legitimately be named $anything.
            var category = inherited
                ?? (depth == 1 && name.Length > 0 && name[0] == (byte)'$'
                        ? FileCategory.System
                        : FileCategories.ByExtension(extension));

            categoryBytes[(int)category] += bytes;
            categoryFiles[(int)category]++;

            if (extension.IsEmpty)
            {
                extensions.TryGetValue(NoExtension, out var none);
                extensions[NoExtension] = (none.Bytes + bytes, none.Files + 1);
                continue;
            }

            // The alternate lookup allocates the key string only when the extension is
            // new, so a million files cost a few hundred strings in total.
            bySpan.TryGetValue(extension, out var current);
            bySpan[extension] = (current.Bytes + bytes, current.Files + 1);
        }

        var categories = new List<CategoryTotal>(8);
        for (var i = 0; i < categoryBytes.Length; i++)
        {
            if (categoryFiles[i] == 0) continue;
            categories.Add(new CategoryTotal((FileCategory)i, categoryBytes[i], categoryFiles[i]));
        }
        categories.Sort((a, b) => b.AllocatedBytes.CompareTo(a.AllocatedBytes));

        return new ScanAggregateSet(categories, Cap(extensions));
    }

    /// <summary>
    /// Whether this directory claims its whole subtree for a category, by name.
    /// </summary>
    private static FileCategory? Claim(ReadOnlySpan<byte> name, int depth, Span<char> buffer)
    {
        if (name.Length == 0 || name.Length > buffer.Length) return null;

        var written = Encoding.UTF8.GetChars(name, buffer);
        return FileCategories.Inherited(buffer[..written], depth);
    }

    /// <summary>
    /// The lower-cased suffix after the last dot, or empty when there is none.
    /// </summary>
    private static ReadOnlySpan<char> LowerExtension(ReadOnlySpan<byte> name, Span<char> buffer)
    {
        var dot = name.LastIndexOf((byte)'.');

        // A leading dot is a name, not an extension: ".gitignore" has none.
        if (dot <= 0 || dot == name.Length - 1) return default;

        var extension = name[(dot + 1)..];
        if (extension.Length > buffer.Length) return default;

        var written = Encoding.UTF8.GetChars(extension, buffer);

        // In place is safe here: casing a char never changes the length, and
        // Ascii.ToLower allows source and destination to be the same span.
        for (var i = 0; i < written; i++)
        {
            var c = buffer[i];
            if (c is >= 'A' and <= 'Z') buffer[i] = (char)(c + 32);
            else if (c > 127) buffer[i] = char.ToLowerInvariant(c);
        }

        return buffer[..written];
    }

    private static IReadOnlyList<ExtensionTotal> Cap(Dictionary<string, (long Bytes, int Files)> source)
    {
        var all = new List<ExtensionTotal>(source.Count);
        foreach (var (extension, totals) in source)
            all.Add(new ExtensionTotal(extension, totals.Bytes, totals.Files));

        all.Sort((a, b) => b.AllocatedBytes.CompareTo(a.AllocatedBytes));
        if (all.Count <= MaxExtensionRows) return all;

        var kept = all.GetRange(0, MaxExtensionRows);
        long restBytes = 0;
        var restFiles = 0;
        for (var i = MaxExtensionRows; i < all.Count; i++)
        {
            restBytes += all[i].AllocatedBytes;
            restFiles += all[i].FileCount;
        }

        kept.Add(new ExtensionTotal(RestExtension, restBytes, restFiles));
        return kept;
    }
}
