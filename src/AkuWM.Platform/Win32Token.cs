using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Security;

namespace AkuWM.Platform;

/// <summary>
/// What Windows has actually granted this process, as opposed to what its
/// manifest asked for.
/// </summary>
/// <remarks>
/// A manifest that declares <c>uiAccess="true"</c> is a request. Windows grants
/// it only when the binary is Authenticode-signed with a certificate the
/// machine trusts <em>and</em> sits in a secure directory; if either is missing
/// it refuses to start the process at all rather than starting it without.
/// So the flag on the token is the only honest answer, and it is the one
/// <c>doctor</c> reports -- an AkuWM that quietly lost uiAccess would look
/// perfectly healthy right up until the first chord over a game.
/// </remarks>
public static class Win32Token
{
    /// <summary><c>TokenUIAccess</c>, which CsWin32 does not name.</summary>
    private const TOKEN_INFORMATION_CLASS TokenUiAccess = (TOKEN_INFORMATION_CLASS)26;

    /// <summary>True when this process may drive higher-integrity windows.</summary>
    public static bool HasUiAccess()
    {
        if (!PInvoke.OpenProcessToken(
                PInvoke.GetCurrentProcess_SafeHandle(), TOKEN_ACCESS_MASK.TOKEN_QUERY, out SafeFileHandle token))
        {
            return false;
        }

        using (token)
        {
            unsafe
            {
                uint granted = 0;
                return PInvoke.GetTokenInformation(token, TokenUiAccess, &granted, sizeof(uint), out uint _)
                       && granted != 0;
            }
        }
    }

    /// <summary>True when this process is running elevated.</summary>
    public static bool IsElevated()
    {
        if (!PInvoke.OpenProcessToken(
                PInvoke.GetCurrentProcess_SafeHandle(), TOKEN_ACCESS_MASK.TOKEN_QUERY, out SafeFileHandle token))
        {
            return false;
        }

        using (token)
        {
            unsafe
            {
                TOKEN_ELEVATION elevation = default;
                return PInvoke.GetTokenInformation(
                           token,
                           TOKEN_INFORMATION_CLASS.TokenElevation,
                           &elevation,
                           (uint)sizeof(TOKEN_ELEVATION),
                           out uint _)
                       && elevation.TokenIsElevated != 0;
            }
        }
    }
}
