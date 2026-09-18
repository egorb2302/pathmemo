using System.Collections.Concurrent;
using System.Diagnostics;
using PathMemo.Analysis;
using PathMemo.Config;
using PathMemo.Platform;
using PathMemo.Platform.Native;
using PathMemo.Snapshots;

namespace PathMemo.Duplicates;

/// <summary>
/// The five stages of README section 8.1, in order, over one snapshot.
/// </summary>
/// <remarks>
/// <para>
/// The shape is a funnel, and the point of every stage is to make the next one smaller.
/// Stage 0 is free - it reads the snapshot, not the disk - and throws away every file whose
/// size is unique, which on a real machine is most of them. Stage 2 reads 128 KB of each
/// survivor, stage 3 reads the rest, and stage 4 reads the finalists a second time to
/// replace a probability with a fact. A stage that costs more than the one after it saved
/// would be a mistake, which is why the partial hash exists at all.
/// </para>
/// <para>
/// Hard links are collapsed before any content is compared, from the file id read off the
/// handle that the partial hash opens (README section 8.1, stage 1). Three names for one
/// file would otherwise look like two duplicates worth deleting, and deleting them would
/// free nothing whatsoever.
/// </para>
/// <para>
/// Nothing here deletes, and nothing here writes to the disk being searched. The result is
/// a proposal; <c>rm</c> and its guard are what act on it (README section 9).
/// </para>
/// </remarks>
internal static class DuplicateFinder
{
    /// <summary>One candidate as it moves down the funnel.</summary>
    private sealed class Candidate
    {
        internal required int Node { get; init; }
        internal required string Path { get; init; }
        internal required long Bytes { get; init; }
        internal required long Allocated { get; init; }
        internal required DateTime ModifiedUtc { get; init; }
        internal int LinkCount { get; set; } = 1;
        internal FileIdentity Identity { get; set; }
        internal string? Partial { get; set; }
        internal string? Full { get; set; }

        internal DupeFile ToFile() => new()
        {
            Path = Path,
            Bytes = Bytes,
            Allocated = Allocated,
            ModifiedUtc = ModifiedUtc,
            LinkCount = LinkCount,
            Identity = Identity,
        };
    }

    internal static DupeReport Find(
        NodeStore tree, long scanId, DupeQuery? request = null, CancellationToken ct = default)
    {
        var query = request ?? new DupeQuery();
        var stats = new DupeStats();
        var clock = Stopwatch.StartNew();
        var keep = (query.Keep ?? AppConfig.Current.Protect.Keep).Select(PathGlob.Parse).ToList();

        var groups = new List<DupeGroup>();
        var partial = false;
        var declined = false;
        var degree = query.Parallelism > 0 ? query.Parallelism : Degree(tree);

        try
        {
            var buckets = Sizes(tree, query, stats, ct);

            // The one question asked before any of the disk is read (README section 8.5).
            // It is asked here rather than by the caller because only stage 0 knows how
            // much reading there is, and "this will take a while" without a number is a
            // prompt nobody can answer.
            if (query.Confirm is { } ask)
            {
                var bytes = buckets.Sum(b => b.Sum(c => c.Bytes));

                switch (ask(bytes, stats.Candidates))
                {
                    case DupeConsent.Cancel:
                        declined = true;
                        buckets = [];
                        break;

                    case DupeConsent.SkipLarge:
                        buckets = Trim(buckets, 1L << 30, stats);
                        break;
                }
            }

            var survivors = Identify(buckets, query, degree, stats, groups, keep, ct);
            survivors = SamePartialHash(survivors, stats, ct);
            survivors = SameFullHash(survivors, query, degree, stats, ct);

            Emit(survivors, query, stats, groups, keep, ct);
        }
        catch (OperationCanceledException)
        {
            partial = true;
        }

        stats.Elapsed = clock.Elapsed;

        return new DupeReport
        {
            Groups = [.. groups
                .OrderByDescending(g => g.Wasted)
                .ThenByDescending(g => g.Bytes)
                .ThenBy(g => g.Files[0].Path, StringComparer.OrdinalIgnoreCase)],
            ScanId = scanId,
            Algorithm = query.Algorithm,
            MinBytes = query.MinBytes,
            Stats = stats,
            Partial = partial,
            Declined = declined,
        };
    }

