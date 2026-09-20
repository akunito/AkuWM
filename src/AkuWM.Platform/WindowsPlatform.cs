using AkuWM.Core.Logging;
using AkuWM.Core.Model;
using AkuWM.Core.Platform;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.HiDpi;

namespace AkuWM.Platform;

/// <summary>The real thing: <see cref="IPlatform"/> against Win32.</summary>
public sealed class WindowsPlatform : IPlatform, IPlatformActions, IDisposable
{
    private readonly Win32VirtualDesktops _desktops = new();

    /// <summary>The virtual-desktop probe, for doctor to report on.</summary>
    public Win32VirtualDesktops Desktops => _desktops;
    private readonly ImmersiveShell _shell = new();

    public IReadOnlyList<MonitorSnapshot> Monitors() => Win32Monitors.Enumerate();

    public IReadOnlyList<WindowSnapshot> Windows()
    {
        // Process names and elevation are cached for the length of one
        // enumeration: a full pass asks about the same twenty processes over
        // and over, and opening a process handle is the expensive part.
        Win32Windows.ForgetProcesses();
        return [.. Win32Windows.Enumerate().Select(WithDesktop)];
    }

    public WindowSnapshot? Window(WindowHandle handle)
    {
        WindowSnapshot? window = Win32Windows.Read(handle);
        return window is null ? null : WithDesktop(window);
    }

    // One COM call to the shell, not two. DesktopOf is consumed by exactly one
    // caller -- the human-readable `query windows` -- and it was being paid for
    // every window of every enumeration, an out-of-process call each.
    private WindowSnapshot WithDesktop(WindowSnapshot window) => window with
    {
        OnCurrentVirtualDesktop = _desktops.IsOnCurrentDesktop(window.Handle),
    };

    /// <summary>Which native virtual desktop, for the query that prints it.</summary>
    public string? VirtualDesktopOf(WindowHandle window) =>
        _desktops.DesktopOf(window)?.ToString("N")[..8];

    /// <summary>Hides or shows somebody else's window, through the shell.</summary>
    public string? SetCloak(WindowHandle window, bool cloaked) => _shell.SetCloak(window, cloaked);

    public int Place(IReadOnlyList<Placement> placements, bool activate = false) =>
        Win32Position.Place(placements, activate);

    public void SetMaximized(WindowHandle window, bool maximized)
    {
        if (maximized)
        {
            Win32Show.Maximize(window);
        }
        else
        {
            Win32Show.Restore(window);
        }
    }

    public void SetMinimized(WindowHandle window, bool minimized)
    {
        if (minimized)
        {
            Win32Show.Minimize(window);
        }
        else
        {
            Win32Show.Restore(window);
        }
    }

    public void SetTopmost(WindowHandle window, bool topmost) => Win32Position.SetTopmost(window, topmost);

    public bool Focus(WindowHandle window)
    {
        FocusResult result = Win32Focus.Focus(window);

        // Which route it took is worth knowing on the desk: whether attaching
        // the input queues works at all from the wm thread -- a thread that
        // never pumps messages -- is an open question, and this is the only
        // place that can answer it.
        if (result.Route is not (FocusRoute.Direct or FocusRoute.AlreadyFocused))
        {
            Log.Info($"focus {window}: {result.Route} -- {result.Detail}");
        }

        return result.Succeeded;
    }

    public void Dispose()
    {
        _desktops.Dispose();
        _shell.Dispose();
    }

    public WindowHandle Foreground() => new(PInvoke.GetForegroundWindow().Value);

    public (int X, int Y) CursorPosition() =>
        PInvoke.GetCursorPos(out System.Drawing.Point point) ? (point.X, point.Y) : (0, 0);

    /// <summary>
    /// Declares this process per-monitor DPI aware, before anything asks
    /// Windows for a rectangle.
    /// </summary>
    /// <remarks>
    /// Without it Windows virtualises every coordinate for the 150 % monitor
    /// and a layout computed from those numbers puts windows in the wrong
    /// place and the wrong size. A manifest would say the same thing; doing it
    /// in code keeps it true however the binary is published.
    /// </remarks>
    public static bool DeclareDpiAwareness() =>
        PInvoke.SetProcessDpiAwarenessContext((DPI_AWARENESS_CONTEXT)(-4));

    /// <summary>
    /// True when this process is per-monitor DPI aware, which it must be:
    /// without it Windows lies about every rectangle on the 150 % monitor.
    /// </summary>
    public static bool IsPerMonitorDpiAware()
    {
        DPI_AWARENESS_CONTEXT context = PInvoke.GetThreadDpiAwarenessContext();
        return PInvoke.AreDpiAwarenessContextsEqual(
            context, (DPI_AWARENESS_CONTEXT)(-4)); // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2
    }
}
