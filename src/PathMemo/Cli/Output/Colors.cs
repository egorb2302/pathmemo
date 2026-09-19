namespace PathMemo.Cli.Output;

/// <summary>
/// Whether anything may emit colour at all (README section 13.1).
/// </summary>
/// <remarks>
/// One switch, read in one place, so that <c>--no-color</c> and <c>NO_COLOR</c> cannot
/// disagree. The environment variable is the convention (no-color.org) and the option is
/// what a script that cannot set the environment uses; both mean the same thing, and the
/// screen renderer draws in meanings rather than colours so turning them off changes
/// nothing but the escape sequences (README section 14.4).
/// </remarks>
internal static class Colors
{
    private static bool _off = Environment.GetEnvironmentVariable("NO_COLOR") is not null;

    internal static bool Enabled => !_off;

    /// <summary>Called for <c>--no-color</c>, before any command runs.</summary>
    internal static void Disable() => _off = true;
}
