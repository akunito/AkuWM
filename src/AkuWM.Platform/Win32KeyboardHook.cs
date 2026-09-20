using AkuWM.Core.Logging;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace AkuWM.Platform;

/// <param name="VirtualKey">The key, as Windows numbers them.</param>
/// <param name="Down">Key down, as opposed to key up.</param>
/// <param name="Injected">Sent by another program rather than typed.</param>
/// <param name="At">Ticks when the callback saw it, for the latency bench.</param>
public readonly record struct KeyEvent(uint VirtualKey, bool Down, bool Injected, long At);

/// <summary>
/// The low-level keyboard hook.
/// </summary>
/// <remarks>
/// <para>
/// This is how AkuWM will own its chords from M3. It has to be a hook and not
/// <c>RegisterHotKey</c>, because this keyboard's Office key already claims
/// combinations with Ctrl+Alt+Win in them and no application can register one.
/// </para>
/// <para>
/// The callback is on the hot path of every keystroke the machine sees: it
/// reads the event, hands it over, and returns. Windows silently removes a
/// low-level hook whose callback overruns <c>LowLevelHooksTimeout</c>, and the
/// symptom is a keyboard that stops working, so nothing else happens in here.
/// </para>
/// </remarks>
public sealed class Win32KeyboardHook : IDisposable
{
    private const int HookAction = 0;          // HC_ACTION
    private const uint KeyDown = 0x0100;       // WM_KEYDOWN
    private const uint SysKeyDown = 0x0104;    // WM_SYSKEYDOWN
    private const uint KeyUp = 0x0101;         // WM_KEYUP
    private const uint SysKeyUp = 0x0105;      // WM_SYSKEYUP
    private const uint InjectedFlag = 0x00000010; // LLKHF_INJECTED

    private HOOKPROC? _callback;
    private UnhookWindowsHookExSafeHandle? _hook;

    /// <summary>Returns true to swallow the key, so it never reaches the window.</summary>
    public Func<KeyEvent, bool>? Key { get; set; }

    public bool Installed => _hook is { IsInvalid: false };

    /// <summary>Installs the hook on the calling thread, which must have a message loop.</summary>
    public bool Install()
    {
        _callback = OnKey;
        _hook = PInvoke.SetWindowsHookEx(
            WINDOWS_HOOK_ID.WH_KEYBOARD_LL, _callback, PInvoke.GetModuleHandle((string?)null), 0);

        if (_hook.IsInvalid)
        {
            Log.Error("the low-level keyboard hook could not be installed");
            return false;
        }

        return true;
    }

    private unsafe LRESULT OnKey(int code, WPARAM wParam, LPARAM lParam)
    {
        if (code != HookAction)
        {
            return PInvoke.CallNextHookEx(null, code, wParam, lParam);
        }

        var info = *(KBDLLHOOKSTRUCT*)lParam.Value;
        uint message = (uint)wParam.Value;
        bool down = message is KeyDown or SysKeyDown;
        bool up = message is KeyUp or SysKeyUp;

        if ((down || up) && Key is { } handler)
        {
            var key = new KeyEvent(
                info.vkCode, down, (info.flags & (KBDLLHOOKSTRUCT_FLAGS)InjectedFlag) != 0, Environment.TickCount64);

            if (handler(key))
            {
                return new LRESULT(1);
            }
        }

        return PInvoke.CallNextHookEx(null, code, wParam, lParam);
    }

    public void Dispose()
    {
        _hook?.Dispose();
        _hook = null;
        _callback = null;
    }
}
