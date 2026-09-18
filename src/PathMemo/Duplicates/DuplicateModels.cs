using PathMemo.Config;

namespace PathMemo.Duplicates;

/// <summary>Which hash the run used (README section 8.2).</summary>
internal enum HashKind
{
    /// <summary>XxHash128: the default, because the disk is the bottleneck, not the hash.</summary>
    Xxh128,

    /// <summary>SHA-256, for comparing results against another tool. Never the default.</summary>
    Sha256,
}

internal static class HashKinds
{
    internal static string Name(HashKind kind) => kind == HashKind.Sha256 ? "sha256" : "xxh128";

    internal static bool TryParse(string text, out HashKind kind)
    {
        switch (text.Trim().ToLowerInvariant())
        {
            case "xxh128" or "xxhash128" or "xx": kind = HashKind.Xxh128; return true;
            case "sha256" or "sha-256": kind = HashKind.Sha256; return true;
            default: kind = HashKind.Xxh128; return false;
        }
    }
}

/// <summary>What a group of identical files is: real copies, or one file under several names.</summary>
internal enum DupeKind
{
    /// <summary>Distinct files with the same contents. Deleting the extras frees space.</summary>
    Duplicate,

    /// <summary>
    /// One physical file reached through several hard links. Deleting a name frees
    /// nothing at all, which is why it is a separate kind and not a duplicate with an
    /// asterisk (README sections 3.2, 8.1).
    /// </summary>
    HardlinkSet,
}

/// <summary>
/// The NTFS identity of a file: the pair that says "this is the same file", whatever
/// name it was reached by (README section 8.1, stage 1).
/// </summary>
internal readonly record struct FileIdentity(ulong Volume, ulong Low, ulong High)
{
    internal bool IsKnown => Volume != 0 || Low != 0 || High != 0;
}

/// <summary>One file in a group.</summary>
internal sealed record DupeFile
{
    internal required string Path { get; init; }

    /// <summary>Content length: the number two identical files necessarily share.</summary>
    internal required long Bytes { get; init; }

    /// <summary>On-disk bytes, which is what deleting this name would actually free.</summary>
    internal required long Allocated { get; init; }

    internal DateTime ModifiedUtc { get; init; }
    internal int LinkCount { get; init; } = 1;
    internal FileIdentity Identity { get; init; }

    /// <summary>True for the file the group keeps. Exactly one per group, always.</summary>
    internal bool Keeper { get; init; }

    /// <summary>
    /// Matched by <c>protect.keep</c>. Such a file is never offered for deletion, whether
    /// or not it also won the keeper slot (README sections 8.1, 8.4).
    /// </summary>
    internal bool Protected { get; init; }

    /// <summary>Why this one was chosen to stay, shown rather than assumed (README section 8.4).</summary>
    internal string? Why { get; init; }
}

/// <summary>Files that are byte-for-byte the same thing.</summary>
internal sealed record DupeGroup
{
    internal required DupeKind Kind { get; init; }

    /// <summary>The content length one copy has.</summary>
    internal required long Bytes { get; init; }

    internal required IReadOnlyList<DupeFile> Files { get; init; }

    internal string Hash { get; init; } = "";

    internal int Count => Files.Count;

    /// <summary>
    /// What deleting everything the group offers would free. Zero for a hard-link set: the
    /// data is stored once and the names all point at it.
    /// </summary>
    /// <remarks>
    /// Settable so a run read back from the database reports the total it measured, rather
    /// than recomputing it from per-file allocated sizes the cache does not store
    /// (README section 11).
    /// </remarks>
    internal long Wasted
    {
        get => _wasted ?? (Kind == DupeKind.HardlinkSet ? 0 : Victims.Sum(f => f.Allocated));
        init => _wasted = value;
    }

    private readonly long? _wasted;

    internal DupeFile Keeper => Files.FirstOrDefault(f => f.Keeper) ?? Files[0];

    /// <summary>
    /// The copies this group would give up.
    /// </summary>
    /// <remarks>
    /// Not the keeper, not a path the keep list claims, and never a file that has more than
    /// one name. Deleting one name of a hard-linked file frees nothing at all, so counting
    /// it here would put bytes in the total that no deletion can produce - the same mistake
    /// in the same place as a hard-link set, one level further down (README section 3.2).
    /// </remarks>
    internal IEnumerable<DupeFile> Victims =>
        Kind == DupeKind.HardlinkSet ? [] : Files.Where(f => !f.Keeper && !f.Protected && f.LinkCount <= 1);
}