    /// <summary>
    /// Stage 0: everything the snapshot alone can decide.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Grouped by <b>logical</b> size, not allocated: two identical files on volumes with
    /// different cluster sizes occupy different numbers of bytes on disk and the same
    /// number of bytes of content. Allocated is what the report promises back, and logical
    /// is what makes two files comparable in the first place (README section 3.1).
    /// </para>
    /// <para>
    /// Paths are built only for sizes that more than one file shares, which is the whole
    /// reason this stage is free: a million <c>GetPath</c> calls would cost more than the
    /// hashing (README section 17.3).
    /// </para>
    /// </remarks>
    private static List<List<Candidate>> Sizes(
        NodeStore tree, DupeQuery query, DupeStats stats, CancellationToken ct)
    {
        var under = query.Under is { } path ? TreeQuery.Find(tree, System.IO.Path.GetFullPath(path)) : NodeStore.NoNode;
        var subtree = under == NodeStore.NoNode ? null : TreeQuery.Subtree(tree, under);

        var byKey = new Dictionary<(long Size, int Volume), List<int>>();

        for (var node = 0; node < tree.Count; node++)
        {
            if ((node & 0xFFFF) == 0) ct.ThrowIfCancellationRequested();

            if (tree.IsDirectory(node)) continue;

            var bytes = tree.Logical[node];
            if (bytes < query.MinBytes || bytes == 0) continue;
            if (query.MaxBytes > 0 && bytes > query.MaxBytes) continue;

            var flags = tree.Flags[node];
            if ((flags & (NodeFlags.Reparse | NodeFlags.Encrypted | NodeFlags.SelfData)) != 0) continue;

            if ((flags & NodeFlags.CloudOnly) != 0)
            {
                // The first of the two barriers of README section 8.4, and the cheap one:
                // the scan already knows this file's bytes are not on this disk.
                if (query.SkipCloudOnly) { stats.CloudSkipped++; continue; }
            }

            if (subtree is not null && !subtree.Contains(node)) continue;

            if (query.Extensions is { Count: > 0 })
            {
                var name = tree.Name(node);
                var dot = name.LastIndexOf('.');
                if (dot < 0) continue;
                if (!query.Extensions.Contains(name[(dot + 1)..].ToLowerInvariant())) continue;
            }

            var key = (bytes, query.CrossVolume ? 0 : tree.VolumeIndex[node]);
            if (!byKey.TryGetValue(key, out var bucket)) byKey[key] = bucket = [];
            bucket.Add(node);
        }

        var buckets = new List<List<Candidate>>();

        foreach (var (key, nodes) in byKey)
        {
            if (nodes.Count < 2) continue;

            var bucket = new List<Candidate>(nodes.Count);
            foreach (var node in nodes)
                bucket.Add(new Candidate
                {
                    Node = node,
                    Path = tree.GetPath(node),
                    Bytes = key.Size,
                    Allocated = tree.Allocated[node],
                    ModifiedUtc = tree.ModifiedUtc(node),
                    LinkCount = tree.LinkCount[node],
                });

            stats.Candidates += bucket.Count;
            buckets.Add(bucket);
        }

        return buckets;
    }

    /// <summary>Drops the files above a ceiling, for the "skip the big ones" answer.</summary>
    private static List<List<Candidate>> Trim(List<List<Candidate>> buckets, long ceiling, DupeStats stats)
    {
        var kept = new List<List<Candidate>>(buckets.Count);

        foreach (var bucket in buckets)
        {
            if (bucket.Count > 0 && bucket[0].Bytes > ceiling)
            {
                stats.Candidates -= bucket.Count;
                continue;
            }

            kept.Add(bucket);
        }

        return kept;
    }

