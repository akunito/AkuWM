using AkuWM.Core.Logging;
using AkuWM.Core.Model;

namespace AkuWM.Core.Desk;

/// <summary>
/// An elevation request made with a window that is not the foreground one
/// (an installer or an updater working behind what the person is doing) does
/// not get the secure desktop: consent.exe parks a MINIMIZED window of class
/// "$$$Secure UAP Dummy Window Class For Interim Dialog" and the only sign is
/// a shield button on the taskbar of the primary monitor (measured 2026-10-02:
/// iconic at -21333,-21333, style 0xb4c80000, desktop stays "default", the
/// request waits for ever -- and outlives the process that asked). A request
/// with no window (hwnd 0) goes straight to the secure desktop, at any age of
/// the asking process; only that one was ever seen.
/// </summary>
/// <remarks>
/// WM_SYSCOMMAND/SC_RESTORE posted to that window is what a click on the
/// shield does: the secure desktop came 250 ms later, from a process with or
/// without uiAccess. ShowWindow(SW_RESTORE) and SetForegroundWindow do
/// nothing to it. Two raised together are queued by Windows itself (one
/// secure desktop, answered one after the other). Never while a game has the
/// foreground: the switch of desktop would take it out of the game; those
/// wait for the foreground to move on.
/// </remarks>
public sealed class ElevationPromptRaiser
{
    private readonly Func<WindowHandle, bool> _isPendingPrompt;
    private readonly Func<bool> _gameInFront;
    private readonly Action<WindowHandle> _raise;
    private readonly HashSet<WindowHandle> _raised = [];
    private readonly HashSet<WindowHandle> _deferred = [];

    public ElevationPromptRaiser(Func<WindowHandle, bool> isPendingPrompt, Func<bool> gameInFront, Action<WindowHandle> raise)
    {
        _isPendingPrompt = isPendingPrompt;
        _gameInFront = gameInFront;
        _raise = raise;
    }

    /// <summary>True while a prompt is remembered: the destroy events are worth a lookup only then.</summary>
    public bool Watching => _raised.Count + _deferred.Count > 0;

    /// <summary>Prompts waiting for a game to leave the foreground.</summary>
    public int Deferred => _deferred.Count;

    /// <summary>A window was shown or minimised; if it is a parked prompt it is raised, once.</summary>
    public void Seen(WindowHandle handle)
    {
        if (handle.IsNone || _raised.Contains(handle) || !_isPendingPrompt(handle))
        {
            return;
        }

        if (_gameInFront())
        {
            if (_deferred.Add(handle))
            {
                Log.Info($"elevation prompt {handle} is waiting on the taskbar: a game has the foreground, it is raised when the game lets go");
            }

            return;
        }

        Raise(handle);
    }

    /// <summary>The foreground moved: the prompts a game was holding back are raised if it let go.</summary>
    public void ForegroundChanged()
    {
        if (_deferred.Count == 0 || _gameInFront())
        {
            return;
        }

        WindowHandle[] waiting = [.. _deferred];
        _deferred.Clear();
        foreach (WindowHandle handle in waiting)
        {
            if (_isPendingPrompt(handle))
            {
                Raise(handle);
            }
        }
    }

    /// <summary>A window is gone: its handle can be reused by another prompt.</summary>
    public void WindowGone(WindowHandle handle)
    {
        _raised.Remove(handle);
        _deferred.Remove(handle);
    }

    private void Raise(WindowHandle handle)
    {
        _raised.Add(handle);
        Log.Info($"elevation prompt {handle} was parked on the taskbar: raised");
        _raise(handle);
    }
}
