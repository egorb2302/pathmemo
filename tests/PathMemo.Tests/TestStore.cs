using System.Runtime.CompilerServices;
using PathMemo.Config;
using PathMemo.Platform;

namespace PathMemo.Tests;

/// <summary>
/// Points the store at a temporary directory, and refuses to continue if it did not move.
/// </summary>
/// <remarks>
/// Every test that writes a database, a config file or a quarantine goes through here. The
/// reason is an incident rather than a preference: <c>--data-dir</c> is deliberately ignored
/// while elevated (README section 12.1), so the same setup code that redirects the store for
/// an ordinary user silently leaves it pointing at the real one for an administrator. Run
/// elevated, the suite then wrote its fixtures into a developer's own snapshots and
/// overwrote one of them, and a GitHub Windows runner - whose user is an administrator - is
/// elevated by default.
///
/// Two things prevent a repeat. <see cref="Init"/> tells the application which answer to
/// give, so the suite no longer inherits it from whoever launched the process. And
/// <see cref="Use"/> checks the redirect actually took effect, so if that ever stops working
/// the tests fail on their first line instead of quietly operating on real data. The check
/// is the important half: it holds even if someone later removes the first.
/// </remarks>
internal static class TestStore
{
    /// <summary>
    /// Fixes the answer to "are we elevated" for the whole assembly, before any test runs.
    /// </summary>
    /// <remarks>
    /// Unelevated is the right answer for the suite: it is the configuration in which
    /// <c>--data-dir</c> and user config are honoured, which is what almost every test is
    /// about. The behaviour that only exists while elevated is section 12.1's refusal, and
    /// that is asserted deliberately by <see cref="PolishTests"/> rather than by whatever
    /// token the test runner happens to hold.
    ///
    /// Safe as process-wide state only because the suite runs sequentially - see
    /// TestParallelism.cs, which disables it for this very same reason.
    /// </remarks>
    [ModuleInitializer]
    internal static void Init() => Elevation.Assume(false);

    /// <summary>
    /// Redirects the store to <paramref name="directory"/> and verifies it went there.
    /// </summary>
    internal static void Use(string directory)
    {
        AppPaths.Redirect(directory);

        var actual = Path.GetFullPath(AppPaths.DataDirectory);
        var wanted = Path.GetFullPath(directory);

        if (!string.Equals(actual, wanted, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"the store was asked to move to '{wanted}' and is at '{actual}'. Refusing to " +
                "run: the test would write its fixtures into the real store. This is what " +
                "happens when the process is elevated (README section 12.1).");
        }

        AppConfig.Reset();
    }
}
