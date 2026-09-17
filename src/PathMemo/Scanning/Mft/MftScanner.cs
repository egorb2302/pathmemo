using System.Diagnostics;
using System.Text;
using PathMemo.Config;
using PathMemo.Platform;
using PathMemo.Snapshots;

namespace PathMemo.Scanning.Mft;

/// <summary>
/// The primary scanner: reads every file record from the $MFT and builds the tree from
/// parent references, without opening a single file (README section 4.3).
/// </summary>
/// <remarks>
/// <para>
/// Two passes over arrays indexed by record number. The first decodes each record into
/// sizes, attributes and names; extension records (a file whose attributes overflowed
/// one segment - typically one with dozens of hard links) contribute to their base
/// record. The second walks the tree breadth-first from the root record, which is
/// what makes children contiguous in the store and, as a side effect, makes the
/// shallowest path of a hard-linked file its owner: <c>System32\foo.dll</c> owns the
/// bytes, <c>WinSxS\...\foo.dll</c> is the alias.
/// </para>
/// <para>
/// A record whose parent is missing, deleted or reused (sequence mismatch) is an
/// orphan. They exist on every volume in small numbers; they are counted, reported,
/// and not attached anywhere, because inventing a location for them would be a lie.
/// </para>
/// </remarks>
internal sealed class MftScanner : IScanner
{
    public ScannerKind Kind => ScannerKind.Mft;

    public (bool Can, string Reason) CanScan(VolumeInfo volume) => volume.MftScanAvailability;

    private const long RootRecord = 5;
    private const int ReadBufferBytes = 8 << 20;

    /// <summary>Segment-level record flags, one byte per record.</summary>
    [Flags]
    private enum R : byte
    {
        None = 0,
        InUse = 1,
        Directory = 2,
        Sparse = 4,
        Seen = 8,
    }

    /// <summary>One name of one file, as the tree sees it.</summary>
    private readonly record struct Link(int Record, ushort RecordSequence, int Parent, ushort ParentSequence, int NameOffset);

    private readonly record struct ExtensionSizes(int Record, ushort Sequence, long Logical, long Allocated, bool Sparse);

    private sealed class Counters
    {
        internal long Records;
        internal long Bytes;
        internal long Total;
    }

    public async Task<ScanResult> ScanAsync(
        ScanRequest request,
        IReadOnlyList<VolumeInfo> volumes,
        IProgress<ScanProgress>? progress,
        CancellationToken ct)
    {
        var startedUtc = DateTime.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        var errors = new List<ScanError>();
        var parts = new List<NodeStore>(volumes.Count);
        var counters = new Counters();

        using var progressLoop = StartProgressLoop(progress, counters, stopwatch, ct);

        var cancelled = false;
        foreach (var volume in volumes)
        {
            // The tree build is CPU work of a few seconds; keep it off the caller's
            // thread so the progress timer and Ctrl+C stay responsive.
            var part = await Task.Run(() => ScanVolume(volume, counters, errors, ct), CancellationToken.None);
            parts.Add(part);

            if (ct.IsCancellationRequested)
            {
                cancelled = true;
                break;
            }
        }

        stopwatch.Stop();

        var tree = TreeAssembly.Concat(parts);
        TreeAssembly.MarkSelfData(tree, AppPaths.DataDirectory);

        var flags = ScanFlags.None;
        if (cancelled || parts.Count < volumes.Count) flags |= ScanFlags.Partial;
        if (Elevation.IsElevated) flags |= ScanFlags.Elevated;

        return new ScanResult
        {
            Scanner = ScannerKind.Mft,
            Flags = flags,
            StartedUtc = startedUtc,
            Duration = stopwatch.Elapsed,
            Volumes = [.. volumes.Take(parts.Count)],
            Tree = tree,
            Errors = errors,
            Note = request.Note,
        };
    }

