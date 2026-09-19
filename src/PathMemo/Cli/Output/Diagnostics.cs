using System.Diagnostics;
using System.Globalization;

namespace PathMemo.Cli.Output;

/// <summary>
/// The measurement hook behind the numbers in README section 20: <c>PATHMEMO_DIAG=1</c>.
/// </summary>
/// <remarks>
/// <para>
/// A budget nobody can measure is a wish. Every row of section 20 has to be reproducible
/// on someone else's machine with the shipped executable, which means the tool itself has
/// to be able to say what it just cost - not a profiler, not a debug build.
/// </para>
/// <para>
/// It writes to stderr, never stdout: <c>--json</c> promises that stdout carries nothing
/// but the document (README section 13.6), and a diagnostic line is not data.
/// </para>
/// </remarks>
internal static class Diagnostics
{
    /// <summary>Whether diagnostics were asked for. Read once: it cannot change mid-run.</summary>
    internal static readonly bool On = Environment.GetEnvironmentVariable("PATHMEMO_DIAG") == "1";

    internal static void Note(string text)
    {
        if (On) Console.Error.WriteLine($"  diag: {text}");
    }

    internal static void Note(string format, params object[] arguments)
    {
        if (On) Console.Error.WriteLine($"  diag: {string.Format(CultureInfo.InvariantCulture, format, arguments)}");
    }

    /// <summary>
    /// Memory as the operating system sees it, for the RSS rows of section 20.
    /// </summary>
    /// <remarks>
    /// Peak working set is the one that matters and the one a user can check in Task
    /// Manager; the managed numbers are here to say how much of it is the tree itself and
    /// how much is transient, which is the difference between a budget missed by design
    /// and one missed by a copy nobody needed.
    /// </remarks>
    internal static void Memory(TextWriter w, string label, long nodes = 0)
    {
        if (!On) return;

        var process = Process.GetCurrentProcess();
        w.WriteLine();
        w.WriteLine($"Diagnostics: {label}");
        w.WriteLine($"  live managed      {SizeFormat.Bytes(GC.GetTotalMemory(forceFullCollection: true))}");
        w.WriteLine($"  managed heap peak {SizeFormat.Bytes((long)GC.GetGCMemoryInfo().TotalCommittedBytes)}");
        w.WriteLine($"  private bytes     {SizeFormat.Bytes(process.PrivateMemorySize64)}");
        w.WriteLine($"  working set       {SizeFormat.Bytes(process.WorkingSet64)}");
        w.WriteLine($"  peak working set  {SizeFormat.Bytes(process.PeakWorkingSet64)}");
        w.WriteLine($"  gen0/1/2 GCs      {GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)}");

        if (nodes > 0)
            w.WriteLine($"  bytes per node    {GC.GetTotalMemory(forceFullCollection: false) / nodes}");
    }
}
