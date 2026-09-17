using static PathMemo.Audit.Probes.ProbeHelpers;

namespace PathMemo.Audit.Probes;

/// <summary>Shared shape for "size these directories, they are safe to clear" probes.</summary>
internal abstract class DirectorySetProbe : IAuditProbe
{
    public abstract string Id { get; }
    public abstract string Title { get; }

    protected abstract IEnumerable<string> Directories(AuditContext context);
    protected abstract string Explain(Measured measured, IReadOnlyList<string> present);
    protected abstract IReadOnlyList<Remedy> Remedies(AuditContext context, IReadOnlyList<string> present);

    protected virtual Risk Risk => Risk.Safe;
    protected virtual Recoverability Recoverability => Recoverability.Instant;
    protected virtual string Absent => "Nothing found.";

    /// <summary>Below this the finding is noise and is left out of the report.</summary>
    protected virtual long MinimumBytes => 16L * 1024 * 1024;

    public virtual IEnumerable<AuditFinding> Run(AuditContext context)
    {
        var (measured, present) = MeasureAll(Directories(context), context.Cancellation);

        if (present.Count == 0)
        {
            yield return AuditFinding.NotApplicable(Id, Title, Absent);
            yield break;
        }

        // Unreadable and empty is different from empty: the first is "needs elevation".
        if (measured.Files == 0 && measured.Errors > 0 && !context.Elevated)
        {
            yield return new AuditFinding
            {
                Id = Id, Title = Title, Status = FindingStatus.NeedsElevation,
                Volume = AuditContext.Letter(present[0]),
                Explanation = Explain(measured, present),
                Note = "run as administrator to measure",
                Paths = present,
            };
            yield break;
        }

        if (measured.Allocated < MinimumBytes && !measured.Incomplete) yield break;

        yield return new AuditFinding
        {
            Id = Id,
            Title = Title,
            Status = FindingStatus.Measured,
            Volume = AuditContext.Letter(present[0]),
            UsedBytes = measured.Allocated,
            ReclaimableBytes = measured.Allocated,
            Risk = Risk,
            Recoverability = Recoverability,
            Explanation = Explain(measured, present),
            Remedies = Remedies(context, present),
            Paths = present,
            Note = IncompleteNote(measured, context.Elevated),
        };
    }
}

internal sealed class WindowsUpdateCacheProbe : DirectorySetProbe
{
    public override string Id => "wu.softwaredistribution";
    public override string Title => "Windows Update cache";

    protected override IEnumerable<string> Directories(AuditContext context) =>
        [Path.Combine(context.WindowsDirectory, "SoftwareDistribution", "Download")];

    protected override string Explain(Measured m, IReadOnlyList<string> present) =>
        $"{Size(m.Allocated)} of downloaded update packages in {Files(m.Files)}. Installed updates do not " +
        "need them; anything still pending is downloaded again.";

    protected override IReadOnlyList<Remedy> Remedies(AuditContext context, IReadOnlyList<string> present) =>
    [
        Elevated("net stop wuauserv && net stop bits && rd /s /q \"" + present[0] + "\" && net start bits && net start wuauserv",
            "Stops Windows Update for the few seconds it takes"),
        Settings("Settings > System > Storage > Temporary files > Windows Update Cleanup"),
    ];
}

internal sealed class DeliveryOptimizationProbe : DirectorySetProbe
{
    public override string Id => "wu.delivery-optimization";
    public override string Title => "Delivery Optimization cache";

    protected override IEnumerable<string> Directories(AuditContext context) =>
    [
        Path.Combine(context.WindowsDirectory, "SoftwareDistribution", "DeliveryOptimization"),
        Path.Combine(context.ProgramData, "Microsoft", "Windows", "DeliveryOptimization", "Cache"),
        Path.Combine(context.WindowsDirectory, "ServiceProfiles", "NetworkService", "AppData", "Local", "Microsoft", "Windows", "DeliveryOptimization"),
    ];

    protected override string Absent => "Delivery Optimization has no cache on this machine.";

    protected override string Explain(Measured m, IReadOnlyList<string> present) =>
        $"{Size(m.Allocated)} of update content kept to share with other PCs. Safe to drop; it is " +
        "re-fetched only if another machine asks for it.";

    protected override IReadOnlyList<Remedy> Remedies(AuditContext context, IReadOnlyList<string> present) =>
    [
        Elevated("powershell -NoProfile -Command Delete-DeliveryOptimizationCache -Force"),
        Settings("Settings > System > Storage > Temporary files > Delivery Optimization Files"),
    ];
}