/// <summary>What the run cost and what it refused to touch, for the footer of the report.</summary>
internal sealed class DupeStats
{
    /// <summary>Files that survived the snapshot-side filter of stage 0.</summary>
    internal int Candidates;

    internal int Opened;
    internal int PartialHashed;
    internal int FullHashed;
    internal int FromCache;
    internal long BytesRead;

    /// <summary>Cloud placeholders left alone; reading one would download it (threat T10).</summary>
    internal int CloudSkipped;

    internal int Unreadable;

    /// <summary>Files stage 4 read in full to prove equality rather than assume it.</summary>
    internal int Compared;

    /// <summary>Files that agreed on a hash and disagreed on their bytes. Expected: zero.</summary>
    internal int Impostors;

    internal TimeSpan Elapsed;
}

/// <summary>
/// The answer to the notice of README section 8.5, given before a byte is read.
/// </summary>
internal enum DupeConsent
{
    Continue,

    /// <summary>Leave the files over 1 GB alone: the ones that cost the most to read.</summary>
    SkipLarge,

    Cancel,
}

/// <summary>The line the console shows while a run is reading the disk.</summary>
internal readonly record struct DupeProgress(string Stage, int Done, int Total, long BytesRead, string Path);

/// <summary>What to look for (README sections 8.1, 12).</summary>
internal sealed record DupeQuery
{
    internal long MinBytes { get; init; } = 1L << 20;

    /// <summary>A ceiling, for the "skip files over 1 GB" answer to the antivirus notice.</summary>
    internal long MaxBytes { get; init; }

    internal string? Under { get; init; }
    internal IReadOnlySet<string>? Extensions { get; init; }
    internal HashKind Algorithm { get; init; } = HashKind.Xxh128;

    /// <summary>Photos backed up to D: with the originals on C: are the point (README section 8.4).</summary>
    internal bool CrossVolume { get; init; } = true;

    internal bool SkipCloudOnly { get; init; } = true;
    internal bool ByteForByte { get; init; } = true;
    internal int PartialHashBytes { get; init; } = 64 << 10;
    internal int BufferBytes { get; init; } = 1 << 20;

    /// <summary>Files read at once. Zero means "ask the medium" (README section 4.4).</summary>
    internal int Parallelism { get; init; }

    /// <summary>
    /// The keep patterns. Null means the configured ones; passing them explicitly is what
    /// lets a test assert the precedence without a <c>config.json</c> on disk.
    /// </summary>
    internal IReadOnlyList<string>? Keep { get; init; }

    /// <summary>Where full hashes are remembered between runs. Null runs without one.</summary>
    internal HashCache? Cache { get; init; }

    /// <summary>
    /// Asked once, with the number of bytes the run is about to read, before it reads any
    /// of them (README section 8.5). Null means "do not ask".
    /// </summary>
    internal Func<long, int, DupeConsent>? Confirm { get; init; }

    internal IProgress<DupeProgress>? Progress { get; init; }
}

/// <summary>What one duplicate search found (README section 8.1).</summary>
internal sealed record DupeReport
{
    internal required IReadOnlyList<DupeGroup> Groups { get; init; }
    internal required long ScanId { get; init; }
    internal DateTime RanAtUtc { get; init; } = DateTime.UtcNow;
    internal HashKind Algorithm { get; init; }
    internal long MinBytes { get; init; }
    internal DupeStats Stats { get; init; } = new();

    /// <summary>True when the run was cancelled: the groups are real, the totals are a floor.</summary>
    internal bool Partial { get; init; }

    /// <summary>True when the user answered the read notice with "no" (README section 8.5).</summary>
    internal bool Declined { get; init; }

    /// <summary>
    /// True when this came out of the database rather than off the disk. A run that read
    /// nothing because nothing shared a size is not the same thing, and saying so would
    /// send the user looking for a search they never made.
    /// </summary>
    internal bool Stored { get; init; }

    internal IEnumerable<DupeGroup> Duplicates => Groups.Where(g => g.Kind == DupeKind.Duplicate);
    internal IEnumerable<DupeGroup> Hardlinks => Groups.Where(g => g.Kind == DupeKind.HardlinkSet);

    internal long Wasted => Groups.Sum(g => g.Wasted);
    internal bool IsEmpty => Groups.Count == 0;
}