    private static NodeStore ScanVolume(
        VolumeInfo volume, Counters counters, List<ScanError> errors, CancellationToken ct)
    {
        using var mft = MftVolume.Open(volume.Letter);

        var n = checked((int)mft.RecordCount);
        Interlocked.Add(ref counters.Total, n);

        var sequence = new ushort[n];
        var linkCount = new ushort[n];
        var rflags = new R[n];
        var logical = new long[n];
        var allocated = new long[n];
        var mtime = new uint[n];
        var attributes = new uint[n];

        var names = new NameBlobBuilder(Math.Max(1 << 14, n / 8));
        var links = new List<Link>(n);
        var extensions = new List<ExtensionSizes>();
        var scratch = new List<MftName>(4);

        var unreadable = 0;

        // Double-buffered: the next chunk is read from the volume while this one is
        // decoded. The read is sequential I/O, the decode is CPU - they overlap cleanly.
        var buffers = new[] { new byte[ReadBufferBytes], new byte[ReadBufferBytes] };
        var recordsPerBuffer = ReadBufferBytes / mft.BytesPerRecord;
        var current = 0;
        long first = 0;

        var pending = Task.Run(() => mft.ReadRecords(0, buffers[0]));

        while (true)
        {
            // The in-flight read completes before a cancellation is honoured, so the
            // volume handle is never closed under it.
            var got = pending.GetAwaiter().GetResult();
            ct.ThrowIfCancellationRequested();
            if (got == 0) break;

            var buffer = buffers[current];
            var nextFirst = first + got;
            var nextBuffer = buffers[current ^ 1];
            pending = nextFirst < mft.RecordCount
                ? Task.Run(() => mft.ReadRecords(nextFirst, nextBuffer))
                : Task.FromResult(0);

            long bytesThisChunk = 0;

            for (var i = 0; i < got; i++)
            {
                var record = buffer.AsSpan(i * mft.BytesPerRecord, mft.BytesPerRecord);
                var number = (int)(first + i);

                if (!MftParser.ApplyFixup(record, mft.BytesPerSector))
                {
                    // Never-written slots are zero and expected; anything else is a
                    // record we could not trust.
                    if (record.IndexOfAnyExcept((byte)0) >= 0) unreadable++;
                    continue;
                }

                scratch.Clear();
                var parsed = MftParser.Parse(record, number, mft.ClusterBytes, names, scratch);
                if (!parsed.InUse) continue;

                if (parsed.BaseRecord < 0 || parsed.BaseRecord >= n) continue;
                var target = (int)parsed.BaseRecord;

                foreach (var name in scratch)
                {
                    if (name.ParentRecord < 0 || name.ParentRecord >= n) continue;
                    links.Add(new Link(target, parsed.BaseSequence,
                        (int)name.ParentRecord, name.ParentSequence, name.NameOffset));
                }

                if (parsed.IsExtension)
                {
                    if (parsed.Logical != 0 || parsed.Allocated != 0 || parsed.Sparse)
                        extensions.Add(new ExtensionSizes(target, parsed.BaseSequence,
                            parsed.Logical, parsed.Allocated, parsed.Sparse));
                    continue;
                }

                sequence[number] = parsed.Sequence;
                linkCount[number] = parsed.LinkCount;
                logical[number] += parsed.Logical;
                allocated[number] += parsed.Allocated;
                attributes[number] = parsed.Attributes;
                mtime[number] = FileTimeToSnapshot(parsed.ModifiedFileTime);

                var f = R.InUse;
                if (parsed.IsDirectory) f |= R.Directory;
                if (parsed.Sparse) f |= R.Sparse;
                rflags[number] |= f;

                bytesThisChunk += parsed.Allocated;
            }

            Interlocked.Add(ref counters.Records, got);
            Interlocked.Add(ref counters.Bytes, bytesThisChunk);

            first = nextFirst;
            current ^= 1;
        }

        // Sizes that overflowed into extension records belong to their base, provided
        // the base is the same incarnation of that slot.
        foreach (var ext in extensions)
        {
            if ((rflags[ext.Record] & R.InUse) == 0 || sequence[ext.Record] != ext.Sequence) continue;
            logical[ext.Record] += ext.Logical;
            allocated[ext.Record] += ext.Allocated;
            if (ext.Sparse) rflags[ext.Record] |= R.Sparse;
        }

        // Index links by parent: counting sort into one array, so a directory's
        // children are a slice. Invalid links are orphans.
        var childCount = new int[n];
        var orphans = 0;
        var valid = 0;

        bool IsValid(in Link link)
        {
            if (link.Record == RootRecord && link.Parent == RootRecord) return false;   // the root's own "."
            if ((rflags[link.Record] & R.InUse) == 0 || sequence[link.Record] != link.RecordSequence) return false;
            if ((rflags[link.Parent] & (R.InUse | R.Directory)) != (R.InUse | R.Directory)) return false;
            if (sequence[link.Parent] != link.ParentSequence) return false;
            return true;
        }

        foreach (var link in links)
        {
            if (!IsValid(link))
            {
                if (link.Record != RootRecord) orphans++;
                continue;
            }
            childCount[link.Parent]++;
            valid++;
        }

        var childStart = new int[n + 1];
        for (var i = 0; i < n; i++) childStart[i + 1] = childStart[i] + childCount[i];

        var sorted = new int[valid];
        var fill = new int[n];
        for (var i = 0; i < links.Count; i++)
        {
            var link = links[i];
            if (!IsValid(link)) continue;
            sorted[childStart[link.Parent] + fill[link.Parent]++] = i;
        }

        // Start record: the volume root, or the directory the request points at.
        var startRecord = (int)RootRecord;
        if (!IsVolumeRoot(volume))
        {
            startRecord = FindRecord(volume, names, links, sorted, childStart, childCount);
            if (startRecord < 0)
                throw new MftUnavailableException($"{volume.Root} was not found in the $MFT");
        }

        if ((rflags[startRecord] & (R.InUse | R.Directory)) != (R.InUse | R.Directory))
            throw new MftUnavailableException($"record {startRecord} of {volume.Letter} is not a live directory");

        // Breadth-first emission from the start record.
        var capacity = valid + 1;
        var parent = new int[capacity];
        var nameOffset = new int[capacity];
        var firstChild = new int[capacity];
        var nodeChildCount = new int[capacity];
        var nodeAllocated = new long[capacity];
        var nodeLogical = new long[capacity];
        var nodeMtime = new uint[capacity];
        var nodeAttributes = new uint[capacity];
        var nodeFlags = new NodeFlags[capacity];
        var nodeLinks = new byte[capacity];

        var queue = new Queue<(int Record, int Node)>();
        var next = 0;

        var rootNode = next++;
        parent[rootNode] = NodeStore.NoNode;
        nameOffset[rootNode] = names.Intern(volume.Root);
        nodeFlags[rootNode] = NodeFlags.Directory;
        nodeAllocated[rootNode] = allocated[startRecord];
        nodeLogical[rootNode] = logical[startRecord];
        nodeMtime[rootNode] = mtime[startRecord];
        nodeAttributes[rootNode] = attributes[startRecord] | (uint)FileAttributes.Directory;
        nodeLinks[rootNode] = 1;
        rflags[startRecord] |= R.Seen;
        queue.Enqueue((startRecord, rootNode));

        while (queue.Count > 0)
        {
            var (record, node) = queue.Dequeue();
            var count = childCount[record];
            if (count == 0) continue;

            firstChild[node] = next;
            nodeChildCount[node] = count;

            var start = childStart[record];
            for (var k = 0; k < count; k++)
            {
                var link = links[sorted[start + k]];
                var child = link.Record;
                var index = next++;

                parent[index] = node;
                nameOffset[index] = link.NameOffset;
                nodeAllocated[index] = allocated[child];
                nodeLogical[index] = logical[child];
                nodeMtime[index] = mtime[child];
                nodeLinks[index] = (byte)Math.Min(linkCount[child], (ushort)255);

                var rf = rflags[child];
                var isDir = (rf & R.Directory) != 0;
                var attr = Win32Attributes(attributes[child], isDir);
                nodeAttributes[index] = attr;

                var f = NodeFlags.None;
                if (isDir) f |= NodeFlags.Directory;
                if ((attr & (uint)FileAttributes.ReparsePoint) != 0) f |= NodeFlags.Reparse;
                if ((attr & (uint)FileAttributes.Encrypted) != 0) f |= NodeFlags.Encrypted;
                if (IsCloudPlaceholder(attr)) f |= NodeFlags.CloudOnly;
                if ((rf & R.Sparse) != 0 || (!isDir && allocated[child] < logical[child] - (logical[child] >> 4)))
                    f |= NodeFlags.Sparse;

                // First emission owns the bytes; every later name of the same record is
                // an alias. A directory reached twice is data corruption and must not be
                // walked twice.
                if ((rf & R.Seen) != 0)
                {
                    f |= NodeFlags.HardlinkAlias;
                }
                else
                {
                    rflags[child] |= R.Seen;
                    if (isDir) queue.Enqueue((child, index));
                }

                nodeFlags[index] = f;
            }
        }

        if (next < capacity)
        {
            Array.Resize(ref parent, next);
            Array.Resize(ref nameOffset, next);
            Array.Resize(ref firstChild, next);
            Array.Resize(ref nodeChildCount, next);
            Array.Resize(ref nodeAllocated, next);
            Array.Resize(ref nodeLogical, next);
            Array.Resize(ref nodeMtime, next);
            Array.Resize(ref nodeAttributes, next);
            Array.Resize(ref nodeFlags, next);
            Array.Resize(ref nodeLinks, next);
        }

        var store = new NodeStore
        {
            Parent = parent,
            NameOffset = nameOffset,
            FirstChild = firstChild,
            ChildCount = nodeChildCount,
            Allocated = nodeAllocated,
            Logical = nodeLogical,
            FileCount = new int[next],
            Mtime = nodeMtime,
            Attributes = nodeAttributes,
            Flags = nodeFlags,
            LinkCount = nodeLinks,
            VolumeIndex = new byte[next],
            NameBlob = names.ToBlob(),
            Roots = [rootNode],
        };

        TreeAssembly.Aggregate(store);

        lock (errors)
        {
            if (orphans > 0)
                errors.Add(new ScanError(volume.Root, ScanErrorKind.Unknown, 0,
                    $"{orphans:N0} file records whose parent directory no longer exists (orphans, not placed in the tree)"));
            if (unreadable > 0)
                errors.Add(new ScanError(volume.Root, ScanErrorKind.IoError, 0,
                    $"{unreadable:N0} file records failed their integrity check and were skipped"));
        }

        return store;
    }