internal sealed class LogsProbe : DirectorySetProbe
{
    public override string Id => "logs.cbs-panther";
    public override string Title => "Windows logs";

    protected override IEnumerable<string> Directories(AuditContext context) =>
    [
        Path.Combine(context.WindowsDirectory, "Logs"),
        Path.Combine(context.WindowsDirectory, "Panther"),
        Path.Combine(context.WindowsDirectory, "Temp", "CBS"),
    ];

    protected override string Explain(Measured m, IReadOnlyList<string> present) =>
        $"{Size(m.Allocated)} of servicing, setup and update logs (CBS, DISM, Panther) in {Files(m.Files)}. " +
        "CBS.log in particular can grow to gigabytes. Useful only when diagnosing a failed update.";

    protected override Recoverability Recoverability => Recoverability.Irreversible;

    protected override IReadOnlyList<Remedy> Remedies(AuditContext context, IReadOnlyList<string> present) =>
    [
        Delete("Delete the contents of " + string.Join(", ", present),
            "Files in use by the servicing stack are skipped automatically"),
        Elevated("net stop TrustedInstaller && del /q \"" + Path.Combine(context.WindowsDirectory, "Logs", "CBS", "*.log") + "\" && del /q \"" + Path.Combine(context.WindowsDirectory, "Logs", "CBS", "*.cab") + "\"",
            "Stops the Windows Modules Installer briefly so CBS.log can be removed"),
    ];
}

internal sealed class TempProbe : DirectorySetProbe
{
    public override string Id => "store.temp";
    public override string Title => "Temp directories";

    protected override IEnumerable<string> Directories(AuditContext context) =>
    [
        Path.Combine(context.WindowsDirectory, "Temp"),
        Path.GetTempPath(),
        Path.Combine(context.LocalAppData, "Temp"),
    ];

    protected override string Explain(Measured m, IReadOnlyList<string> present) =>
        $"{Size(m.Allocated)} in {Files(m.Files)} across the system and user temp directories. " +
        "Installers and applications leave these behind; anything a running program holds open stays.";

    protected override IReadOnlyList<Remedy> Remedies(AuditContext context, IReadOnlyList<string> present) =>
    [
        Delete("Delete the contents of " + string.Join(", ", present), "Files in use are skipped"),
        Command("powershell -NoProfile -Command \"Remove-Item -Recurse -Force -ErrorAction SilentlyContinue ($env:TEMP + '\\*')\"",
            "User temp only; the system temp needs an elevated prompt"),
        Settings("Settings > System > Storage > Temporary files"),
    ];
}

internal sealed class DumpsProbe : DirectorySetProbe
{
    public override string Id => "dumps";
    public override string Title => "Crash dumps";

    protected override IEnumerable<string> Directories(AuditContext context) =>
    [
        Path.Combine(context.WindowsDirectory, "Minidump"),
        Path.Combine(context.WindowsDirectory, "LiveKernelReports"),
        Path.Combine(context.LocalAppData, "CrashDumps"),
        Path.Combine(context.LocalAppData, "Microsoft", "Windows", "WER"),
        Path.Combine(context.ProgramData, "Microsoft", "Windows", "WER"),
    ];

    protected override Recoverability Recoverability => Recoverability.Irreversible;

    // The full kernel dump is a single file beside the directories.
    public override IEnumerable<AuditFinding> Run(AuditContext context)
    {
        var memoryDump = Path.Combine(context.WindowsDirectory, "MEMORY.DMP");
        var dump = DirectoryMeasure.File_(memoryDump);
        var findings = base.Run(context).ToList();

        if (dump.Files == 0) return findings;

        var measured = findings.FindIndex(f => f.Status == FindingStatus.Measured);
        if (measured >= 0)
        {
            var finding = findings[measured];
            var used = (finding.UsedBytes ?? 0) + dump.Allocated;
            findings[measured] = finding with
            {
                UsedBytes = used,
                ReclaimableBytes = used,
                Explanation = $"MEMORY.DMP alone is {Size(dump.Allocated)}. " + finding.Explanation,
                Paths = [memoryDump, .. finding.Paths],
            };
            return findings;
        }

        // Directories absent or unreadable, but the big file is right there.
        return
        [
            new AuditFinding
            {
                Id = Id, Title = Title, Status = FindingStatus.Measured,
                Volume = AuditContext.Letter(memoryDump),
                UsedBytes = dump.Allocated, ReclaimableBytes = dump.Allocated,
                Risk = Risk.Safe, Recoverability = Recoverability.Irreversible,
                Explanation = $"MEMORY.DMP is {Size(dump.Allocated)}: the last kernel crash, kept for debugging.",
                Remedies = Remedies(context, [memoryDump]),
                Paths = [memoryDump],
            },
        ];
    }

