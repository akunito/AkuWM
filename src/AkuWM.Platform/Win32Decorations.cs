using AkuWM.Core.Logging;
using AkuWM.Core.Model;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;

namespace AkuWM.Platform;

/// <summary>
/// The border colour and the corner shape, which the compositor draws.
/// </summary>
/// <remarks>
/// <para>
/// Both are Windows 11 (build 22000) attributes: on anything older
/// <c>DwmSetWindowAttribute</c> returns <c>E_INVALIDARG</c> and nothing is
/// drawn. That is not an error, and saying so once is enough -- a window
/// manager that logs a failure per window per focus change is one nobody can
/// read the log of.
/// </para>
/// <para>
/// A window with no DWM frame of its own -- an application that draws its own
/// chrome -- takes the call and shows nothing. There is no way to tell the two
/// apart from here.
/// </para>
/// </remarks>
internal static class Win32Decorations
{
    // Not in the DWMWINDOWATTRIBUTE the metadata generates for this Windows
    // version, so the numbers are here, from the MS documentation.
    private const DWMWINDOWATTRIBUTE CornerPreference = (DWMWINDOWATTRIBUTE)33;
    private const DWMWINDOWATTRIBUTE BorderColor = (DWMWINDOWATTRIBUTE)34;

    private static bool _unsupported;

    internal static bool Apply(HWND hwnd, Decoration decoration)
    {
        if (_unsupported)
        {
            return false;
        }

        unsafe
        {
            uint border = decoration.Border;
            HRESULT colour = PInvoke.DwmSetWindowAttribute(hwnd, BorderColor, &border, sizeof(uint));

            uint corners = decoration.Corners switch
            {
                Corners.Square => 1u,      // DWMWCP_DONOTROUND
                Corners.Round => 2u,       // DWMWCP_ROUND
                Corners.RoundSmall => 3u,  // DWMWCP_ROUNDSMALL
                _ => 0u,                   // DWMWCP_DEFAULT
            };

            HRESULT shape = PInvoke.DwmSetWindowAttribute(hwnd, CornerPreference, &corners, sizeof(uint));

            if (colour.Succeeded || shape.Succeeded)
            {
                return true;
            }

            // E_INVALIDARG from BOTH is the old-Windows answer. A single
            // window refusing is ordinary; everything refusing is the build.
            if (colour.Value == unchecked((int)0x80070057) && shape.Value == unchecked((int)0x80070057))
            {
                _unsupported = true;
                Log.Info("this Windows build has no window border or corner attributes; AkuWM will not ask again");
            }

            return false;
        }
    }
}
