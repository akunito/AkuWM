using AkuWM.Core.Logging;
using AkuWM.Core.Model;

namespace AkuWM.Core.Desk;

/// <summary>
/// Windows' own active-window tracking (the focus follows the pointer; on
/// since 10.x, raise off, delay 0) activates whatever the pointer crosses --
/// and a light-dismiss popup closes the moment it is deactivated. The "Open
/// with" dialog (OpenWith.exe, class "Open With": a captioned top-level
/// window, topmost, tool window, no owner) closed before the pointer could
/// reach it, every time
/// (2026-10-02, reproduced: gone the instant the pointer parked over another
/// window). The same is true of menus and the shell's flyouts.
/// </summary>
/// <remarks>
/// While such a popup holds the foreground the native tracking is switched
/// off; the next foreground change to anything else, the popup's destruction,
/// or the daemon's exit switches it back to what it was. Only ever pauses a
/// tracking that was on: a desk with it off is left alone.
/// </remarks>
public sealed class LightDismissGuard
{
    private readonly Func<WindowHandle, bool> _isPopup;
    private readonly Func<bool> _trackingOn;
    private readonly Action<bool> _setTracking;
    private WindowHandle _popup = WindowHandle.None;

    public LightDismissGuard(Func<WindowHandle, bool> isPopup, Func<bool> trackingOn, Action<bool> setTracking)
    {
        _isPopup = isPopup;
        _trackingOn = trackingOn;
        _setTracking = setTracking;
    }

    /// <summary>True while the tracking is paused for a popup.</summary>
    public bool Paused => !_popup.IsNone;

    /// <summary>The foreground moved to <paramref name="handle"/>; <paramref name="managed"/> says whether the desk manages it (a managed window is never a light-dismiss popup).</summary>
    public void ForegroundChanged(WindowHandle handle, bool managed)
    {
        bool popup = !managed && !handle.IsNone && _isPopup(handle);
        if (!managed && !handle.IsNone)
        {
            Log.Debug(() => $"  foreground {handle} is not managed; light-dismiss popup: {popup}");
        }

        if (popup)
        {
            if (!_popup.IsNone)
            {
                _popup = handle; // another popup took over while paused: it is the one to watch now
            }
            else if (_trackingOn())
            {
                _setTracking(false);
                _popup = handle;
                Log.Debug(() => $"  light-dismiss popup {handle} has the foreground: native focus-follows-mouse paused");
            }

            return;
        }

        Resume();
    }

    /// <summary>A window is gone; if it was the popup, the tracking comes back.</summary>
    public void WindowGone(WindowHandle handle)
    {
        if (handle == _popup)
        {
            Resume();
        }
    }

    /// <summary>Puts the tracking back if this guard paused it.</summary>
    public void Resume()
    {
        if (_popup.IsNone)
        {
            return;
        }

        _popup = WindowHandle.None;
        _setTracking(true);
        Log.Debug("  light-dismiss popup gone: native focus-follows-mouse resumed");
    }
}