    protected override string Explain(Measured m, IReadOnlyList<string> present) =>
        $"{Size(m.Allocated)} of crash dumps and error reports in {Files(m.Files)}. They exist to be sent " +
        "to a developer; if nobody is going to look at them, they are just space.";

    protected override IReadOnlyList<Remedy> Remedies(AuditContext context, IReadOnlyList<string> present) =>
    [
        Delete("Delete " + string.Join(", ", present)),
        Settings("Settings > System > Storage > Temporary files > System error memory dump files"),
        Settings("System Properties > Advanced > Startup and Recovery > Write debugging information: (none)"),
    ];
}

internal sealed class BrowserCacheProbe : DirectorySetProbe
{
    public override string Id => "browser.caches";
    public override string Title => "Browser caches";

    protected override IEnumerable<string> Directories(AuditContext context)
    {
        var local = context.LocalAppData;

        // Chromium profiles: Default, Profile 1, Profile 2 ... each with several caches.
        foreach (var userData in new[]
                 {
                     Path.Combine(local, "Google", "Chrome", "User Data"),
                     Path.Combine(local, "Microsoft", "Edge", "User Data"),
                     Path.Combine(local, "BraveSoftware", "Brave-Browser", "User Data"),
                     Path.Combine(local, "Vivaldi", "User Data"),
                     Path.Combine(local, "Opera Software", "Opera Stable"),
                 })
        {
            foreach (var profile in Profiles(userData))
            {
                yield return Path.Combine(profile, "Cache");
                yield return Path.Combine(profile, "Code Cache");
                yield return Path.Combine(profile, "GPUCache");
                yield return Path.Combine(profile, "Service Worker", "CacheStorage");
                yield return Path.Combine(profile, "Service Worker", "ScriptCache");
            }

            yield return Path.Combine(userData, "ShaderCache");
            yield return Path.Combine(userData, "GrShaderCache");
        }

        var firefox = Path.Combine(local, "Mozilla", "Firefox", "Profiles");
        if (Directory.Exists(firefox))
        {
            foreach (var profile in SafeDirectories(firefox))
            {
                yield return Path.Combine(profile, "cache2");
                yield return Path.Combine(profile, "startupCache");
            }
        }
    }

    private static IEnumerable<string> Profiles(string userData)
    {
        if (!Directory.Exists(userData)) yield break;

        foreach (var directory in SafeDirectories(userData))
        {
            var name = Path.GetFileName(directory);
            if (name.Equals("Default", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("Profile ", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Guest Profile", StringComparison.OrdinalIgnoreCase))
                yield return directory;
        }
    }

    private static IEnumerable<string> SafeDirectories(string directory)
    {
        try { return Directory.EnumerateDirectories(directory).ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    protected override string Absent => "No Chrome, Edge, Brave, Vivaldi, Opera or Firefox profile found.";

    protected override string Explain(Measured m, IReadOnlyList<string> present) =>
        $"{Size(m.Allocated)} of page, script and shader caches across {present.Count} cache director" +
        $"{(present.Count == 1 ? "y" : "ies")}. Browsers refill them as you browse; clearing costs a few slower page loads.";

    protected override IReadOnlyList<Remedy> Remedies(AuditContext context, IReadOnlyList<string> present) =>
    [
        Manual("In each browser: Settings > Privacy > Clear browsing data > Cached images and files"),
        Delete("Delete the cache directories with the browser closed", "A running browser recreates and relocks them"),
    ];
}

internal sealed class DefenderHistoryProbe : DirectorySetProbe
{
    public override string Id => "defender.history";
    public override string Title => "Defender scan history";

    protected override IEnumerable<string> Directories(AuditContext context) =>
        [Path.Combine(context.ProgramData, "Microsoft", "Windows Defender", "Scans", "History")];

    protected override string Absent => "Microsoft Defender keeps no scan history here.";

    protected override string Explain(Measured m, IReadOnlyList<string> present) =>
        m.Files == 0
            ? "Defender's scan history is readable only by an elevated process."
            : $"{Size(m.Allocated)} of detection history and quarantine metadata in {Files(m.Files)}. " +
              "Defender rewrites it as it scans.";

    protected override IReadOnlyList<Remedy> Remedies(AuditContext context, IReadOnlyList<string> present) =>
    [
        Elevated("rd /s /q \"" + Path.Combine(present[0], "Service") + "\"",
            "Clears detection history; quarantined items under Quarantine are left alone"),
    ];
}
