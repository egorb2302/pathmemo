using System.Globalization;
using System.Security.Principal;
using System.Text;
using PathMemo.Audit;

namespace PathMemo.Platform;

internal enum ScheduleKind
{
    Daily,
    Weekly,
}

/// <summary>What is registered with the Task Scheduler right now, as its own XML says.</summary>
internal sealed record ScheduleInfo
{
    internal required ScheduleKind? Kind { get; init; }
    internal TimeOnly Time { get; init; }
    internal DayOfWeek Day { get; init; } = DayOfWeek.Monday;
    internal bool Enabled { get; init; } = true;
    internal bool Elevated { get; init; }
    internal string Command { get; init; } = "";
    internal string Arguments { get; init; } = "";
    internal string? User { get; init; }

    internal string Describe() => Kind switch
    {
        ScheduleKind.Daily => string.Create(CultureInfo.InvariantCulture,
            $"every day at {Time:HH\\:mm}"),
        ScheduleKind.Weekly => string.Create(CultureInfo.InvariantCulture,
            $"every {Day} at {Time:HH\\:mm}"),
        _ => "on a schedule this build does not recognise",
    };
}

/// <summary>
/// Registers the scheduled scan that makes history worth keeping (README section 10.1).
/// </summary>
/// <remarks>
/// <para>
/// Through <c>schtasks.exe</c> and a task XML document rather than the <c>ITaskService</c>
/// COM API. The XML is the only way to set the four settings that matter - run only when
/// idle, start when available, not on batteries, below-normal priority - and it costs no
/// COM interop, which is one fewer thing to keep working in a trimmed single-file build
/// (README section 19.4).
/// </para>
/// <para>
/// The task runs as the calling user with <c>InteractiveToken</c>, so no password is asked
/// for and none is stored. Registered from an ordinary prompt the scan is unelevated and
/// therefore degraded; registered from an elevated one it asks for the highest available
/// token, which is what lets a nightly scan use the MFT and the change journal.
/// </para>
/// </remarks>
internal static class ScheduledScan
{
    internal const string TaskName = "pathmemo scan";

    /// <summary>What the task runs. Quiet, because nobody is watching at 03:00.</summary>
    internal static readonly string[] ScanArguments = ["scan", "--all-volumes", "--quiet"];

    /// <summary>
    /// The executable a task would be pointed at, or null when this process is not one that
    /// can be scheduled.
    /// </summary>
    /// <remarks>
    /// A build running under <c>dotnet PathMemo.dll</c> has <c>dotnet.exe</c> as its process
    /// path, and registering that would schedule the runtime with no arguments. Better to
    /// refuse and say so than to leave a task that fails every night at three.
    /// </remarks>
    internal static string? Executable
    {
        get
        {
            var path = Environment.ProcessPath;
            if (path is null) return null;

            var name = Path.GetFileNameWithoutExtension(path);
            return name.Equals("pathmemo", StringComparison.OrdinalIgnoreCase) ? path : null;
        }
    }

    internal static ScheduleInfo? Query(CancellationToken ct)
    {
        var result = ExternalTool.Run("schtasks.exe", ["/Query", "/TN", TaskName, "/XML", "ONE"], ct);

        // Exit code 1 with nothing on stdout is how schtasks says "no such task", in every
        // language. Parsing its message would be parsing a translation (README section 6.3).
        if (!result.Succeeded || result.StdOut.Trim().Length == 0) return null;

        return Parse(result.StdOut);
    }

