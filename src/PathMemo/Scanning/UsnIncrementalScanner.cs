using System.Diagnostics;
using PathMemo.Platform;
using PathMemo.Snapshots;

namespace PathMemo.Scanning;

/// <summary>
/// Rescans by asking the change journal what moved, instead of walking the disk
/// (README section 4.5).
/// </summary>
/// <remarks>
/// <para>
/// This is what makes history worth having. A scheduled scan that costs nine seconds and a
/// gigabyte of reads every hour is a scan nobody schedules; one that costs a fraction of a
/// second is one that can run on a timer, which is the only way "what grew this week" ever
/// gets answered (README section 10.1).
/// </para>
/// <para>
/// Three things have to line up before it may run: the volume must be the same one
/// (serial), the journal must be the same journal (id), and the saved watermark must still
/// be inside it. Any doubt at all falls back to a full scan with the reason printed - a
/// rescan that silently reports a stale tree would be worse than a slow one.
/// </para>
/// </remarks>
internal sealed class UsnIncrementalScanner(SnapshotContents baseline) : IScanner
{
    private readonly SnapshotContents _base = baseline;

    public ScannerKind Kind => ScannerKind.Incremental;

    public (bool Can, string Reason) CanScan(VolumeInfo volume)
    {
        if (!volume.IsNtfs) return (false, $"{volume.FileSystem} has no change journal");
        if (!Elevation.IsElevated) return (false, "reading the change journal requires administrator rights");

        var state = Find(volume);
        if (state is null) return (false, $"the last scan recorded no journal position for {volume.Letter}");
        if (state.Serial != volume.Serial) return (false, $"{volume.Letter} is a different volume than it was");

        return (true, "ready");
    }

    /// <summary>
    /// Whether this snapshot can be the base of a rescan of these roots at all, before any
    /// volume is opened.
    /// </summary>
    /// <remarks>
    /// The root set has to match exactly. Rescanning <c>C:\Users</c> from a snapshot of
    /// <c>C:\</c> would produce a tree that says it covers the volume while holding one
    /// directory, and a rescan of <c>C:\</c> from a snapshot of <c>C:\Users</c> would
    /// invent the rest of the disk out of nothing.
    /// </remarks>
    internal static bool CanBase(SnapshotContents? snapshot, IReadOnlyList<VolumeInfo> volumes, out string reason)
    {
        reason = "";

        if (snapshot is null)
        {
            reason = "there is no previous scan to start from";
            return false;
        }

        if ((snapshot.Flags & ScanFlags.Partial) != 0)
        {
            reason = "the previous scan was cancelled, so its tree is incomplete";
            return false;
        }

        if (snapshot.Usn.Count == 0)
        {
            reason = "the previous scan recorded no journal position";
            return false;
        }

        var before = snapshot.Volumes.Select(v => v.Root).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var now = volumes.Select(v => v.Root).ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (!before.SetEquals(now))
        {
            reason = $"the previous scan covered {string.Join(", ", before.Order(StringComparer.OrdinalIgnoreCase))}";
            return false;
        }

        return true;
    }

    public Task<ScanResult> ScanAsync(
        ScanRequest request,
        IReadOnlyList<VolumeInfo> volumes,
        IProgress<ScanProgress>? progress,
        CancellationToken ct)
    {
        var startedUtc = DateTime.UtcNow;
        var stopwatch = Stopwatch.StartNew();

        var errors = new List<ScanError>();
        var changed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var watermarks = new List<UsnState>(volumes.Count);
        var records = 0;

        foreach (var volume in volumes)
        {
            ct.ThrowIfCancellationRequested();

            var (can, why) = CanScan(volume);
            if (!can) throw new UsnUnavailableException(why);

            var state = Find(volume)!;

            using var journal = UsnJournal.Open(volume.Letter, volume.Root);

            if (journal.Data.UsnJournalId != state.JournalId)
                throw new UsnUnavailableException(
                    $"{volume.Letter}'s change journal was recreated since the last scan");

            // LowestValidUsn is 0 on a journal that has never been trimmed, so it only
            // rules the watermark out once the journal has actually rolled over.
            if (state.NextUsn < journal.Data.LowestValidUsn || state.NextUsn < journal.Data.FirstUsn)
                throw new UsnUnavailableException(
                    $"{volume.Letter}'s journal no longer reaches back to the last scan");

            if (state.NextUsn > journal.Data.NextUsn)
                throw new UsnUnavailableException(
                    $"{volume.Letter}'s journal has gone backwards since the last scan");

            // Taken now, not after the walk: see UsnState.
            watermarks.Add(state with { NextUsn = journal.Data.NextUsn, Serial = volume.Serial });

            var changes = journal.Read(state.NextUsn, ct);
            records += changes.Count;

            using var paths = new FileIdPaths(volume.Root);
            if (!paths.Usable)
                throw new UsnUnavailableException($"{volume.Letter}'s root directory could not be opened");

            Collect(changes, paths, volume, changed, progress, stopwatch, records);

            if (changed.Count > IncrementalTree.MaxChangedDirectories)
                throw new UsnUnavailableException(
                    $"{changed.Count:N0} directories changed since the last scan, which is more than a full scan costs");
        }

        var lister = new DirectoryLister(new NameBlobBuilder(1 << 12), 0, resolveAllocatedSize: true);
        var tree = IncrementalTree.Rebuild(_base.Tree, changed, Reader(lister, volumes), errors, ct);

        stopwatch.Stop();

        // The base's own accuracy limits carry over - they describe the untouched part of
        // the tree, which is most of it - and the changed part was read by the walk lister,
        // so its limits apply too (README section 4.5).
        var flags = _base.Flags
                  | ScanFlags.Incremental
                  | ScanFlags.Degraded
                  | ScanFlags.PartialHardlinkResolution
                  | ScanFlags.NoAdsAccounting;

        flags &= ~ScanFlags.Partial;
        if (Elevation.IsElevated) flags |= ScanFlags.Elevated;

        return Task.FromResult(new ScanResult
        {
            Scanner = ScannerKind.Incremental,
            Flags = flags,
            StartedUtc = startedUtc,
            Duration = stopwatch.Elapsed,
            Volumes = volumes,
            Tree = tree,
            Errors = errors,
            Note = request.Note,
            Usn = watermarks,
            ChangedDirectories = changed.Count,
        });
    }

