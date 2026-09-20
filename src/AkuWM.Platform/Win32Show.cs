using AkuWM.Core.Model;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace AkuWM.Platform;

/// <summary>Showing and hiding a window the ordinary way.</summary>
/// <remarks>
/// Not how a workspace is hidden -- minimising a game stops it rendering, and
/// <c>SW_HIDE</c> takes it off the taskbar for good -- but the path to try when
/// the cloak will not come off.
/// </remarks>
public static class Win32Show
{
    public static void Show(WindowHandle window) =>
        PInvoke.ShowWindow(new HWND((IntPtr)window.Value), SHOW_WINDOW_CMD.SW_SHOW);

    public static void Restore(WindowHandle window) =>
        PInvoke.ShowWindow(new HWND((IntPtr)window.Value), SHOW_WINDOW_CMD.SW_RESTORE);
}
