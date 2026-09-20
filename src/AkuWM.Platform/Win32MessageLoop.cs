using Windows.Win32;
using Windows.Win32.UI.WindowsAndMessaging;

namespace AkuWM.Platform;

/// <summary>
/// A message loop, which is what makes hooks fire.
/// </summary>
/// <remarks>
/// Both the low-level hooks and <c>SetWinEventHook</c> deliver through the
/// message queue of the thread that installed them: a thread that never pumps
/// never hears a thing, and a low-level hook whose thread stops pumping is
/// removed by Windows after <c>LowLevelHooksTimeout</c>.
/// </remarks>
public static class Win32MessageLoop
{
    private const uint QuitMessage = 0x0012;

    /// <summary>Pumps this thread's queue for a while, then returns.</summary>
    public static void PumpFor(TimeSpan duration)
    {
        uint threadId = PInvoke.GetCurrentThreadId();

        var alarm = new Thread(() =>
        {
            Thread.Sleep(duration);
            PInvoke.PostThreadMessage(threadId, QuitMessage, default, default);
        })
        {
            IsBackground = true,
            Name = "pump-alarm",
        };
        alarm.Start();

        Pump();
    }

    /// <summary>Pumps until a <c>WM_QUIT</c> arrives.</summary>
    public static unsafe void Pump()
    {
        MSG message;
        while (PInvoke.GetMessage(&message, default, 0, 0))
        {
            PInvoke.TranslateMessage(&message);
            PInvoke.DispatchMessage(&message);
        }
    }
}
