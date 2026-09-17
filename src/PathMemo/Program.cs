using PathMemo.Cli;
using PathMemo.Cli.Commands;
using PathMemo.Config;

namespace PathMemo;

internal static class Program
{
    internal static int Main(string[] args)
    {
        // Long paths without the \?\ prefix. The manifest declares longPathAware;
        // this switch turns off BCL-side MAX_PATH validation (README section 15.5).
        AppContext.SetSwitch("Switch.System.IO.UseLegacyPathHandling", false);
        AppContext.SetSwitch("Switch.System.IO.BlockLongPaths", false);

        // P0 uses a minimal dispatcher. System.CommandLine arrives in P1 together with
        // the first command that actually has options to parse.
        var verb = args.Length > 0 ? args[0] : "";

        try
        {
            return verb switch
            {
                "doctor" => DoctorCommand.Run(),
                "--version" or "-V" => PrintVersion(),
                "" or "help" or "--help" or "-h" or "/?" => PrintUsage(),
                _ => UnknownVerb(verb),
            };
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("cancelled");
            return ExitCode.Cancelled;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"pathmemo: {ex.Message}");
            return ExitCode.Failure;
        }
    }

    private static int PrintVersion()
    {
        Console.WriteLine(AppInfo.Version);
        return ExitCode.Ok;
    }

    private static int UnknownVerb(string verb)
    {
        Console.Error.WriteLine($"pathmemo: unknown command '{verb}'");
        Console.Error.WriteLine("Run 'pathmemo help' for usage.");
        return ExitCode.Usage;
    }

    private static int PrintUsage()
    {
        Console.WriteLine($"""
            pathmemo {AppInfo.Version} - disk space screener for Windows

            USAGE
              pathmemo <command> [options]

            COMMANDS
              doctor          report what pathmemo can do on this machine
              help            show this help
              --version       print version

            Not implemented yet (see README.md for the full command set):
              scan, tree, top, audit, reclaim, dupes, rm, restore, purge,
              ops, history, diff, errors, export, schedule, config
            """);
        return ExitCode.Ok;
    }
}