    private const uint RecallOnOpen = 0x00040000;           // on disk, the same bit means "has extended attributes"
    private const uint RecallOnDataAccess = 0x00400000;

    /// <summary>
    /// The attributes as Win32 would report them. On disk, $STANDARD_INFORMATION carries
    /// 0x40000 for "has extended attributes" - every file applied from a WIM image, and
    /// C:\Windows itself - and Win32 reuses that value for RECALL_ON_OPEN, which it only
    /// ever sets on a reparse point. Without this mask half of Windows shows as cloud.
    /// </summary>
    internal static uint Win32Attributes(uint onDisk, bool isDirectory)
    {
        var attr = onDisk;
        if ((attr & (uint)FileAttributes.ReparsePoint) == 0) attr &= ~RecallOnOpen;
        if (isDirectory) attr |= (uint)FileAttributes.Directory;
        return attr;
    }

    /// <summary>A cloud placeholder is a reparse point that recalls on open or access, or an HSM-offline file.</summary>
    internal static bool IsCloudPlaceholder(uint win32Attributes)
    {
        if ((win32Attributes & (uint)FileAttributes.Offline) != 0) return true;
        if ((win32Attributes & (uint)FileAttributes.ReparsePoint) == 0) return false;
        return (win32Attributes & (RecallOnOpen | RecallOnDataAccess)) != 0;
    }

