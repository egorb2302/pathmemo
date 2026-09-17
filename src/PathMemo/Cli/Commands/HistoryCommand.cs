using System.Globalization;
using PathMemo.Cli.Output;
using PathMemo.Snapshots;

namespace PathMemo.Cli.Commands;

/// <summary>
/// Stored scans. Reads the snapshot directory directly; the scans table arrives with
/// SQLite in a later phase.
/// </summary>
internal static class HistoryCommand
{
    internal static int Run()
    {
        var entries = SnapshotStore.List();
        if (entries.Count == 0)
        {
            Console.Error.WriteLine("pathmemo: no scans yet - run 'pathmemo scan' first");
            return ExitCode.NoData;
        }

        var w = Console.Out;
        w.WriteLine();
        w.WriteLine("    ID  STARTED (UTC)      SCANNER       FILES       SIZE   SNAPSHOT  FLAGS");
        w.WriteLine("  " + new string('-', 82));

        long totalSnapshotBytes = 0;

        foreach (var entry in entries)
        {
            totalSnapshotBytes += entry.Bytes;

            string line;
            try
            {
                var snapshot = SnapshotFile.Read(entry.Path);
                var tree = snapshot.Tree;
                var files = tree.Roots.Sum(r => tree.FileCount[r]);
                var bytes = tree.Roots.Sum(r => tree.Allocated[r]);

                line = string.Format(CultureInfo.InvariantCulture,
                    "  {0,4}  {1:yyyy-MM-dd HH:mm}   {2,-9} {3,11}  {4,9}  {5,9}  {6}",
                    entry.Id, snapshot.StartedUtc, snapshot.Scanner.ToString().ToLowerInvariant(),
                    files.ToString("N0", CultureInfo.InvariantCulture),
                    SizeFormat.Bytes(bytes), SizeFormat.Bytes(entry.Bytes),
                    Describe(snapshot.Flags));
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException)
            {
                line = string.Format(CultureInfo.InvariantCulture,
                    "  {0,4}  {1:yyyy-MM-dd HH:mm}   (unreadable: {2})", entry.Id, entry.WrittenUtc, ex.Message);
            }

            w.WriteLine(line);
        }

        w.WriteLine("  " + new string('-', 82));
        w.WriteLine($"  {entries.Count} scans, {SizeFormat.Bytes(totalSnapshotBytes)} of snapshots");
        return ExitCode.Ok;
    }

    private static string Describe(Scanning.ScanFlags flags)
    {
        var parts = new List<string>(4);
        if ((flags & Scanning.ScanFlags.Partial) != 0) parts.Add("partial");
        if ((flags & Scanning.ScanFlags.Degraded) != 0) parts.Add("degraded");
        if ((flags & Scanning.ScanFlags.Elevated) != 0) parts.Add("elevated");
        if ((flags & Scanning.ScanFlags.Incremental) != 0) parts.Add("incremental");
        return string.Join(' ', parts);
    }
}
