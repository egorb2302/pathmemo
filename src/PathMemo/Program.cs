using System.Text;
using PathMemo.Cli;
using PathMemo.Cli.Commands;
using PathMemo.Cli.Interactive;
using PathMemo.Config;
using PathMemo.Platform;
using PathMemo.Tui;

namespace PathMemo;

internal static class Program
{
    internal static async Task<int> Main(string[] args)
    {
        // Long paths without the \\?\ prefix. The manifest declares longPathAware;
        // this switch turns off BCL-side MAX_PATH validation (README section 15.5).
        AppContext.SetSwitch("Switch.System.IO.UseLegacyPathHandling", false);
        AppContext.SetSwitch("Switch.System.IO.BlockLongPaths", false);

        ConfigureConsole();

        // --data-dir is global, so it is taken out of the argument list before the verb
        // sees it. Ignored while elevated, where an arbitrary store path would be a
        // write primitive running as administrator (README section 12.1).
        try
        {
            args = TakeDataDirectory(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"pathmemo: {ex.Message}");
            return ExitCode.Usage;
        }

        var verb = args.Length > 0 ? args[0] : "";
        var rest = args.Length > 1 ? args[1..] : [];

        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            // First Ctrl+C asks for a graceful stop so a partial result can still be kept;
            // a second is left to the runtime and kills the process (README section 4.8).
            if (cancellation.IsCancellationRequested) return;
            e.Cancel = true;
            cancellation.Cancel();

            // Silent while the TUI owns the terminal: a line of stderr in the middle of a
            // frame is exactly what README section 14.6 forbids, and the loop is about to
            // exit and restore the screen anyway.
            if (TuiHost.Active) return;

            Console.Error.WriteLine();
            Console.Error.WriteLine("stopping - press Ctrl+C again to abort immediately");
        };

