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

    /// <summary>
    /// Brings a window back from minimised or maximised to the size it had.
    /// </summary>
    /// <remarks>
    /// <c>SW_RESTORE</c> activates the window as a side effect. Restoring the
    /// desk therefore does it only for windows that were not in their normal
    /// state to begin with, and the focus is put back afterwards.
    /// </remarks>
    public static void Restore(WindowHandle window) =>
        PInvoke.ShowWindow(new HWND((IntPtr)window.Value), SHOW_WINDOW_CMD.SW_RESTORE);

    public static void Maximize(WindowHandle window) =>
        PInvoke.ShowWindow(new HWND((IntPtr)window.Value), SHOW_WINDOW_CMD.SW_MAXIMIZE);

    /// <summary>
    /// Minimises without taking the focus, which <c>SW_MINIMIZE</c> would hand
    /// to whatever is underneath.
    /// </summary>
    public static void Minimize(WindowHandle window) =>
        PInvoke.ShowWindow(new HWND((IntPtr)window.Value), SHOW_WINDOW_CMD.SW_SHOWMINNOACTIVE);
}
