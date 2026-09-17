using System.Diagnostics;
using System.IO.Enumeration;
using PathMemo.Scanning;

namespace PathMemo.Audit;

/// <summary>Size of one directory tree, with what could not be read.</summary>
internal readonly record struct Measured(long Logical, long Allocated, int Files, int Errors, bool Partial)
{
    internal static readonly Measured Empty = default;

    internal Measured Add(Measured other) => new(
        Logical + other.Logical,
        Allocated + other.Allocated,
        Files + other.Files,
        Errors + other.Errors,
        Partial || other.Partial);

    /// <summary>True when at least one directory could not be listed.</summary>
    internal bool Incomplete => Errors > 0 || Partial;
}

/// <summary>
/// Sizes a directory the audit cares about: a cache, a log folder, a temp directory.
/// </summary>
/// <remarks>
/// <para>
/// Thousands of files, not millions, so this stays simple: recursion is a plain stack,
/// reparse points are never followed (README section 4.6), and errors are counted, not
/// thrown, because a half-readable directory is still worth reporting - with the caveat
/// attached.
/// </para>
/// <para>
/// The on-disk size is the logical size rounded up to a cluster, except for files whose
/// attributes say Compressed or SparseFile - only those get the extra syscall. Measured
/// on a 175k-file temp directory: 20 s with a call per file, a few seconds without.
/// </para>
/// </remarks>
internal static class DirectoryMeasure
{
    private static readonly EnumerationOptions Options = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = false,
        AttributesToSkip = 0,
        BufferSize = 64 * 1024,
        MatchType = MatchType.Win32,
        ReturnSpecialDirectories = false,
    };

    /// <summary>A subdirectory to descend into, a file to count, or (neither) a link to ignore.</summary>
    private readonly record struct Entry(string? Subdirectory, bool IsFile, long Logical, long Allocated);

    // The transform is a static delegate; the cluster size of the tree being measured
    // rides along per thread, since probes run in parallel.
    [ThreadStatic] private static long t_cluster;

    /// <summary>
    /// Returns <see cref="Measured.Empty"/> for a directory that does not exist; a caller
    /// distinguishes "absent" from "empty" with <see cref="Directory.Exists"/> beforehand.
    /// </summary>
    internal static Measured Directory_(string path, CancellationToken ct, TimeSpan? budget = null)
    {
        if (!Directory.Exists(path)) return Measured.Empty;

        var clock = Stopwatch.StartNew();
        var limit = budget ?? TimeSpan.FromSeconds(30);
        t_cluster = ClusterSize(path);

        long logical = 0, allocated = 0;
        int files = 0, errors = 0;
        var partial = false;

        var pending = new Stack<string>();
        pending.Push(path);

        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();

            if (clock.Elapsed > limit)
            {
                partial = true;
                break;
            }

            var directory = pending.Pop();

            try
            {
                foreach (var entry in new FileSystemEnumerable<Entry>(directory, Project, Options))
                {
                    if (entry.Subdirectory is { } sub)
                    {
                        pending.Push(sub);
                        continue;
                    }

                    if (!entry.IsFile) continue;

                    logical += entry.Logical;
                    allocated += entry.Allocated;
                    files++;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errors++;
            }
        }

        return new Measured(logical, allocated, files, errors, partial);
    }

    /// <summary>A single file's sizes; both zero when it is absent.</summary>
    internal static Measured File_(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists) return Measured.Empty;

        var logical = info.Length;
        var allocated = AllocatedSize(info.DirectoryName ?? "", info.Name, logical);
        return new Measured(logical, allocated, 1, 0, false);
    }

    private static Entry Project(ref FileSystemEntry entry)
    {
        var attributes = entry.Attributes;

        if ((attributes & FileAttributes.ReparsePoint) != 0)
            return new Entry(null, false, 0, 0);          // a link: counted as nothing, never followed

        if (entry.IsDirectory)
            return new Entry(entry.ToFullPath(), false, 0, 0);

        var logical = entry.Length;

        // A cloud placeholder occupies nothing locally; reporting its logical size would
        // promise space that deleting it cannot free (threat T10).
        if ((attributes & ((FileAttributes)0x00400000 | (FileAttributes)0x00040000 | FileAttributes.Offline)) != 0)
            return new Entry(null, true, logical, 0);

        if (logical == 0) return new Entry(null, true, 0, 0);

        var allocated = (attributes & (FileAttributes.Compressed | FileAttributes.SparseFile)) != 0
            ? DirectoryLister.QueryAllocatedSize(entry.Directory, entry.FileName, logical)
            : RoundUp(logical, t_cluster);

        return new Entry(null, true, logical, allocated);
    }

    private static long RoundUp(long bytes, long cluster) =>
        cluster <= 0 ? bytes : (bytes + cluster - 1) / cluster * cluster;

    private static long ClusterSize(string path)
    {
        var root = Path.GetPathRoot(path);
        if (string.IsNullOrEmpty(root)) return 4096;

        return Platform.Native.Kernel32.GetDiskFreeSpace(root, out var perCluster, out var perSector, out _, out _)
            ? (long)perCluster * perSector
            : 4096;
    }

    private static long AllocatedSize(string directory, string name, long fallback) =>
        DirectoryLister.QueryAllocatedSize(directory, name, fallback);
}