    /// <summary>
    /// Stages 1 and 2 in one pass over the disk: open, learn what the file is, and read
    /// its two ends.
    /// </summary>
    /// <remarks>
    /// One open per candidate, not two. The file id that decides hard-link membership and
    /// the bytes that feed the partial hash come off the same handle, so stage 1 costs a
    /// call rather than a pass. Files that turn out to be several names for one file are
    /// emitted here as their own kind of group and leave exactly one representative behind.
    /// </remarks>
    private static List<List<Candidate>> Identify(
        List<List<Candidate>> buckets, DupeQuery query, int degree, DupeStats stats,
        List<DupeGroup> groups, IReadOnlyList<PathGlob> keep, CancellationToken ct)
    {
        var total = buckets.Sum(b => b.Count);
        var done = 0;

        foreach (var bucket in buckets)
        {
            ct.ThrowIfCancellationRequested();

            Parallel.ForEach(bucket, Options(degree, ct), candidate =>
            {
                var n = Interlocked.Increment(ref done);
                query.Progress?.Report(new DupeProgress("reading", n, total, stats.BytesRead, candidate.Path));

                using var handle = HashPipeline.Open(candidate.Path, out _);
                if (handle is null)
                {
                    Interlocked.Increment(ref stats.Unreadable);
                    return;
                }

                if (HashPipeline.IsElsewhere(handle))
                {
                    Interlocked.Increment(ref stats.CloudSkipped);
                    return;
                }

                Interlocked.Increment(ref stats.Opened);

                HashPipeline.TryIdentify(handle, out var identity, out var links);
                candidate.Identity = identity;
                candidate.LinkCount = links;

                candidate.Partial = HashPipeline.Partial(handle, candidate.Bytes, query, out var read);
                if (candidate.Partial is not null) Interlocked.Increment(ref stats.PartialHashed);

                Interlocked.Add(ref stats.BytesRead, read);
            });
        }

        var next = new List<List<Candidate>>(buckets.Count);

        foreach (var bucket in buckets)
        {
            var open = bucket.Where(c => c.Partial is not null).ToList();
            var representatives = new List<Candidate>(open.Count);

            foreach (var family in open.GroupBy(c => c.Identity))
            {
                var members = family.ToList();

                if (family.Key.IsKnown && members.Count > 1)
                {
                    groups.Add(new DupeGroup
                    {
                        Kind = DupeKind.HardlinkSet,
                        Bytes = members[0].Bytes,
                        Files = Keeper.Mark([.. members.Select(m => m.ToFile())], keep),
                        Hash = members[0].Partial ?? "",
                    });
                }

                // One name stands for the file from here on: comparing two hard links to
                // each other would always say "identical", which is true and useless.
                representatives.Add(members[0]);
            }

            if (representatives.Count > 1) next.Add(representatives);
        }

        return next;
    }

    /// <summary>Drops whatever no longer shares its 128 KB with anything (stage 2).</summary>
    private static List<List<Candidate>> SamePartialHash(
        List<List<Candidate>> buckets, DupeStats stats, CancellationToken ct)
    {
        var next = new List<List<Candidate>>();

        foreach (var bucket in buckets)
        {
            ct.ThrowIfCancellationRequested();

            foreach (var family in bucket.GroupBy(c => c.Partial, StringComparer.Ordinal))
            {
                var members = family.ToList();
                if (members.Count > 1) next.Add(members);
            }
        }

        return next;
    }