    /// <summary>
    /// Reads the task XML back into what it means. Element names are the same in every
    /// language, which is why this is the form both written and read.
    /// </summary>
    /// <remarks>
    /// <para>
    /// By hand, not with <c>XDocument</c>. Pulling in <c>System.Private.Xml</c> to read nine
    /// elements cost <em>8.04 MB</em> of the trimmed single-file build - measured both ways,
    /// 33.61 MB with it against 25.57 MB without - which is a third of the whole application
    /// for one parse of forty lines. The same reasoning already keeps a JSON serializer out
    /// (README sections 18, 19.1).
    /// </para>
    /// <para>
    /// This is not a general XML reader and does not pretend to be. It reads a document this
    /// same file wrote, or the scheduler's normalisation of it, and anything else yields null
    /// - which is the only answer <c>schedule --status</c> has any use for.
    /// </para>
    /// </remarks>
    internal static ScheduleInfo? Parse(string xml)
    {
        if (!xml.Contains("<Task", StringComparison.Ordinal)) return null;

        // Blocks first, because element names repeat: <Enabled> lives in the trigger and
        // again in the settings, and only the second one means "switched off".
        var trigger = Block(xml, "CalendarTrigger");
        if (trigger is null) return null;

        var settings = Block(xml, "Settings") ?? "";
        var principal = Block(xml, "Principal") ?? "";
        var exec = Block(xml, "Exec") ?? "";
        var weekly = Block(trigger, "ScheduleByWeek");

        var kind = weekly is not null ? ScheduleKind.Weekly
                 : Block(trigger, "ScheduleByDay") is not null ? ScheduleKind.Daily
                 : (ScheduleKind?)null;

        var time = DateTime.TryParse(Value(trigger, "StartBoundary"), CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var at) ? TimeOnly.FromDateTime(at) : new TimeOnly(3, 0);

        return new ScheduleInfo
        {
            Kind = kind,
            Time = time,
            Day = Enum.TryParse<DayOfWeek>(FirstTagName(Block(weekly ?? "", "DaysOfWeek")),
                out var parsed) ? parsed : DayOfWeek.Monday,
            Enabled = Value(settings, "Enabled") != "false",
            Elevated = Value(principal, "RunLevel") == "HighestAvailable",
            Command = Value(exec, "Command") ?? "",
            Arguments = Value(exec, "Arguments") ?? "",
            User = Value(principal, "UserId"),
        };
    }

    /// <summary>Everything between an element's tags, markup and all, or null.</summary>
    private static string? Block(string xml, string name)
    {
        // The opening tag may carry attributes (<Principal id="Author">), so it ends at the
        // first '>' rather than at a known offset.
        var at = xml.IndexOf("<" + name, StringComparison.Ordinal);
        if (at < 0) return null;

        var open = xml.IndexOf('>', at);
        if (open < 0) return null;
        if (xml[open - 1] == '/') return "";            // <ScheduleByDay /> is present and empty

        var close = xml.IndexOf("</" + name + ">", open, StringComparison.Ordinal);
        return close < 0 ? null : xml[(open + 1)..close];
    }

    /// <summary>The text of a leaf element, unescaped, or null when it is not there.</summary>
    private static string? Value(string xml, string name) => Block(xml, name) is { } text
        ? text.Trim()
            .Replace("&lt;", "<", StringComparison.Ordinal)
            .Replace("&gt;", ">", StringComparison.Ordinal)
            .Replace("&quot;", "\"", StringComparison.Ordinal)
            .Replace("&apos;", "'", StringComparison.Ordinal)
            .Replace("&amp;", "&", StringComparison.Ordinal)   // last, or &amp;lt; would double-decode
        : null;

    /// <summary>The name of the first child tag, which is how a day of the week is written.</summary>
    private static string FirstTagName(string? xml)
    {
        if (xml is null) return "";

        var at = xml.IndexOf('<');
        if (at < 0) return "";

        var end = at + 1;
        while (end < xml.Length && char.IsAsciiLetter(xml[end])) end++;

        return xml[(at + 1)..end];
    }

