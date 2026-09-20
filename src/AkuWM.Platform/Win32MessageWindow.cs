using AkuWM.Core.Logging;
using AkuWM.Core.Model;
using AkuWM.Core.Platform;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace AkuWM.Platform;

/// <summary>
/// A window with no pixels, so AkuWM hears what only a window is told.
/// </summary>
/// <remarks>
/// <c>SetWinEventHook</c> delivers what happens to other people's windows. It
/// says nothing about the screens: a monitor sleeping and waking, a resolution
/// or scaling change, the taskbar moving, the machine suspending. Those arrive
/// as messages, and only to a window.
///
/// Without this the model called SetMonitors exactly once, at startup, and
/// then laid every workspace out against a screen geometry that no longer
/// existed -- which on this desk means windows placed off the edge of the
/// remaining monitor. The whole EDID-role design was unreachable because the
/// wake-up never arrived.
/// </remarks>
public sealed class Win32MessageWindow : IDisposable
{
    private const uint DisplayChange = 0x007E;
    private const uint SettingChange = 0x001A;
    private const uint PowerBroadcast = 0x0218;
    private const uint DpiChanged = 0x02E0;

    private const uint SuspendResumeAutomatic = 0x0012;
    private const uint Suspend = 0x0004;
    private const uint ResumeSuspend = 0x0007;

    /// <summary>The work area moved: the taskbar changed side or size.</summary>
    private const uint SpiSetWorkArea = 0x002F;

    private const string ClassName = "AkuWM.Messages";

    private WNDPROC? _procedure;
    private HWND _window;
    private ushort _class;

    public event Action<PlatformEvent>? Event;

    /// <summary>Must be called on the thread that pumps the messages.</summary>
    public unsafe bool Create()
    {
        _procedure = Procedure;

        fixed (char* name = ClassName)
        {
            var description = new WNDCLASSEXW
            {
                cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<WNDCLASSEXW>(),
                lpfnWndProc = _procedure,
                hInstance = (HINSTANCE)PInvoke.GetModuleHandle((string?)null).DangerousGetHandle(),
                lpszClassName = name,
            };

            _class = PInvoke.RegisterClassEx(description);
        }

        if (_class == 0)
        {
            Log.Warn("the message window class could not be registered; display changes will not be seen");
            return false;
        }

        // HWND_MESSAGE: no pixels, no taskbar entry, not enumerated -- and it
        // still receives broadcasts.
        _window = PInvoke.CreateWindowEx(
            0, ClassName, "AkuWM", 0, 0, 0, 0, 0, HWND.HWND_MESSAGE, null, null, null);

        if (_window.IsNull)
        {
            Log.Warn("the message window could not be created; display changes will not be seen");
            return false;
        }

        Log.Info("message window up: display, work-area and power changes are heard");
        return true;
    }

    private LRESULT Procedure(HWND window, uint message, WPARAM wParam, LPARAM lParam)
    {
        PlatformEventKind? kind = message switch
        {
            DisplayChange => PlatformEventKind.DisplayChanged,
            DpiChanged => PlatformEventKind.DisplayChanged,
            SettingChange when (uint)wParam.Value == SpiSetWorkArea => PlatformEventKind.SettingsChanged,
            PowerBroadcast when (uint)wParam.Value is Suspend or ResumeSuspend => PlatformEventKind.PowerSuspend,
            PowerBroadcast when (uint)wParam.Value == SuspendResumeAutomatic => PlatformEventKind.PowerResume,
            _ => null,
        };

        if (kind is not null)
        {
            // Translate and hand over, like every other callback here: this is
            // somebody else's notification.
            Event?.Invoke(new PlatformEvent(kind.Value, WindowHandle.None, Environment.TickCount64));
        }

        return PInvoke.DefWindowProc(window, message, wParam, lParam);
    }

    public void Dispose()
    {
        if (!_window.IsNull)
        {
            PInvoke.DestroyWindow(_window);
            _window = HWND.Null;
        }

        if (_class != 0)
        {
            PInvoke.UnregisterClass(ClassName, null);
            _class = 0;
        }

        _procedure = null;
    }
}