    /// <summary>
    /// Stage 3: the whole file, from the cache where the file has not changed since it was
    /// last read (README section 8.3).
    /// </summary>
    private static List<List<Candidate>> SameFullHash(
        List<List<Candidate>> buckets, DupeQuery query, int degree, DupeStats stats, CancellationToken ct)
    {
        var pending = new List<Candidate>();

        foreach (var bucket in buckets)
        {
            foreach (var candidate in bucket)
            {
                ct.ThrowIfCancellationRequested();

                // A file small enough that its "partial" hash was the whole file is already
                // fully hashed; reading it again would be pure cost.
                if (candidate.Bytes <= query.PartialHashBytes * 2L)
                {
                    candidate.Full = candidate.Partial;
                    continue;
                }

                if (query.Cache is { } cache
                    && cache.TryGet(candidate.Identity, candidate.Bytes, candidate.ModifiedUtc, out var stored))
                {
                    candidate.Full = stored;
                    stats.FromCache++;
                    continue;
                }

                pending.Add(candidate);
            }
        }

        var computed = new ConcurrentBag<Candidate>();
        var done = 0;

        Parallel.ForEach(pending, Options(degree, ct), candidate =>
        {
            var n = Interlocked.Increment(ref done);
            query.Progress?.Report(new DupeProgress("hashing", n, pending.Count, stats.BytesRead, candidate.Path));

            using var handle = HashPipeline.Open(candidate.Path, out _);
            if (handle is null)
            {
                Interlocked.Increment(ref stats.Unreadable);
                return;
            }

            candidate.Full = HashPipeline.Full(handle, candidate.Bytes, query, out var read);
            if (candidate.Full is null) return;

            Interlocked.Increment(ref stats.FullHashed);
            Interlocked.Add(ref stats.BytesRead, read);
            computed.Add(candidate);
        });

        // The cache is written from one thread, after the reading is over: SQLite in this
        // process is one connection, and a lock per file would cost more than it saves.
        if (query.Cache is { } store)
        {
            foreach (var candidate in computed)
                store.Put(candidate.Identity, candidate.Bytes, candidate.ModifiedUtc, candidate.Full!);

            store.Flush();
        }

        var next = new List<List<Candidate>>();

        foreach (var bucket in buckets)
        {
            ct.ThrowIfCancellationRequested();

            foreach (var family in bucket.Where(c => c.Full is not null)
                                         .GroupBy(c => c.Full, StringComparer.Ordinal))
            {
                var members = family.ToList();
                if (members.Count > 1) next.Add(members);
            }
        }

        return next;
    }

    /// <summary>Stage 4, then the groups the report is made of.</summary>
    private static void Emit(
        List<List<Candidate>> buckets, DupeQuery query, DupeStats stats,
        List<DupeGroup> groups, IReadOnlyList<PathGlob> keep, CancellationToken ct)
    {
        var done = 0;

        foreach (var bucket in buckets)
        {
            ct.ThrowIfCancellationRequested();

            query.Progress?.Report(new DupeProgress(
                "verifying", ++done, buckets.Count, stats.BytesRead, bucket[0].Path));

            var files = bucket.Select(c => c.ToFile()).ToList();

            if (!query.ByteForByte)
            {
                Add(groups, files, bucket[0], keep);
                continue;
            }

            foreach (var proven in ByteComparer.Partition(files, query, stats, ct))
            {
                if (proven.Count < 2) continue;
                Add(groups, proven, bucket[0], keep);
            }
        }
    }

    private static void Add(
        List<DupeGroup> groups, IReadOnlyList<DupeFile> files, Candidate sample, IReadOnlyList<PathGlob> keep) =>
        groups.Add(new DupeGroup
        {
            Kind = DupeKind.Duplicate,
            Bytes = files[0].Bytes,
            Files = Keeper.Mark(files, keep),
            Hash = sample.Full ?? sample.Partial ?? "",
        });

