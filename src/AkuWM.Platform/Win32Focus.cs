using AkuWM.Core.Model;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.WindowsAndMessaging;

namespace AkuWM.Platform;

/// <summary>How the focus was obtained, if it was.</summary>
public enum FocusRoute
{
    /// <summary>The window was already there.</summary>
    AlreadyFocused,

    /// <summary>A plain call did it: this process held the foreground right.</summary>
    Direct,

    /// <summary>The input queues had to be attached first.</summary>
    AttachedInput,

    /// <summary>A keystroke had to be injected to claim the right.</summary>
    InjectedInput,

    /// <summary>
    /// The last resort was available but deliberately not taken, because a
    /// game had the foreground. See <see cref="Win32Focus"/>.
    /// </summary>
    RefusedNearGame,

    /// <summary>Nothing worked.</summary>
    Refused,
}

/// <param name="Route">Which of the three ways it took, or none.</param>
/// <param name="Detail">What happened, for the log and the spike.</param>
public readonly record struct FocusResult(FocusRoute Route, string Detail)
{
    public bool Succeeded => Route is not (FocusRoute.Refused or FocusRoute.RefusedNearGame);
}

/// <summary>
/// Putting a window in the foreground, which Windows does not simply let a
/// program do.
/// </summary>
/// <remarks>
/// <para>
/// <c>SetForegroundWindow</c> only works for a process that holds the
/// foreground right: the one the user is interacting with, the one that has
/// just received input, or a few other documented cases. Everyone else gets a
/// flashing taskbar button -- and, worth knowing, <strong>a return value of
/// true anyway</strong>. So success is never the return value here; it is
/// whether the window is actually in front afterwards.
/// </para>
/// <para>
/// In normal use AkuWM holds the right honestly: its hook has just consumed
/// the keystroke the user pressed. The two fallbacks are for the times it does
/// not -- a command arriving over the pipe, a repair after a display change.
/// </para>
/// <para>
/// The third route fabricates a keystroke, and is <strong>never</strong> taken
/// while a game has the foreground: synthetic input is the one thing an
/// anti-cheat is built to catch, and no window-manager convenience is worth
/// being indistinguishable from a macro. Not moving the focus is the cheaper
/// failure.
/// </para>
/// </remarks>
public static class Win32Focus
{
    /// <summary>
    /// The foreground change is not instantaneous; without a moment to settle,
    /// the check below reads the old foreground and reports a failure that
    /// did not happen.
    /// </summary>
    // The longest a route is given to take effect, and how often it is asked.
    // This runs on the wm thread, so a flat 40 ms sleep per route put up to
    // 120 ms of doing nothing inside the gesture -- against a 5 ms budget for
    // the whole thing. The usual case is the first route working, and polling
    // returns the moment it has.
    private const int SettleMs = 40;
    private const int PollMs = 2;

    /// <summary>
    /// Whether the foreground window is something no synthetic input may go
    /// near. Supplied by the caller, because only the model knows what is a
    /// game.
    /// </summary>
    public static Func<bool>? GameInFront { get; set; }

    public static FocusResult Focus(WindowHandle handle)
    {
        var hwnd = new HWND((IntPtr)handle.Value);

        if (!PInvoke.IsWindow(hwnd))
        {
            return new FocusResult(FocusRoute.Refused, "the window is gone");
        }

        if (PInvoke.GetForegroundWindow() == hwnd)
        {
            return new FocusResult(FocusRoute.AlreadyFocused, "it was already in front");
        }

        if (Try(hwnd))
        {
            return new FocusResult(FocusRoute.Direct, "a plain SetForegroundWindow did it");
        }

        // Before the attach route, not only before the injection: attaching
        // this thread's input queue to a game's is coupling to the thing the
        // anti-cheat policy says never to touch (docs/input-and-anticheat.md),
        // and with uiAccess the attach is no longer refused by integrity. Both
        // fallbacks also cost up to SettleMs each on the wm thread.
        if (GameInFront?.Invoke() == true)
        {
            return new FocusResult(
                FocusRoute.RefusedNearGame,
                "refused: the remaining routes attach to or inject into the foreground, and a game has it");
        }

        if (WithAttachedInput(hwnd))
        {
            return new FocusResult(FocusRoute.AttachedInput, "attaching the input queues did it");
        }

        if (WithInjectedInput(hwnd))
        {
            return new FocusResult(
                FocusRoute.InjectedInput, "a dummy keystroke was needed to claim the foreground right");
        }

        return new FocusResult(
            FocusRoute.Refused,
            $"refused by all three routes; the foreground is {Describe(PInvoke.GetForegroundWindow())}");
    }

