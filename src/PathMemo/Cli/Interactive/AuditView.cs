using System.Globalization;
using PathMemo.Audit;
using PathMemo.Cli.Commands;
using PathMemo.Cli.Output;
using PathMemo.Platform;

namespace PathMemo.Cli.Interactive;

/// <summary>
/// The audit inside the double-click session: the report, then a numbered list to dig
/// into one finding and copy its command. Same line-oriented shape as <see cref="Browser"/>.
/// </summary>
internal static class AuditView
{
    internal static void Run(CancellationToken ct)
    {
        Launcher.ClearScreen();
        Console.WriteLine();
        Console.WriteLine("  Auditing. A few seconds; longer when DISM is consulted.");

        AuditReport report;
        try
        {
            var progress = new Progress<string>(title => Console.Write($"\r  probing {title,-40}"));
            report = AuditRunner.Run(AuditRunner.AllProbes(), progress, ct);
            Console.Write("\r" + new string(' ', 52) + "\r");
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("  Cancelled.");
            Launcher.Pause();
            return;
        }

        var findings = report.Findings
            .Where(f => f.Status == FindingStatus.Measured && (f.ReclaimableBytes > 0 || f.ReclaimableBytes is null))
            .OrderByDescending(f => f.ReclaimableBytes ?? f.UsedBytes ?? 0)
            .ToList();

        while (true)
        {
            Launcher.ClearScreen();
            AuditCommand.Print(report, Console.Out);

            if (findings.Count == 0)
            {
                Launcher.Pause();
                return;
            }

            Console.WriteLine("  DETAILS");
            for (var i = 0; i < findings.Count; i++)
            {
                var f = findings[i];
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "  {0,3}  {1}{2}",
                    i + 1, f.Title, f.Volume.Length > 0 ? "  " + f.Volume : ""));
            }

            Console.WriteLine();
            Console.WriteLine("  number = details and remedies   ·   q = back");
            Console.Write("  > ");

            var input = (Console.ReadLine() ?? "q").Trim().ToLowerInvariant();
            if (input is "q" or "") return;

            if (int.TryParse(input, CultureInfo.InvariantCulture, out var row) && row >= 1 && row <= findings.Count)
            {
                Details(findings[row - 1]);
                continue;
            }

            Console.WriteLine("  ?");
            Launcher.Pause();
        }
    }

    private static void Details(AuditFinding f)
    {
        while (true)
        {
            Launcher.ClearScreen();
            Console.WriteLine();
            Console.WriteLine($"  {f.Title}{(f.Volume.Length > 0 ? "  " + f.Volume : "")}");
            Console.WriteLine("  " + new string('-', 62));
            if (f.UsedBytes is { } used) Console.WriteLine($"  used           {SizeFormat.Bytes(used)}");
            Console.WriteLine($"  reclaimable    {(f.ReclaimableBytes is { } r ? SizeFormat.Bytes(r) : "unknown from outside")}");
            Console.WriteLine($"  risk           {AuditCommand.Label(f)}");
            Console.WriteLine();
            foreach (var line in AuditCommand.Wrap(f.Explanation, 66)) Console.WriteLine("  " + line);
            if (f.Note is { } note) Console.WriteLine("  (" + note + ")");

            var n = 1;
            if (f.Remedies.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine("  REMEDIES");
                foreach (var remedy in f.Remedies)
                {
                    Console.WriteLine($"  {n++}. {AuditCommand.RemedyLine(remedy)}");
                    if (remedy.Caveat is { } caveat) Console.WriteLine("     ⚠ " + caveat);
                }
            }

            if (f.Paths.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine("  PATHS");
                foreach (var path in f.Paths.Take(12)) Console.WriteLine("  " + path);
                if (f.Paths.Count > 12) Console.WriteLine($"  ... {f.Paths.Count - 12} more");
            }

            Console.WriteLine();
            Console.WriteLine("  Nothing runs from here. c<n> = copy remedy n to the clipboard (c = the first)   ·   q = back");
            Console.Write("  > ");

            var input = (Console.ReadLine() ?? "q").Trim().ToLowerInvariant();
            if (input is "q" or "") return;

            if (input.StartsWith('c'))
            {
                var index = input.Length == 1 ? 1
                    : int.TryParse(input[1..], CultureInfo.InvariantCulture, out var i) ? i : 0;

                if (index >= 1 && index <= f.Remedies.Count)
                {
                    var remedy = f.Remedies[index - 1];
                    Console.WriteLine(Clipboard.TrySetText(remedy.Display, out var error)
                        ? $"  copied.{(remedy.NeedsElevation ? " Paste it into an administrator prompt." : "")}"
                        : $"  could not copy: {error}");
                }
                else
                {
                    Console.WriteLine("  ?");
                }

                Launcher.Pause();
                continue;
            }

            Console.WriteLine("  ?");
            Launcher.Pause();
        }
    }
}