    /// <summary>
    /// The last check before anything is deleted (README section 8.4).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Size, modification time and the full hash of the survivor and of every file about to
    /// go are recomputed from the disk as it is now, not as the search found it. A single
    /// mismatch cancels the <b>whole</b> operation rather than skipping the one file: if
    /// the disk moved under the search, every other conclusion it reached is suspect too.
    /// </para>
    /// <para>
    /// The modification times are compared with two seconds of slack. The snapshot stores
    /// seconds and NTFS stores hundred-nanosecond ticks, so an exact comparison would
    /// refuse every file that had merely been rounded - which is how the equivalent check
    /// in the deletion module came to be wrong for a whole phase (README section 9.9).
    /// </para>
    /// </remarks>
    internal static bool Reverify(
        IEnumerable<DupeGroup> groups, IReadOnlySet<string> victims, DupeQuery query,
        IProgress<string>? progress, out string complaint)
    {
        foreach (var group in groups)
        {
            var going = group.Files.Where(f => victims.Contains(f.Path)).ToList();
            if (going.Count == 0) continue;

            if (group.Kind == DupeKind.HardlinkSet)
            {
                complaint = $"{going[0].Path} is one name of a hard-linked file; deleting it frees nothing";
                return false;
            }

            if (group.Files.FirstOrDefault(f => !victims.Contains(f.Path)) is not { } keeper)
            {
                complaint = $"every copy of {group.Files[0].Path} is marked - a group must keep one";
                return false;
            }

            progress?.Report(keeper.Path);

            if (!Measure(keeper.Path, query, out var survivor))
            {
                complaint = $"{keeper.Path} cannot be read now, so nothing may be deleted against it";
                return false;
            }

            foreach (var victim in going)
            {
                progress?.Report(victim.Path);

                if (!Measure(victim.Path, query, out var now))
                {
                    complaint = $"{victim.Path} cannot be read now";
                    return false;
                }

                if (now.Bytes != survivor.Bytes)
                {
                    complaint = $"{victim.Path} is {now.Bytes} bytes and {keeper.Path} is {survivor.Bytes}; "
                              + "run the search again";
                    return false;
                }

                if (now.Links > 1)
                {
                    complaint = $"{victim.Path} now has {now.Links} names; deleting one frees nothing";
                    return false;
                }

                if (Math.Abs((now.ModifiedUtc - victim.ModifiedUtc).TotalSeconds) > 2)
                {
                    complaint = $"{victim.Path} was modified since the search; run it again";
                    return false;
                }

                if (!string.Equals(now.Hash, survivor.Hash, StringComparison.Ordinal))
                {
                    complaint = $"{victim.Path} is no longer identical to {keeper.Path}";
                    return false;
                }
            }
        }

        complaint = "";
        return true;
    }

    private readonly record struct Measurement(long Bytes, DateTime ModifiedUtc, int Links, string Hash);

    private static bool Measure(string path, DupeQuery query, out Measurement measurement)
    {
        measurement = default;

        using var handle = HashPipeline.Open(path, out _);
        if (handle is null) return false;
        if (HashPipeline.IsElsewhere(handle)) return false;
        if (!FileApi.TryReadStandardInfo(handle, out var standard)) return false;
        if (!FileApi.TryReadBasicInfo(handle, out var basic)) return false;

        var hash = HashPipeline.Full(handle, standard.EndOfFile, query, out _);
        if (hash is null) return false;

        measurement = new Measurement(
            standard.EndOfFile, DateTime.FromFileTimeUtc(basic.LastWriteTime),
            (int)standard.NumberOfLinks, hash);

        return true;
    }

    /// <summary>
    /// How many files to read at once.
    /// </summary>
    /// <remarks>
    /// One at a time on rotational media: several threads seeking across a platter run
    /// two to three times slower than one that reads in order, which is the same reason
    /// the walk scanner asks (README section 4.4). An SSD answers several queues happily,
    /// but not unboundedly - past four the queue is full and the extra threads only add
    /// context switches, because this workload is pure sequential read.
    /// </remarks>
    private static ParallelOptions Options(int degree, CancellationToken ct) => new()
    {
        CancellationToken = ct,
        MaxDegreeOfParallelism = Math.Max(1, degree),
    };

    /// <summary>The slowest medium among the snapshot's volumes decides for all of them.</summary>
    private static int Degree(NodeStore tree)
    {
        foreach (var root in tree.Roots)
        {
            var name = tree.Name(root);
            if (name.Length < 2 || name[1] != ':') continue;

            if (StorageMedium.Detect(name[..2]) == MediumKind.Rotational) return 1;
        }

        return Math.Min(4, Environment.ProcessorCount);
    }
}
