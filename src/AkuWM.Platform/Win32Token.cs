using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Foundation;
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

    /// <summary>
    /// Whether this binary's own manifest asked for uiAccess at all.
    /// </summary>
    /// <remarks>
    /// Asking and being granted are different failures with different fixes,
    /// and they look identical from the token alone. A build that never asked
    /// is a mistake in the build; one that asked and was refused is a mistake
    /// in the install. Saying which saves the wrong half of the problem being
    /// investigated -- as it was here, for the better part of an hour, because
    /// a stale intermediate build quietly put the development manifest into
    /// the binary that went to Program Files.
    /// </remarks>
    public static bool ManifestRequestsUiAccess()
    {
        try
        {
            unsafe
            {
                // The executable's own module: its resources are where the
                // manifest the loader read lives.
                using FreeLibrarySafeHandle module = PInvoke.GetModuleHandle((string?)null);
                var self = (HMODULE)module.DangerousGetHandle();

                // RT_MANIFEST (24), CREATEPROCESS_MANIFEST_RESOURCE_ID (1).
                HRSRC resource = PInvoke.FindResource(self, (char*)1, (char*)24);
                if (resource == default)
                {
                    return false;
                }

                uint size = PInvoke.SizeofResource(self, resource);
                void* data = PInvoke.LockResource(PInvoke.LoadResource(self, resource));
                if (size == 0 || data is null)
                {
                    return false;
                }

                var bytes = new byte[size];
                Marshal.Copy((IntPtr)data, bytes, 0, (int)size);
                return Encoding.UTF8.GetString(bytes)
                    .Contains("uiAccess=\"true\"", StringComparison.OrdinalIgnoreCase);
            }
        }
        catch (Exception)
        {
            return false;
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
