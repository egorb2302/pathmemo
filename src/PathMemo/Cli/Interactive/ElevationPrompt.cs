namespace PathMemo.Cli.Interactive;

internal enum ElevationChoice
{
    Continue,
    Restart,
    Quit,
}

/// <summary>
/// The one-time offer to restart elevated before a degraded scan (README section 4.2).
/// </summary>
/// <remarks>
/// Asked only when a person is at the keyboard: with either stream redirected the
/// answer is "continue" and the warning goes to stderr, so a script never hangs on a
/// question nobody will see.
/// </remarks>
internal static class ElevationPrompt
{
    internal static ElevationChoice Ask()
    {
        var interactive = !Console.IsInputRedirected && !Console.IsOutputRedirected;
        var w = interactive ? Console.Out : Console.Error;

        w.WriteLine();
        w.WriteLine("Running without administrator rights.");
        w.WriteLine("  · Fast MFT scan unavailable (falling back to directory walk: ~10-40x slower)");
        w.WriteLine("  · Files in other user profiles and protected folders will be missed");
        w.WriteLine("  · Hard link detection limited to files over 1 MB");
        w.WriteLine();

        if (!interactive) return ElevationChoice.Continue;

        while (true)
        {
            Console.Write("  [R] Restart as administrator    [C] Continue anyway    [Q] Quit   > ");
            var answer = (Console.ReadLine() ?? "q").Trim().ToLowerInvariant();

            switch (answer)
            {
                case "r": return ElevationChoice.Restart;
                case "c" or "": return ElevationChoice.Continue;
                case "q": return ElevationChoice.Quit;
            }
        }
    }
}