    /// <summary>Registers or replaces the task. Returns the error text, or null on success.</summary>
    internal static string? Register(ScheduleKind kind, TimeOnly time, DayOfWeek day, CancellationToken ct)
    {
        if (Executable is not { } exe)
            return "a scheduled task can only be registered from pathmemo.exe itself";

        var xml = BuildXml(kind, time, day, exe, Environment.UserDomainName + @"\" + Environment.UserName,
            Elevation.IsElevated);

        // The scheduler insists on UTF-16 with a byte order mark, matching the declaration.
        var file = Path.Combine(Path.GetTempPath(),
            "pathmemo-task-" + Guid.NewGuid().ToString("N")[..8] + ".xml");

        try
        {
            File.WriteAllText(file, xml, new UnicodeEncoding(bigEndian: false, byteOrderMark: true));

            var result = ExternalTool.Run("schtasks.exe",
                ["/Create", "/TN", TaskName, "/XML", file, "/F"], ct);

            if (result.Succeeded) return null;

            var message = Trim(result.StdErr) ?? Trim(result.StdOut) ?? $"schtasks exited with {result.ExitCode}";
            return message;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ex.Message;
        }
        finally
        {
            try { File.Delete(file); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>Removes the task. Returns the error text, or null when it is gone or was never there.</summary>
    internal static string? Remove(CancellationToken ct)
    {
        var result = ExternalTool.Run("schtasks.exe", ["/Delete", "/TN", TaskName, "/F"], ct);
        if (result.Succeeded) return null;

        // Already absent is the state that was asked for, not a failure.
        return Query(ct) is null ? null : Trim(result.StdErr) ?? Trim(result.StdOut) ?? "schtasks refused to delete the task";
    }

    /// <summary>
    /// The task document. Written by hand rather than by a serializer, like every other
    /// structured output in this application (README section 18).
    /// </summary>
    /// <remarks>
    /// Several of these values are the schema's own defaults - <c>Priority</c> 7, which is
    /// BELOW_NORMAL, among them - and the scheduler omits those when it hands the task back.
    /// They are written anyway: a document that states what it wants does not change meaning
    /// when a default does.
    /// </remarks>
    internal static string BuildXml(
        ScheduleKind kind, TimeOnly time, DayOfWeek day, string exe, string user, bool elevated)
    {
        // A start boundary needs a date, and tomorrow is the first day the schedule can
        // actually fire; the scheduler takes it from there.
        var boundary = DateTime.Today.AddDays(1).Add(time.ToTimeSpan())
            .ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);

        var schedule = kind == ScheduleKind.Weekly
            ? $"""
                  <ScheduleByWeek>
                    <DaysOfWeek><{day} /></DaysOfWeek>
                    <WeeksInterval>1</WeeksInterval>
                  </ScheduleByWeek>
              """
            : """
                  <ScheduleByDay>
                    <DaysInterval>1</DaysInterval>
                  </ScheduleByDay>
              """;

        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Author>pathmemo</Author>
                <Description>Disk space scan by pathmemo. Remove with: pathmemo schedule --off</Description>
              </RegistrationInfo>
              <Triggers>
                <CalendarTrigger>
                  <StartBoundary>{boundary}</StartBoundary>
                  <Enabled>true</Enabled>
            {schedule}
                </CalendarTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{Escape(user)}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>{(elevated ? "HighestAvailable" : "LeastPrivilege")}</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>true</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>true</StopIfGoingOnBatteries>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>true</StartWhenAvailable>
                <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
                <IdleSettings>
                  <Duration>PT10M</Duration>
                  <WaitTimeout>PT1H</WaitTimeout>
                  <StopOnIdleEnd>false</StopOnIdleEnd>
                  <RestartOnIdle>false</RestartOnIdle>
                </IdleSettings>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <Hidden>false</Hidden>
                <RunOnlyIfIdle>true</RunOnlyIfIdle>
                <WakeToRun>false</WakeToRun>
                <ExecutionTimeLimit>PT1H</ExecutionTimeLimit>
                <Priority>7</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{Escape(exe)}</Command>
                  <Arguments>{Escape(string.Join(' ', ScanArguments))}</Arguments>
                </Exec>
              </Actions>
            </Task>
            """;
    }

    /// <summary>
    /// Escapes the two values that come from outside: the executable path and the account
    /// name. A path may legitimately contain <c>&amp;</c>, and a document that will not
    /// parse is how a nightly scan silently never runs.
    /// </summary>
    private static string Escape(string value) => value
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal)
        .Replace("\"", "&quot;", StringComparison.Ordinal);

    /// <summary>
    /// An account name for what the task XML holds, which is a SID.
    /// </summary>
    /// <remarks>
    /// The scheduler normalises whatever account name it is given to a SID and hands that
    /// back, so a status line would otherwise read "As: S-1-5-21-2390494320-..." - true,
    /// and no use to anyone. A SID that no longer resolves is printed as it stands.
    /// </remarks>
    internal static string AccountName(string? userId)
    {
        if (userId is null or { Length: 0 }) return "";
        if (!userId.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase)) return userId;

        try
        {
            return new SecurityIdentifier(userId).Translate(typeof(NTAccount)).Value;
        }
        catch (Exception ex) when (ex is ArgumentException or IdentityNotMappedException
                                     or SystemException)
        {
            return userId;
        }
    }

    private static string? Trim(string text)
    {
        var value = text.Trim();
        return value.Length == 0 ? null : value.ReplaceLineEndings(" ").Trim();
    }
}