        try
        {
            return verb switch
            {
                "scan" => await ScanCommand.RunAsync(ParseScan(rest), cancellation.Token),
                "tree" => TreeCommand.Run(ParseTree(rest)),
                "top" => TopCommand.Run(ParseTop(rest)),
                "audit" => AuditCommand.Run(ParseAudit(rest), cancellation.Token),
                "reclaim" => ReclaimCommand.Run(ParseReclaim(rest), cancellation.Token),
                "dupes" or "duplicates" => DupesCommand.Run(ParseDupes(rest), cancellation.Token),
                "rm" or "delete" => RmCommand.Run(ParseRm(rest), cancellation.Token),
                "restore" => QuarantineCommands.Restore(ParseOpId(rest)),
                "purge" => QuarantineCommands.Purge(ParsePurge(rest), cancellation.Token),
                "ops" => ParseOps(rest),
                "history" => HistoryCommand.Run(ParseHistory(rest)),
                "diff" => DiffCommand.Run(ParseDiff(rest)),
                "doctor" => DoctorCommand.Run(),
                "status" => StatusCommand.Run(),
                "--version" or "-V" => PrintVersion(),
                // No arguments: an interactive session if the window is ours to keep open
                // (double-clicked from Explorer), otherwise plain help for a shell.
                "--interactive" or "-i" => await InteractiveAsync(cancellation.Token),
                "" when ConsoleOwnership.OwnsTheWindow => await InteractiveAsync(cancellation.Token),
                // A pipe gets the summary rather than the help text: something read this,
                // and "here is how to use me" answers nothing (README section 14.5).
                "" when Console.IsOutputRedirected => StatusCommand.Run(),
                "" or "help" or "--help" or "-h" or "/?" => PrintUsage(),
                _ => UnknownVerb(verb),
            };
        }
        catch (OperationCanceledException)
        {
            return Fatal("cancelled", ExitCode.Cancelled);
        }
        catch (ArgumentException ex)
        {
            return Fatal($"pathmemo: {ex.Message}", ExitCode.Usage);
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex)
        {
            return Fatal($"pathmemo: {ex.Message}",
                ex.SqliteErrorCode is 5 or 6 ? ExitCode.Locked : ExitCode.Failure);
        }
        catch (Storage.DatabaseException ex)
        {
            // Exit code 8 exists so a script can tell "another pathmemo is running"
            // from a real failure (README section 13.5).
            return Fatal($"pathmemo: {ex.Message}", ex.Locked ? ExitCode.Locked : ExitCode.Failure);
        }
        catch (Exception ex)
        {
            return Fatal($"pathmemo: {ex.Message}", ExitCode.Failure);
        }
        finally
        {
            Console.Out.Flush();
        }
    }

    /// <summary>
    /// Reports a failure and, when the console window belongs to us, holds it open.
    /// </summary>
    /// <remarks>
    /// A window Explorer created for a double-click dies with the process, taking the
    /// message with it - the user sees a flash and nothing else, which is the worst
    /// possible way to learn that the database is locked (README section 2.1).
    /// </remarks>
    private static int Fatal(string message, int code)
    {
        Console.Error.WriteLine(message);

        if (ConsoleOwnership.OwnsTheWindow) Launcher.Pause();
        return code;
    }

    /// <summary>
    /// The interactive session: the real screens when the terminal can host them, and the
    /// line-based menu when it cannot (README sections 2.1, 14.5). The fallback is not a
    /// formality - a host without VT processing would render the frame as literal escape
    /// sequences, which is worse than no TUI at all.
    /// </summary>
    private static async Task<int> InteractiveAsync(CancellationToken ct) =>
        TuiHost.TryRun(ct, out var code) ? code : await Launcher.RunAsync(ct);

    private static void ConfigureConsole()
    {
        try
        {
            var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

            if (Console.IsOutputRedirected)
            {
                // Redirected output ignores the console code page entirely, so replace the
                // writer: piping a file listing must not mangle non-ASCII file names.
                Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), utf8) { AutoFlush = true });
            }
            else
            {
                Console.OutputEncoding = Encoding.UTF8;
            }

            // Same for stderr, which carries warnings and paths of its own when a scan
            // runs inside a script.
            if (Console.IsErrorRedirected)
                Console.SetError(new StreamWriter(Console.OpenStandardError(), utf8) { AutoFlush = true });

            if (!Console.IsInputRedirected) Console.InputEncoding = Encoding.UTF8;
        }
        catch (IOException)
        {
            // No console attached. Nothing to configure.
        }
        catch (PlatformNotSupportedException)
        {
            // Some hosts refuse to change the console encoding; ASCII output still works.
        }
    }

    /// <summary>
    /// Removes <c>--data-dir &lt;path&gt;</c> from the arguments and applies it, so every
    /// command shares one store location without parsing the option itself.
    /// </summary>
    private static string[] TakeDataDirectory(string[] args)
    {
        var kept = new List<string>(args.Length);

        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] != "--data-dir")
            {
                kept.Add(args[i]);
                continue;
            }

            if (i + 1 >= args.Length) throw new ArgumentException("'--data-dir' needs a path");
            Config.AppPaths.Redirect(args[++i]);
        }

        return [.. kept];
    }

    private static ScanOptions ParseScan(string[] args)
    {
        var roots = new List<string>();
        var options = new ScanOptions();

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--top": options = options with { Top = ArgParse.Count(ArgParse.Value(args, ref i)) }; break;
                case "--parallelism": options = options with { Parallelism = ArgParse.Count(ArgParse.Value(args, ref i)) }; break;
                case "--note": options = options with { Note = ArgParse.Value(args, ref i) }; break;
                case "--quiet" or "-q": options = options with { Quiet = true }; break;
                case "--no-save": options = options with { Save = false }; break;
                case "--all-volumes": break;   // the default when no path is given
                case "--scanner": options = options with { Scanner = ParseScanner(ArgParse.Value(args, ref i)) }; break;
                case "--pause": options = options with { Pause = true }; break;
                case "--no-elevate": options = options with { NoElevate = true }; break;
                case "--format": options = options with { Format = ParseFormat(ArgParse.Value(args, ref i)) }; break;
                case "--output" or "-o": options = options with { Output = ArgParse.Value(args, ref i) }; break;
                case "--json": options = options with { Format = Cli.Output.ScanFormat.Json }; break;
                default: roots.Add(Positional(args[i])); break;
            }
        }

        // If the user takes the offer to restart elevated, the new copy repeats this
        // exact scan and waits before its console closes.
        var relaunch = new List<string> { "scan" };
        relaunch.AddRange(args);
        if (!relaunch.Contains("--pause")) relaunch.Add("--pause");

        return options with { Roots = roots, RelaunchArguments = relaunch };
    }

    private static Scanning.ScannerKind ParseScanner(string value) => value.ToLowerInvariant() switch
    {
        "mft" => Scanning.ScannerKind.Mft,
        "walk" => Scanning.ScannerKind.Walk,
        _ => throw new ArgumentException($"--scanner must be mft or walk, not '{value}'"),
    };

    private static Cli.Output.ScanFormat ParseFormat(string value) => value.ToLowerInvariant() switch
    {
        "console" or "text" => Cli.Output.ScanFormat.Console,
        "json" => Cli.Output.ScanFormat.Json,
        "csv" => Cli.Output.ScanFormat.Csv,
        _ => throw new ArgumentException($"--format must be console, json or csv, not '{value}'"),
    };

    private static HistoryOptions ParseHistory(string[] args)
    {
        var options = new HistoryOptions();

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--limit": options = options with { Limit = ArgParse.Count(ArgParse.Value(args, ref i)) }; break;
                case "--since": options = options with { Since = ArgParse.Since(ArgParse.Value(args, ref i)) }; break;
                case "--json": options = options with { Json = true }; break;
                case "--categories": options = options with { Categories = true, Json = true }; break;
                default: throw new ArgumentException($"unexpected argument '{args[i]}'");
            }
        }

        return options;
    }

    private static DiffOptions ParseDiff(string[] args)
    {
        var options = new DiffOptions();
        var ids = new List<long>(2);

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--limit": options = options with { Limit = ArgParse.Count(ArgParse.Value(args, ref i)) }; break;
                case "--min": options = options with { MinBytes = ArgParse.Size(ArgParse.Value(args, ref i)) }; break;
                case "--size": options = options with { SizeMode = ArgParse.Mode(ArgParse.Value(args, ref i)) }; break;
                case "--json": options = options with { Json = true }; break;
                default:
                    ids.Add(ArgParse.Id(Positional(args[i])));
                    if (ids.Count > 2) throw new ArgumentException("diff takes at most two scan ids");
                    break;
            }
        }

        // One id means "that scan against the newest", none means "the last two".
        return ids.Count switch
        {
            2 => options with { BeforeId = ids[0], AfterId = ids[1] },
            1 => options with { BeforeId = ids[0] },
            _ => options,
        };
    }

    private static TreeOptions ParseTree(string[] args)
    {
        var options = new TreeOptions();

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--scan": options = options with { ScanId = ArgParse.Id(ArgParse.Value(args, ref i)) }; break;
                case "--limit": options = options with { Limit = ArgParse.Count(ArgParse.Value(args, ref i)) }; break;
                case "--size": options = options with { SizeMode = ArgParse.Mode(ArgParse.Value(args, ref i)) }; break;
                default: options = options with { Path = Positional(args[i]) }; break;
            }
        }

        return options;
    }

    private static TopOptions ParseTop(string[] args)
    {
        var options = new TopOptions();

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--scan": options = options with { ScanId = ArgParse.Id(ArgParse.Value(args, ref i)) }; break;
                case "--dirs" or "--directories": options = options with { Directories = true }; break;
                case "--files": options = options with { Directories = false }; break;
                case "--limit": options = options with { Limit = ArgParse.Count(ArgParse.Value(args, ref i)) }; break;
                case "--min": options = options with { MinBytes = ArgParse.Size(ArgParse.Value(args, ref i)) }; break;
                case "--under": options = options with { Under = ArgParse.Value(args, ref i) }; break;
                case "--ext": options = options with { Extensions = ArgParse.Extensions(ArgParse.Value(args, ref i)) }; break;
                case "--older-than": options = options with { OlderThan = ArgParse.Duration(ArgParse.Value(args, ref i)) }; break;
                case "--newer-than": options = options with { NewerThan = ArgParse.Duration(ArgParse.Value(args, ref i)) }; break;
                case "--paths-only": options = options with { PathsOnly = true }; break;
                case "--size": options = options with { SizeMode = ArgParse.Mode(ArgParse.Value(args, ref i)) }; break;
                default: throw new ArgumentException($"unexpected argument '{args[i]}'");
            }
        }

        return options;
    }

    private static RmOptions ParseRm(string[] args)
    {
        var paths = new List<string>();
        var options = new RmOptions();

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--mode": options = options with { Mode = ParseMode(ArgParse.Value(args, ref i)) }; break;
                case "--dry-run" or "-n": options = options with { DryRun = true }; break;
                case "--from-stdin": options = options with { FromStdin = true }; break;
                case "--reason": options = options with { Reason = ArgParse.Value(args, ref i) }; break;
                case "--confirm-token": options = options with { ConfirmToken = ArgParse.Value(args, ref i) }; break;
                case "--scan": options = options with { ScanId = ArgParse.Id(ArgParse.Value(args, ref i)) }; break;
                case "--yes" or "-y": options = options with { Yes = true }; break;
                case "--force": options = options with { Force = true }; break;
                case "--json": options = options with { Json = true }; break;
                case "--permanent": options = options with { Mode = Deletion.DeleteMode.Permanent }; break;
                default: paths.Add(Positional(args[i])); break;
            }
        }

        return options with { Paths = paths };
    }

    private static ReclaimOptions ParseReclaim(string[] args)
    {
        var options = new ReclaimOptions();

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--scan": options = options with { ScanId = ArgParse.Id(ArgParse.Value(args, ref i)) }; break;
                case "--risk": options = options with { Risk = ParseRisk(ArgParse.Value(args, ref i)) }; break;
                case "--rule": options = options with { RuleId = ArgParse.Value(args, ref i) }; break;
                case "--min": options = options with { MinBytes = ArgParse.Size(ArgParse.Value(args, ref i)) }; break;
                case "--limit": options = options with { Limit = ArgParse.Count(ArgParse.Value(args, ref i)) }; break;
                case "--list" or "--list-rules": options = options with { ListRules = true }; break;
                case "--json": options = options with { Json = true }; break;
                case "--dry-run" or "-n": options = options with { DryRun = true }; break;
                case "--apply": options = options with { Apply = true }; break;
                case "--mode": options = options with { Mode = ParseMode(ArgParse.Value(args, ref i)) }; break;
                case "--permanent": options = options with { Mode = Deletion.DeleteMode.Permanent }; break;
                case "--yes" or "-y": options = options with { Yes = true }; break;
                case "--force": options = options with { Force = true }; break;
                case "--keep": options = options with { Keep = ArgParse.Value(args, ref i) }; break;
                case "--disable": options = options with { Disable = ArgParse.Value(args, ref i) }; break;
                case "--enable": options = options with { Enable = ArgParse.Value(args, ref i) }; break;
                default: throw new ArgumentException($"unexpected argument '{args[i]}'");
            }
        }

        return options;
    }

    private static DupesOptions ParseDupes(string[] args)
    {
        var options = new DupesOptions();

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--scan": options = options with { ScanId = ArgParse.Id(ArgParse.Value(args, ref i)) }; break;
                case "--min": options = options with { MinBytes = ArgParse.Size(ArgParse.Value(args, ref i)) }; break;
                case "--max": options = options with { MaxBytes = ArgParse.Size(ArgParse.Value(args, ref i)) }; break;
                case "--under": options = options with { Under = ArgParse.Value(args, ref i) }; break;
                case "--ext": options = options with { Extensions = ArgParse.Extensions(ArgParse.Value(args, ref i)) }; break;
                case "--hash": options = options with { Algorithm = ParseHash(ArgParse.Value(args, ref i)) }; break;
                case "--limit": options = options with { Limit = ArgParse.Count(ArgParse.Value(args, ref i)) }; break;
                case "--group": options = options with { Group = ArgParse.Count(ArgParse.Value(args, ref i)) }; break;
                case "--all": options = options with { All = true }; break;
                case "--paths-only": options = options with { PathsOnly = true }; break;
                case "--cached": options = options with { Cached = true }; break;
                case "--estimate": options = options with { Estimate = true }; break;
                case "--no-verify": options = options with { NoVerify = true }; break;
                case "--no-cache": options = options with { NoCache = true }; break;
                case "--same-volume": options = options with { SameVolume = true }; break;
                case "--json": options = options with { Json = true }; break;
                case "--dry-run" or "-n": options = options with { DryRun = true }; break;
                case "--apply": options = options with { Apply = true }; break;
                case "--mode": options = options with { Mode = ParseMode(ArgParse.Value(args, ref i)) }; break;
                case "--permanent": options = options with { Mode = Deletion.DeleteMode.Permanent }; break;
                case "--yes" or "-y": options = options with { Yes = true }; break;
                case "--force": options = options with { Force = true }; break;
                default: throw new ArgumentException($"unexpected argument '{args[i]}'");
            }
        }

        return options;
    }

    private static Duplicates.HashKind ParseHash(string value) =>
        Duplicates.HashKinds.TryParse(value, out var kind)
            ? kind
            : throw new ArgumentException($"--hash must be xxh128 or sha256, not '{value}'");

    private static Audit.Risk ParseRisk(string value) =>
        Analysis.ReclaimNames.TryRisk(value, out var risk)
            ? risk
            : throw new ArgumentException($"--risk must be safe, caution or danger, not '{value}'");

    private static Deletion.DeleteMode ParseMode(string value) =>
        Deletion.DeleteModes.TryParse(value, out var mode)
            ? mode
            : throw new ArgumentException($"--mode must be quarantine, recycle or permanent, not '{value}'");

    private static long ParseOpId(string[] args)
    {
        if (args.Length == 0) throw new ArgumentException("restore needs an operation id (see 'pathmemo ops')");
        if (args.Length > 1) throw new ArgumentException("restore takes one operation id");

        // Both "118" and "op-000118" are what a person reads off the screen.
        var text = args[0].StartsWith("op-", StringComparison.OrdinalIgnoreCase) ? args[0][3..] : args[0];
        return ArgParse.Id(text.TrimStart('0').Length == 0 ? "0" : text.TrimStart('0'));
    }

    private static PurgeOptions ParsePurge(string[] args)
    {
        var options = new PurgeOptions();

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--expired": options = options with { Expired = true }; break;
                case "--all": options = options with { All = true }; break;
                case "--bin" or "--recycle-bin": options = options with { Bin = true }; break;
                case "--yes" or "-y": options = options with { Yes = true }; break;
                default: options = options with { OpId = ParseOpId([args[i]]) }; break;
            }
        }

        return options;
    }

    private static int ParseOps(string[] args)
    {
        long? id = null;
        var limit = 20;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--limit": limit = ArgParse.Count(ArgParse.Value(args, ref i)); break;
                default: id = ParseOpId([args[i]]); break;
            }
        }

        return QuarantineCommands.Ops(id, limit);
    }

    private static AuditOptions ParseAudit(string[] args)
    {
        var options = new AuditOptions();

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--id": options = options with { Id = ArgParse.Value(args, ref i) }; break;
                case "--json": options = options with { Json = true }; break;
                case "--quiet" or "-q": options = options with { Quiet = true }; break;
                case "--copy": options = options with { Copy = true }; break;
                case "--copy-index": options = options with { Copy = true, CopyIndex = ArgParse.Count(ArgParse.Value(args, ref i)) }; break;
                case "--apply": options = options with { Apply = ArgParse.Value(args, ref i) }; break;
                case "--yes" or "-y": options = options with { Yes = true }; break;
                default: throw new ArgumentException($"unexpected argument '{args[i]}'");
            }
        }

        if (options.Copy && options.Id is null)
            throw new ArgumentException("--copy needs --id <finding>");

        return options;
    }

    private static string Positional(string arg) =>
        arg.StartsWith('-') ? throw new ArgumentException($"unknown option '{arg}'") : arg;

    private static int PrintVersion()
    {
        Console.WriteLine(AppInfo.Version);
        return ExitCode.Ok;
    }

    private static int UnknownVerb(string verb)
    {
        Console.Error.WriteLine($"pathmemo: unknown command '{verb}'");
        Console.Error.WriteLine("Run 'pathmemo help' for usage.");
        if (ConsoleOwnership.OwnsTheWindow) Launcher.Pause();
        return ExitCode.Usage;
    }

    private static int PrintUsage()
    {
        Console.WriteLine($"""
            pathmemo {AppInfo.Version} - disk space screener for Windows

            USAGE
              pathmemo <command> [options]

            COMMANDS
              (none)              the screens, when launched from Explorer or with -i
              --interactive, -i   the screens, forced (needs a real terminal)
              status              volumes, last scan and store, as plain text
              scan [<path>...]    traverse the given roots, or every fixed volume
              tree [<path>]       one level of the last scan, largest first
              top [options]       largest files or directories, with filters
              audit [options]     space no scan can see: restore points, WinSxS, WSL, caches
              reclaim [options]   what is worth deleting, by rule, with risk and recovery
              dupes [options]     files stored twice, proved byte for byte
              rm <path>...        delete, into quarantine by default
              restore <op-id>     put a quarantined operation back
              purge [<op-id>]     free the space a quarantine is holding
              ops [<op-id>]       the deletion journal
              history [options]   list stored scans
              diff [<a> <b>]      what changed between two scans (default: the last two)
              doctor              report what pathmemo can do on this machine
              help                show this help
              --version           print version

            GLOBAL
              --data-dir <path>   where to keep snapshots and the database
                                  (ignored while running as administrator)

            scan
              --top <n>           how many largest entries to list (default 15)
              --scanner <kind>    mft | walk; omit to pick per volume (MFT needs NTFS
                                  and administrator rights, and is ~10-40x faster)
              --parallelism <n>   walk scanner worker count; omit to detect the medium
              --note <text>       label this scan
              --no-save           analyse without storing a snapshot
              --no-elevate        never offer to restart as administrator
              --quiet, -q         suppress the progress line and the elevation prompt
              --pause             wait for Enter before exiting
              --format <kind>     console | json | csv (default console)
              --output <file>     write the result there instead of stdout

            tree
              --scan <id>         which stored scan to read (default: newest)
              --limit <n>         rows to show (default 30)
              --size <mode>       unique | allocated | logical (default unique)

            top
              --dirs | --files    what to rank (default files)
              --limit <n>         rows to show (default 40)
              --min <size>        e.g. 1GB, 500mb
              --under <path>      restrict to a subtree
              --ext <a,b,c>       e.g. iso,vhdx,zip
              --older-than <dur>  e.g. 90d, 12h
              --newer-than <dur>
              --paths-only        one path per line, for pipes
              --scan <id>, --size <mode>

            history
              --limit <n>         rows to show (default 50)
              --since <when>      only scans since a date or a duration back (2026-09-01, 30d)
              --json              machine-readable, with per-volume numbers
              --categories        add the per-category totals (implies --json)

            diff
              <a> <b>             scan ids; one id means "against the newest",
                                  none means "the last two"
              --limit <n>         rows per section (default 15)
              --min <size>        change the 64MB floor of the interest threshold
              --size <mode>       unique | allocated | logical (default unique)
              --json              machine-readable
              Both scans need their snapshot, and neither may be a cancelled one.

            audit
              --id <probe>        one probe in full, with every remedy and path
              --apply <probe>     carry out that probe's remedy: confirmed, journalled,
                                  and elevated or refused
              --copy              with --id: put the first command on the clipboard
              --copy-index <n>    with --id: copy the n-th command instead
              --json              machine-readable report
              --quiet, -q         no progress line
              The report itself is read-only. Run as administrator to measure
              restore points and the component store.

            reclaim
              --scan <id>         which stored scan to read (default: newest)
              --risk <level>      how far to go: safe (default) | caution | danger
              --rule <id>         the paths behind one rule, with its remedy
              --list              print the rule set itself, no scan needed
              --min <size>        ignore matches below this
              --limit <n>         paths shown with --rule (default 40)
              --dry-run, -n       the deletion plan and a confirmation token
              --apply             delete what the ceiling allows, through 'rm'
              --mode <mode>       quarantine (default) | recycle | permanent
              --yes, -y           skip the y/n question
              --force             required for --risk danger
              --json              machine-readable report
              --keep <path>       add a path to protect.keep in config.json
              --disable <rule>    stop a rule appearing;  --enable <rule> undoes it
              Rules whose remedy is another tool's command (git gc, docker prune) are
              reported and never deleted, whatever the flags say.

            dupes
              --scan <id>         which stored scan to read (default: newest)
              --min <size>        ignore files below this (default 1MB)
              --max <size>        and above this
              --under <path>      restrict to a subtree
              --ext <a,b,c>       e.g. jpg,mp4,iso
              --hash <algo>       xxh128 (default) | sha256, for comparing with other tools
              --limit <n>         groups listed (default 25)
              --group <n>         the files of one group, as numbered in the table
              --all               every group with its files
              --paths-only        the extra copies, one path per line, for pipes
              --cached            show the last run instead of reading the disk again
              --estimate          say how much a search would read, and read nothing
              --no-verify         trust the hash; skip the byte-for-byte stage
              --no-cache          do not read or write the stored hashes
              --same-volume       do not match files across volumes
              --dry-run, -n       the deletion plan and a confirmation token
              --apply             delete every copy but the keeper, through 'rm'
              --mode <mode>       quarantine (default) | recycle | permanent
              --yes, -y           skip the y/n question and the read notice
              --json              machine-readable report
              Every group keeps one file, chosen by the rules of README section 8.4 and
              shown before anything happens. Cloud-only files are never read.

            rm
              --mode <mode>       quarantine | recycle | permanent (default: config)
              --permanent         same as --mode permanent
              --dry-run, -n       show the plan and a confirmation token, change nothing
              --from-stdin        read paths from standard input, one per line
              --reason <text>     recorded in the journal
              --scan <id>         verify sizes against that scan instead of the newest
              --confirm-token <t> the token a dry run printed, for scripts
              --yes, -y           skip the y/n question (never the typed confirmation)
              --json              machine-readable plan or result
              Quarantine is a rename on the same volume: it frees nothing until
              'pathmemo purge'. Permanent frees the space and cannot be undone.

            purge
              <op-id>             one operation      --expired   only what has aged out
              --bin               empty the Recycle Bin instead
              --yes, -y           skip the confirmation

            ops
              <op-id>             the items of one operation
              --limit <n>         rows to show (default 20)

            Not implemented yet (see README.md for the full command set):
              errors, export, schedule, config
            """);
        return ExitCode.Ok;
    }
}
