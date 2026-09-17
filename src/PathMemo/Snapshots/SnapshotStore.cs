using System.Globalization;
using PathMemo.Config;
using PathMemo.Scanning;

namespace PathMemo.Snapshots;

internal sealed record SnapshotEntry(long Id, string Path, DateTime WrittenUtc, long Bytes);

/// <summary>
/// The snapshots directory: numbering, lookup and retention.
/// </summary>
/// <remarks>
/// Retention is a hard rule rather than the tiered day-count policy a disk tool is tempted
/// to write: 20 recent snapshots plus one per month for a year, and a total cap. A tool for
/// freeing disk space that grows without bound is self-defeating (README sections 5.4, P7).
/// </remarks>
internal static class SnapshotStore
{
    private const string Extension = ".pmsnap";
    private const int KeepRecent = 20;
    private const int KeepMonthly = 12;
    private const long MaxTotalBytes = 400L * 1024 * 1024;

    /// <summary>
    /// Writes a snapshot under the given scan id and applies retention. The id comes from
    /// the caller, not from this directory: it is the same number as the row in
    /// <c>scans</c>, so that "scan 42" means one thing everywhere (README section 11).
    /// </summary>
    /// <returns>Ids of snapshots that retention deleted.</returns>
    internal static IReadOnlyList<long> Save(ScanResult result, long id)
    {
        Directory.CreateDirectory(AppPaths.SnapshotsDirectory);

        var path = PathFor(id);

        // Write to a temp name and move into place, so an interrupted write never leaves a
        // truncated snapshot that later looks loadable.
        var temp = path + ".tmp";
        SnapshotFile.Write(temp, result);
        File.Move(temp, path, overwrite: true);

        return ApplyRetention();
    }

    /// <summary>Highest snapshot number present on disk, or 0.</summary>
    internal static long MaxId() => List().Select(s => s.Id).DefaultIfEmpty(0).Max();

    internal static string PathFor(long id) => Path.Combine(
        AppPaths.SnapshotsDirectory,
        id.ToString("D10", CultureInfo.InvariantCulture) + Extension);

    internal static IReadOnlyList<SnapshotEntry> List()
    {
        if (!Directory.Exists(AppPaths.SnapshotsDirectory)) return [];

        var entries = new List<SnapshotEntry>();
        foreach (var file in Directory.EnumerateFiles(AppPaths.SnapshotsDirectory, "*" + Extension))
        {
            var stem = Path.GetFileNameWithoutExtension(file);
            if (!long.TryParse(stem, CultureInfo.InvariantCulture, out var id)) continue;

            var info = new FileInfo(file);
            entries.Add(new SnapshotEntry(id, file, info.LastWriteTimeUtc, info.Length));
        }

        entries.Sort((a, b) => b.Id.CompareTo(a.Id));
        return entries;
    }

    internal static SnapshotEntry? Latest() => List().FirstOrDefault();

    internal static SnapshotContents Load(long id)
    {
        var path = PathFor(id);
        if (!File.Exists(path)) throw new FileNotFoundException($"snapshot {id} not found", path);
        return SnapshotFile.Read(path);
    }

    private static IReadOnlyList<long> ApplyRetention()
    {
        var all = List();
        if (all.Count == 0) return [];

        var keep = new HashSet<long>(all.Take(KeepRecent).Select(s => s.Id));

        // One per calendar month, newest in that month, for the last KeepMonthly months.
        var months = new HashSet<(int Year, int Month)>();
        foreach (var entry in all)
        {
            var month = (entry.WrittenUtc.Year, entry.WrittenUtc.Month);
            if (months.Count >= KeepMonthly && !months.Contains(month)) continue;
            if (months.Add(month)) keep.Add(entry.Id);
        }

        var survivors = all.Where(s => keep.Contains(s.Id)).ToList();
        var doomed = all.Where(s => !keep.Contains(s.Id)).ToList();

        // Cap the total. Oldest monthly snapshots go first; the three newest are never
        // dropped, because losing those would break the next diff.
        var total = survivors.Sum(s => s.Bytes);
        foreach (var entry in survivors.OrderBy(s => s.Id))
        {
            if (total <= MaxTotalBytes) break;
            if (all.Take(3).Any(s => s.Id == entry.Id)) continue;

            doomed.Add(entry);
            total -= entry.Bytes;
        }

        var deleted = new List<long>(doomed.Count);
        foreach (var entry in doomed)
        {
            try
            {
                File.Delete(entry.Path);
                deleted.Add(entry.Id);
            }
            catch (IOException) { /* locked by another pathmemo; retried on the next scan */ }
        }

        return deleted;
    }
}
