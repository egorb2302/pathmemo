using System.Security.Principal;

namespace PathMemo.Platform;

internal static class Elevation
{
    private static bool? _cached;
    private static bool? _assumed;

    /// <summary>
    /// Makes <see cref="IsElevated"/> answer <paramref name="elevated"/> instead of asking
    /// Windows. For tests only.
    /// </summary>
    /// <remarks>
    /// The suite has to be able to say what it is testing rather than inherit it from
    /// whoever started the process, because the two answers lead to different code: while
    /// elevated, <c>--data-dir</c> is refused and user config is ignored (README section
    /// 12.1). Tests that point the store at a temporary directory therefore only work
    /// unelevated - and when they run elevated they operate on the real store instead,
    /// which is how a CI runner (whose user is an administrator) both went red and
    /// overwrote a developer's snapshots. This seam is the fix: the suite declares the
    /// answer, so it behaves the same whoever runs it.
    ///
    /// Note that this does not weaken anything in a shipped build: nothing in the
    /// application calls it, and a filtered token still cannot open <c>\.\C:</c>, so
    /// claiming to be elevated does not make the MFT scanner work.
    /// </remarks>
    internal static void Assume(bool elevated) => _assumed = elevated;

    /// <summary>
    /// True when the process runs with an elevated token.
    /// </summary>
    /// <remarks>
    /// Under UAC a non-elevated admin account gets a filtered token that does not carry the
    /// Administrators role, so the role check answers the question we actually care about:
    /// "can we open <c>\.\C:</c> for the MFT scanner?" (README section 4.2).
    /// </remarks>
    internal static bool IsElevated
    {
        get
        {
            if (_assumed is { } assumed) return assumed;
            if (_cached is { } v) return v;
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                _cached = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                _cached = false;
            }
            return _cached.Value;
        }
    }
}
