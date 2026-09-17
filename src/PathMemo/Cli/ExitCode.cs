namespace PathMemo.Cli;

/// <summary>
/// Process exit codes. Stable contract for scripting (README section 13.5).
/// </summary>
internal static class ExitCode
{
    internal const int Ok = 0;
    internal const int Failure = 1;
    internal const int Usage = 2;
    internal const int Partial = 3;
    internal const int Cancelled = 4;
    internal const int NeedsElevation = 5;
    internal const int Unsafe = 6;
    internal const int NoData = 7;
    internal const int Locked = 8;
}
