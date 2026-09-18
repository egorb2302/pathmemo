using System.Globalization;
using PathMemo.Cli.Output;
using PathMemo.Config;
using PathMemo.Platform;
using PathMemo.Platform.Native;
using PathMemo.Scanning;
using PathMemo.Snapshots;
using PathMemo.Storage;

namespace PathMemo.Cli.Commands;

/// <summary>
/// Environment diagnostics: what pathmemo can and cannot do on this machine,
/// and why. First command implemented because every later phase depends on
/// the facts it reports (README section 13, acceptance criteria "CLI").
/// </summary>
internal static class DoctorCommand
{
    internal static int Run()
    {
        var w = Console.Out;

        w.WriteLine($"pathmemo {AppInfo.Version}   ·   doctor");
        w.WriteLine();

        Section(w, "PROCESS");
        Field(w, "Elevated", Elevation.IsElevated ? "yes" : "no  (fast MFT scan unavailable)");
        Field(w, "Architecture", System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString());
        Field(w, "OS", Environment.OSVersion.VersionString);
        Field(w, "Long paths", AppContext.TryGetSwitch("Switch.System.IO.UseLegacyPathHandling", out var legacy) && legacy
            ? "legacy (MAX_PATH enforced)"
            : "enabled");
        Field(w, "Logical cores", Environment.ProcessorCount.ToString(CultureInfo.InvariantCulture));
        w.WriteLine();

        Section(w, "TERMINAL");
        Field(w, "Output redirected", Console.IsOutputRedirected ? "yes  (TUI disabled)" : "no");
        Field(w, "Size", TerminalSize());
        Field(w, "Output encoding", Console.OutputEncoding.WebName);
        w.WriteLine();

        Section(w, "STORAGE");
        Field(w, "Data directory", AppPaths.DataDirectory + (AppPaths.IsRedirected ? "   (--data-dir)" : ""));
        Field(w, "Exists", Directory.Exists(AppPaths.DataDirectory) ? "yes" : "no  (created on first scan)");
        PrintDatabase(w);
        w.WriteLine();

        Section(w, "VOLUMES");
        var volumes = VolumeInfo.Enumerate();
        if (volumes.Count == 0)
        {
            w.WriteLine("  none detected");
        }
        else
        {
            w.WriteLine("  VOL  FS       CLUSTER      TOTAL       USED      FREE   USED%  MFT SCAN");
            w.WriteLine("  " + new string('-', 76));
            foreach (var v in volumes)
            {
                var (available, reason) = v.MftScanAvailability;
                w.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "  {0,-4} {1,-8} {2,7} {3,10} {4,10} {5,9} {6,6:F1}  {7}",
                    v.Letter,
                    v.FileSystem,
                    v.ClusterBytes == 0 ? "?" : SizeFormat.Bytes((ulong)v.ClusterBytes),
                    SizeFormat.Bytes(v.TotalBytes),
                    SizeFormat.Bytes(v.UsedBytes),
                    SizeFormat.Bytes(v.FreeBytes),
                    v.UsedPercent,
                    available ? "ready" : reason));
            }
        }
        w.WriteLine();

        PrintJournals(w, volumes);

        if (!Elevation.IsElevated && volumes.Any(v => v.IsNtfs))
        {
            w.WriteLine("  Hint: run as administrator to enable the MFT scanner and incremental rescans");
            w.WriteLine("        (seconds instead of minutes, plus accurate on-disk sizes).");
            w.WriteLine();
        }

        return ExitCode.Ok;
    }

    /// <summary>
    /// The change journal per volume, and whether the next scan can be an incremental one
    /// (README sections 4.5, 13).
    /// </summary>
    /// <remarks>
    /// Reporting this needs no privilege - <c>FSCTL_QUERY_USN_JOURNAL</c> answers a handle
    /// to the volume's root directory - while reading records from the journal is
    /// administrator-only. So an ordinary user can be told exactly why a rescan will take
    /// nine seconds rather than a third of one.
    /// </remarks>
    private static void PrintJournals(TextWriter w, IReadOnlyList<VolumeInfo> volumes)
    {
        var ntfs = volumes.Where(v => v.IsNtfs).ToList();
        if (ntfs.Count == 0) return;

        Section(w, "CHANGE JOURNAL");
        w.WriteLine("  VOL   STATE         SIZE       NEXT USN            INCREMENTAL RESCAN");
        w.WriteLine("  " + new string('-', 76));

        var (baseScan, recorded) = LastRecordedJournals();

        foreach (var volume in ntfs)
        {
            if (!UsnJournal.TryQuery(volume.Root, out var data, out var error))
            {
                w.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "  {0,-5} {1,-13} {2,-10} {3,-19} {4}",
                    volume.Letter,
                    error == Usn.ErrorJournalNotActive ? "off" : $"error {error}",
                    "", "", "no - the journal is not running"));

                w.WriteLine($"        enable it with:  fsutil usn createjournal m=32000000 a=8000000 {volume.Letter}");
                continue;
            }

            recorded.TryGetValue(volume.Letter, out var saved);

            w.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "  {0,-5} {1,-13} {2,-10} 0x{3:x12}      {4}",
                volume.Letter,
                "active",
                SizeFormat.Bytes(data.MaximumSize),
                data.NextUsn,
                Verdict(data, saved, baseScan)));
        }

        w.WriteLine();
    }

    /// <summary>What an incremental rescan of this volume would do, and why.</summary>
    private static string Verdict(UsnJournalData now, ScanVolumeRow? saved, long? baseScan)
    {
        if (!Elevation.IsElevated) return "no - reading the journal needs administrator rights";
        if (saved?.UsnJournalId is null || saved.NextUsn is null) return "no - no scan has recorded a position";

        if ((ulong)saved.UsnJournalId.Value != now.UsnJournalId)
            return "no - the journal was recreated since that scan";

        if (saved.NextUsn.Value < now.LowestValidUsn || saved.NextUsn.Value < now.FirstUsn)
            return "no - the journal no longer reaches that far back";

        var behind = now.NextUsn - saved.NextUsn.Value;
        return string.Create(CultureInfo.InvariantCulture,
            $"yes - from scan {baseScan}, {behind:N0} bytes of records behind");
    }

    /// <summary>
    /// The journal positions the newest scan wrote, by volume letter. Read from the
    /// database rather than from the snapshot: it is the same pair of numbers, and it does
    /// not cost opening a 25 MB tree to print one line (README section 11.1).
    /// </summary>
    private static (long? Scan, Dictionary<string, ScanVolumeRow> Volumes) LastRecordedJournals()
    {
        var empty = new Dictionary<string, ScanVolumeRow>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var catalog = ScanCatalog.TryOpen(out _);
            if (catalog is null) return (null, empty);

            var latest = catalog.Scans.List(limit: 1).FirstOrDefault();
            if (latest is null) return (null, empty);

            foreach (var row in catalog.Scans.Volumes(latest.Id)) empty[row.Letter] = row;
            return (latest.Id, empty);
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or DatabaseException)
        {
            // doctor's job is to report, not to fail. An unusable database has already been
            // said so a few lines above.
            return (null, empty);
        }
    }

    /// <summary>
    /// The state of the metadata store. Reported by doctor because "the database is
    /// locked" and "the database is from a newer build" are the two failures a user
    /// cannot otherwise diagnose (README section 13).
    /// </summary>
    private static void PrintDatabase(TextWriter w)
    {
        var file = new FileInfo(AppPaths.DatabasePath);
        var snapshots = SnapshotStore.List();
        var snapshotBytes = snapshots.Sum(s => s.Bytes);

        Field(w, "Snapshots", snapshots.Count == 0
            ? "none"
            : string.Format(CultureInfo.InvariantCulture, "{0} files, {1}", snapshots.Count, SizeFormat.Bytes(snapshotBytes)));

        if (!file.Exists)
        {
            Field(w, "Database", "absent  (created on first scan)");
            return;
        }

        Field(w, "Database", $"{SizeFormat.Bytes(file.Length)}   {AppPaths.DatabasePath}");

        using var catalog = ScanCatalog.TryOpen(out var error);
        if (catalog is null)
        {
            Field(w, "Database state", $"unusable: {error}");
            return;
        }

        var (imported, forgotten) = catalog.Reconcile();

        Field(w, "Schema version", catalog.Database.Version.ToString(CultureInfo.InvariantCulture)
            + (catalog.Database.Version == Database.SchemaVersion ? "  (current)" : "  (older than this build)"));
        Field(w, "Scans recorded", catalog.Scans.Count().ToString(CultureInfo.InvariantCulture));

        if (imported > 0 || forgotten > 0)
            Field(w, "Reconciled", $"{imported} snapshot(s) imported, {forgotten} row(s) without a snapshot");

        if (catalog.Database.RecoveredFromUncleanExit)
            Field(w, "Integrity check", catalog.Database.IntegrityResult ?? "not run");
    }

    private static string TerminalSize()
    {
        try
        {
            return $"{Console.WindowWidth}x{Console.WindowHeight}"
                 + (Console.WindowWidth < 80 || Console.WindowHeight < 24 ? "  (below the 80x24 minimum)" : "");
        }
        catch (IOException)
        {
            return "unavailable (not a console)";
        }
    }

    private static void Section(TextWriter w, string name) => w.WriteLine(name);

    private static void Field(TextWriter w, string name, string value) =>
        w.WriteLine($"  {name,-18} {value}");
}
