using AkuWM.Core.Model;

namespace AkuWM.Core.Platform;

/// <summary>
/// Everything the model is allowed to ask the operating system.
/// </summary>
/// <remarks>
/// The whole point of this interface is that <c>AkuWM.Core</c> never
/// references Win32: the model, the rules and the layout are driven by a fake
/// platform in the tests, on Linux, with the values that were read off the
/// real desk. M1 needs only the reading half; the half that moves, cloaks and
/// focuses windows is added in M2, when AkuWM stops watching.
/// </remarks>
public interface IPlatform
{
    /// <summary>The displays, primary first.</summary>
    IReadOnlyList<MonitorSnapshot> Monitors();

    /// <summary>
    /// The top-level windows, in z-order (front first), already filtered to
    /// the ones a window manager has any business with.
    /// </summary>
    IReadOnlyList<WindowSnapshot> Windows();

    /// <summary>One window, or null when the handle is gone.</summary>
    WindowSnapshot? Window(WindowHandle handle);

    /// <summary>What has the keyboard focus, as the OS sees it.</summary>
    WindowHandle Foreground();

    /// <summary>Where the pointer is, in virtual-screen pixels.</summary>
    (int X, int Y) CursorPosition();
}

/// <summary>
/// The half of the platform that changes things.
/// </summary>
/// <remarks>
/// Everything here is reversible, and the reverse is what the rescue path
/// uses: a window manager that hides and moves other people's windows has to
/// be able to put every one of them back exactly as it found it, from a
/// process that may be starting up after the one that moved them died.
/// </remarks>
public interface IPlatformActions
{
    /// <summary>Hides or shows a window through the shell's cloak.</summary>
    /// <returns>Null on success, or why it failed.</returns>
    string? SetCloak(WindowHandle window, bool cloaked);

    /// <summary>
    /// Moves and sizes windows in one batch, by their visible frames.
    /// </summary>
    /// <returns>How many were placed.</returns>
    int Place(IReadOnlyList<Placement> placements, bool activate = false);

    /// <summary>Maximises or un-maximises.</summary>
    void SetMaximized(WindowHandle window, bool maximized);

    /// <summary>Minimises, or brings back from the taskbar.</summary>
    void SetMinimized(WindowHandle window, bool minimized);

    /// <summary>Puts a window in the always-on-top band, or takes it out.</summary>
    void SetTopmost(WindowHandle window, bool topmost);

    /// <summary>Gives a window the keyboard focus.</summary>
    /// <returns>True when the foreground window is this one afterwards.</returns>
    bool Focus(WindowHandle window);
}

/// <summary>The shell's taskbar, for the one thing a window manager tells it.</summary>
public interface ITaskbar
{
    /// <summary>
    /// Tells the taskbar a window is covering the screen, so it drops behind.
    /// </summary>
    /// <remarks>
    /// It has to be told again when the window stops, or the bar stays hidden
    /// after the game is closed -- which looks exactly like a broken shell,
    /// and is the kind of thing a person blames on the window manager,
    /// correctly.
    /// </remarks>
    bool MarkFullscreen(WindowHandle window, bool fullscreen);
}

/// <summary>
/// A platform event, as the hooks deliver it.
/// </summary>
/// <remarks>
/// The callbacks translate and enqueue, nothing more: a hook that takes longer
/// than <c>LowLevelHooksTimeout</c> is removed by Windows without asking, and a
/// <c>SetWinEventHook</c> callback runs on the thread that owns the hook.
/// </remarks>
public enum PlatformEventKind
{
    WindowCreated,
    WindowDestroyed,
    WindowShown,
    WindowHidden,
    WindowCloaked,
    WindowUncloaked,
    WindowMoved,
    WindowMinimizeStart,
    WindowMinimizeEnd,
    WindowTitleChanged,
    ForegroundChanged,
    DisplayChanged,
    SettingsChanged,
    PowerSuspend,
    PowerResume,
}

/// <param name="Kind">What happened.</param>
/// <param name="Handle">The window it happened to, or none for a display or power event.</param>
/// <param name="At">When the callback saw it, for the latency bench.</param>
public readonly record struct PlatformEvent(PlatformEventKind Kind, WindowHandle Handle, long At)
{
    public override string ToString() => $"{Kind} {Handle}";
}

/// <summary>The source of platform events; started and stopped by the host.</summary>
public interface IPlatformEvents
{
    /// <summary>Called on the platform thread. Must return in well under a millisecond.</summary>
    event Action<PlatformEvent>? Event;

    void Start();

    void Stop();
}
