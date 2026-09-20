using AkuWM.Core.Model;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace AkuWM.Platform;

/// <param name="Succeeded">The window ended up in the foreground.</param>
/// <param name="NeededAttach">
/// The plain call was refused and the input queues had to be attached first.
/// Worth logging: it means AkuWM did not hold the foreground right at that
/// moment, which is usually a sign the chord was not consumed properly.
/// </param>
/// <param name="Detail">What happened, for the spike and the log.</param>
public readonly record struct FocusResult(bool Succeeded, bool NeededAttach, string Detail);

/// <summary>
/// Putting a window in the foreground, which Windows does not simply let a
/// program do.
/// </summary>
/// <remarks>
/// <c>SetForegroundWindow</c> only works for a process that holds the
/// foreground right: the one the user is interacting with, the one that just
/// received input, or a few other documented cases. AkuWM gets it the honest
/// way -- its hook has just consumed the keystroke the user pressed -- and
/// falls back to attaching to the foreground thread's input queue when that is
/// not enough, which is the documented workaround and what spike S2 measures
/// against an elevated window.
/// </remarks>
public static class Win32Focus
{
    public static FocusResult Focus(WindowHandle handle)
    {
        var hwnd = new HWND((IntPtr)handle.Value);

        if (!PInvoke.IsWindow(hwnd))
        {
            return new FocusResult(false, false, "the window is gone");
        }

        if (PInvoke.SetForegroundWindow(hwnd))
        {
            return new FocusResult(
                PInvoke.GetForegroundWindow() == hwnd, false, "SetForegroundWindow was enough");
        }

        // Attach this thread's input queue to the one that currently owns the
        // foreground; while they are attached, the call is allowed.
        uint ours = PInvoke.GetCurrentThreadId();
        uint theirs;
        unsafe
        {
            theirs = PInvoke.GetWindowThreadProcessId(PInvoke.GetForegroundWindow(), null);
        }

        if (theirs == 0 || theirs == ours)
        {
            return new FocusResult(false, false, "refused, and there is no other input queue to attach to");
        }

        bool attached = PInvoke.AttachThreadInput(ours, theirs, true);
        try
        {
            PInvoke.SetForegroundWindow(hwnd);
        }
        finally
        {
            if (attached)
            {
                PInvoke.AttachThreadInput(ours, theirs, false);
            }
        }

        bool ok = PInvoke.GetForegroundWindow() == hwnd;
        return new FocusResult(ok, true, ok
            ? "attaching the input queues did it"
            : "refused even with the input queues attached");
    }
}