/// <summary>
/// Which file of a group stays, and the rule that nothing may take the last one away
/// (README section 8.4).
/// </summary>
/// <remarks>
/// <para>
/// The choice is made here rather than in a screen or a command, so the CLI and the TUI
/// suggest the same file and the invariant is enforced once. "Oldest wins" alone was
/// rejected: the oldest copy is usually the one that was downloaded first and left in
/// <c>Downloads\tmp</c>, and the one the user has filed away is newer.
/// </para>
/// <para>
/// Nothing here deletes anything. It decides what a deletion would be allowed to propose,
/// which is the part that has to be right before a single handle is opened.
/// </para>
/// </remarks>
internal static class Keeper
{
    /// <summary>Directory names that mean "this copy is the throwaway one".</summary>
    private static readonly string[] Transient =
        ["temp", "tmp", "downloads", "cache", "caches", "$recycle.bin", "quarantine"];

    /// <summary>Marks exactly one file of the list as the keeper, and says why.</summary>
    internal static IReadOnlyList<DupeFile> Mark(IReadOnlyList<DupeFile> files, IReadOnlyList<PathGlob> keep)
    {
        if (files.Count == 0) return files;

        var best = 0;
        for (var i = 1; i < files.Count; i++)
            if (Better(files[i], files[best], keep)) best = i;

        var why = Reason(files[best], keep);

        var marked = new List<DupeFile>(files.Count);
        for (var i = 0; i < files.Count; i++)
            marked.Add(files[i] with
            {
                Keeper = i == best,
                Protected = Kept(files[i], keep),
                Why = i == best ? why : null,
            });

        return marked;
    }

    /// <summary>
    /// Whether the selection is legal: every group keeps at least one file, and nothing
    /// the keep list claims is marked (README section 8.4). The message names the group
    /// that broke it, because "some group" is not something a user can act on.
    /// </summary>
    internal static bool Survives(
        IEnumerable<DupeGroup> groups, IReadOnlySet<string> marked, out string complaint)
    {
        foreach (var group in groups)
        {
            if (group.Files.FirstOrDefault(f => f.Protected && marked.Contains(f.Path)) is { } kept)
            {
                complaint = $"{kept.Path} is on the keep list and cannot be deleted";
                return false;
            }

            if (group.Files.Any(f => !marked.Contains(f.Path))) continue;

            complaint = $"every copy of {group.Files[0].Path} is marked - a group must keep one";
            return false;
        }

        complaint = "";
        return true;
    }

    private static bool Better(DupeFile candidate, DupeFile incumbent, IReadOnlyList<PathGlob> keep)
    {
        // 1. A keep match always wins: the user has already said this path stays.
        var (a, b) = (Kept(candidate, keep), Kept(incumbent, keep));
        if (a != b) return a;

        // 2. A file with several names, because deleting one of them frees nothing. Keeping
        //    it is free, and it makes the other copies - which do free their bytes - the
        //    ones on offer.
        var (la, lb) = (candidate.LinkCount > 1, incumbent.LinkCount > 1);
        if (la != lb) return la;

        // 3. Not in Temp, Downloads or a cache.
        var (ta, tb) = (InTransient(candidate.Path), InTransient(incumbent.Path));
        if (ta != tb) return !ta;

        // 4. The shallower path: the filed copy, not the one three levels into a project.
        var (da, db) = (Depth(candidate.Path), Depth(incumbent.Path));
        if (da != db) return da < db;

        // 5. Only now the date, oldest first - and never on its own.
        if (candidate.ModifiedUtc != incumbent.ModifiedUtc) return candidate.ModifiedUtc < incumbent.ModifiedUtc;

        return string.Compare(candidate.Path, incumbent.Path, StringComparison.OrdinalIgnoreCase) < 0;
    }

    private static string Reason(DupeFile file, IReadOnlyList<PathGlob> keep)
    {
        if (Kept(file, keep)) return "protect.keep";
        if (file.LinkCount > 1) return $"{file.LinkCount} names for this file";
        if (!InTransient(file.Path)) return "outside Temp and Downloads";
        return "shallowest path";
    }

    private static bool Kept(DupeFile file, IReadOnlyList<PathGlob> keep) =>
        keep.Any(k => k.Matches(file.Path));

    internal static bool InTransient(string path)
    {
        foreach (var segment in path.Split('\\', StringSplitOptions.RemoveEmptyEntries))
            foreach (var name in Transient)
                if (segment.Equals(name, StringComparison.OrdinalIgnoreCase)) return true;

        return false;
    }

    private static int Depth(string path)
    {
        var depth = 0;
        foreach (var c in path)
            if (c == '\\') depth++;

        return depth;
    }
}