    /// <summary>
    /// Takes the keyboard off the foreground window by handing it to the
    /// shell's own desktop window. Direct route only: no attach, no injection.
    /// </summary>
    /// <remarks>
    /// Cloaking never moves the foreground, so a switch to an empty workspace
    /// left every keystroke going to the window that had just been hidden.
    /// </remarks>
    public static bool Unfocus()
    {
        HWND was = PInvoke.GetForegroundWindow();
        HWND desktop = PInvoke.GetShellWindow();
        if (desktop.IsNull)
        {
            desktop = PInvoke.GetDesktopWindow();
        }

        PInvoke.SetForegroundWindow(desktop);
        return PInvoke.GetForegroundWindow() != was;
    }

    /// <summary>
    /// One attempt, judged by where the foreground actually ended up.
    /// </summary>
    private static bool Try(HWND hwnd)
    {
        PInvoke.SetForegroundWindow(hwnd);
        return Settled(hwnd);
    }

    /// <summary>Waits only as long as it takes, up to <see cref="SettleMs"/>.</summary>
    /// <remarks>
    /// Bounded by the clock, not by counting sleeps: Thread.Sleep(2) sleeps
    /// for the timer period, 15.6 ms on this machine, so twenty of them were
    /// 312 ms per route and a refused focus cost 620 ms on the wm thread
    /// (measured in the trace, 2026-09-22: four times in eight seconds while
    /// a game on a hidden workspace kept taking the foreground).
    /// </remarks>
    private static bool Settled(HWND hwnd)
    {
        if (PInvoke.GetForegroundWindow() == hwnd)
        {
            return true;
        }

        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        while (System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds < SettleMs)
        {
            Thread.Sleep(PollMs);

            if (PInvoke.GetForegroundWindow() == hwnd)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Attach this thread's input queue to the one that owns the foreground;
    /// while they are attached, the call is allowed. The documented
    /// workaround, and the one the stack AkuWM replaces relies on.
    /// </summary>
    private static bool WithAttachedInput(HWND hwnd)
    {
        uint ours = PInvoke.GetCurrentThreadId();
        uint theirs;
        unsafe
        {
            theirs = PInvoke.GetWindowThreadProcessId(PInvoke.GetForegroundWindow(), null);
        }

        if (theirs == 0 || theirs == ours)
        {
            return false;
        }

        if (!PInvoke.AttachThreadInput(ours, theirs, true))
        {
            // Attaching to a higher-integrity thread is refused outright, which
            // is the interesting case: it is exactly what a game is.
            return false;
        }

        try
        {
            return Try(hwnd);
        }
        finally
        {
            PInvoke.AttachThreadInput(ours, theirs, false);
        }
    }

    /// <summary>
    /// Give this process the foreground right by making it the one that last
    /// received input.
    /// </summary>
    /// <remarks>
    /// A key that does nothing: F24 is not on any keyboard and no application
    /// binds it. Pressed and released in one call so nothing can observe it
    /// held down. This is the last resort, and it is logged whenever it runs --
    /// needing it means AkuWM did not hold the right when it should have.
    /// </remarks>
    private static bool WithInjectedInput(HWND hwnd)
    {
        var input = new INPUT[2];
        input[0].type = INPUT_TYPE.INPUT_KEYBOARD;
        input[0].Anonymous.ki.wVk = VIRTUAL_KEY.VK_F24;
        input[1].type = INPUT_TYPE.INPUT_KEYBOARD;
        input[1].Anonymous.ki.wVk = VIRTUAL_KEY.VK_F24;
        input[1].Anonymous.ki.dwFlags = KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP;

        unsafe
        {
            PInvoke.SendInput(input.AsSpan(), sizeof(INPUT));
        }

        return Try(hwnd);
    }

    private static string Describe(HWND hwnd) =>
        hwnd.IsNull ? "nothing" : $"{Win32Windows.ClassOf(hwnd)} \"{Win32Windows.TitleOf(hwnd)}\"";
}