    /// <summary>
    /// Turns records into the set of directories to read again.
    /// </summary>
    /// <remarks>
    /// Only the parent of each record is needed. Whatever happened - a file created,
    /// deleted, renamed, written to, truncated - the directory holding it has to be
    /// enumerated again, and enumerating it answers every one of those at once. A record
    /// whose parent cannot be resolved to a path is a directory that no longer exists;
    /// its own removal was recorded against <em>its</em> parent, which will be read.
    /// </remarks>
    private static void Collect(
        List<UsnChange> changes, FileIdPaths paths, VolumeInfo volume,
        HashSet<string> changed, IProgress<ScanProgress>? progress, Stopwatch clock, int records)
    {
        var root = volume.Root;
        var seen = 0;

        foreach (var change in changes)
        {
            if (paths.TryResolve(change.ParentFileId) is not { } path) continue;

            // A record can name a directory outside the scanned root - the journal covers
            // the whole volume, and a root may be a subtree (README section 4.2). Segment
            // aware, so a root of C:\Windows does not swallow C:\WindowsApps.
            if (!Deletion.Canonical.IsSameOrUnder(path, root)) continue;

            changed.Add(IncrementalTree.Key(path));

            if (++seen % 256 == 0)
            {
                progress?.Report(new ScanProgress
                {
                    Entries = records,
                    Elapsed = clock.Elapsed,
                    CurrentPath = path,
                });
            }
        }
    }

    /// <summary>
    /// Adapts the scan-time directory lister, which speaks interned name offsets, to the
    /// plain entries the rebuild wants.
    /// </summary>
    /// <remarks>
    /// Internal rather than private so that a test can rebuild a real tree through the same
    /// adapter the scanner uses. The reading of one directory is where allocated sizes and
    /// link counts actually come from, and a fake over it would prove nothing about them
    /// (README section 22.2).
    /// </remarks>
    internal static LiveDirectoryReader Reader(DirectoryLister lister, IReadOnlyList<VolumeInfo> volumes)
    {
        var raw = new List<RawEntry>(256);

        return (string path, List<LiveEntry> into, out ScanError? error) =>
        {
            into.Clear();

            // The same rounding the full scan applied, or a rescan would replace a
            // directory's cluster-rounded sizes with logical ones and the tree would
            // shrink by the difference every time something in it changed.
            long cluster = 0;
            foreach (var volume in volumes)
            {
                if (!path.StartsWith(volume.Root, StringComparison.OrdinalIgnoreCase)) continue;
                cluster = volume.ClusterBytes;
                break;
            }

            if (!lister.TryList(path, raw, cluster, out error)) return false;

            for (var i = 0; i < raw.Count; i++)
            {
                var entry = raw[i];
                into.Add(new LiveEntry(
                    System.Text.Encoding.UTF8.GetString(lister.Blob.Read(entry.NameOffset)),
                    entry.Logical, entry.Allocated, entry.Mtime, entry.Attributes, entry.Flags,
                    entry.LinkCount));
            }

            // The lister's file ids are deliberately not used to pick hard link owners: the
            // owner of a link in a changed directory may well sit in one this rescan never
            // opened, and a decision made over part of the tree would be worse than the
            // previous scan's. That is what PartialHardlinkResolution says on the result.
            return true;
        };
    }

    private UsnState? Find(VolumeInfo volume) =>
        _base.Usn.FirstOrDefault(u => u.Letter.Equals(volume.Letter, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Reads every volume's journal position, for a scan that is about to start.
    /// </summary>
    /// <remarks>
    /// Needs no privilege, and failure is not an error: a volume with no journal simply
    /// gets no entry, and the next scan over it is a full one.
    /// </remarks>
    internal static IReadOnlyList<UsnState> Capture(IReadOnlyList<VolumeInfo> volumes)
    {
        var states = new List<UsnState>(volumes.Count);

        foreach (var volume in volumes)
        {
            if (!volume.IsNtfs) continue;
            if (!UsnJournal.TryQuery(volume.Root, out var data, out _)) continue;

            states.Add(new UsnState(volume.Letter, volume.Serial, data.UsnJournalId, data.NextUsn));
        }

        return states;
    }
}