    private static bool IsVolumeRoot(VolumeInfo volume) =>
        volume.Root.Length <= 3 || volume.Root.TrimEnd(Path.DirectorySeparatorChar).Length <= 2;

    /// <summary>Resolves a directory path to its record by walking names from the root.</summary>
    private static int FindRecord(
        VolumeInfo volume, NameBlobBuilder names, List<Link> links, int[] sorted, int[] childStart, int[] childCount)
    {
        var relative = volume.Root.AsSpan(Math.Min(3, volume.Root.Length));
        var record = (int)RootRecord;
        Span<char> decoded = stackalloc char[512];

        foreach (var range in relative.Split(Path.DirectorySeparatorChar))
        {
            var segment = relative[range];
            if (segment.IsEmpty) continue;

            var found = -1;
            var start = childStart[record];

            for (var k = 0; k < childCount[record]; k++)
            {
                var link = links[sorted[start + k]];
                var utf8 = names.Read(link.NameOffset);
                if (utf8.Length > decoded.Length * 3) continue;

                var chars = Encoding.UTF8.GetChars(utf8, decoded);
                if (segment.Equals(decoded[..chars], StringComparison.OrdinalIgnoreCase))
                {
                    found = link.Record;
                    break;
                }
            }

            if (found < 0) return -1;
            record = found;
        }

        return record;
    }

    private static uint FileTimeToSnapshot(long fileTime)
    {
        if (fileTime <= 0) return 0;
        try { return SnapshotTime.FromDateTime(DateTime.FromFileTimeUtc(fileTime)); }
        catch (ArgumentOutOfRangeException) { return 0; }
    }

    private static Timer StartProgressLoop(
        IProgress<ScanProgress>? progress, Counters counters, Stopwatch stopwatch, CancellationToken ct)
    {
        if (progress is null) return new Timer(static _ => { }, null, Timeout.Infinite, Timeout.Infinite);

        return new Timer(_ =>
        {
            if (ct.IsCancellationRequested) return;
            var total = Volatile.Read(ref counters.Total);
            var records = Volatile.Read(ref counters.Records);
            progress.Report(new ScanProgress
            {
                Entries = records,
                Bytes = Volatile.Read(ref counters.Bytes),
                Elapsed = stopwatch.Elapsed,
                Fraction = total > 0 ? Math.Min(1.0, (double)records / total) : null,
                CurrentPath = "$MFT",
            });
        }, null, 250, 250);
    }
}
