using System.Security.Principal;

namespace PathMemo.Platform;

internal static class Elevation
{
    private static bool? _cached;

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
