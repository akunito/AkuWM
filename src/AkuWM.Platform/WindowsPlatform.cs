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

        // The enumeration is the one place that asks the shell, and it is also
        // the one that runs when a native virtual desktop changes. Cleared
        // first so a handle Windows has handed to somebody else since does not
        // keep an answer that was about a different window.
        _onThisDesktop.Clear();
        return [.. Win32Windows.Enumerate().Select(WithDesktop)];
    }

    /// <summary>
    /// Which native virtual desktop each window was last seen on.
    /// </summary>
    /// <remarks>
    /// Filled by the full enumeration, read by the single-window path. The
    /// answer only changes when the person switches native virtual desktops,
    /// which raises an event of its own and re-enumerates -- and asking the
    /// shell is an out-of-process COM call, paid once per window MOVE, which
    /// during an Alt+drag is mouse-rate, on the wm thread.
    /// </remarks>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<WindowHandle, bool> _onThisDesktop = new();

    public CloakKind CloakOf(WindowHandle handle) =>
        Win32Windows.CloakOf(new Windows.Win32.Foundation.HWND((IntPtr)handle.Value));

    public WindowSnapshot? Window(WindowHandle handle)
    {
        if (Win32Windows.Read(handle) is not { } window)
        {
            _onThisDesktop.TryRemove(handle, out _);
            return null;
        }

        // Only a window nobody has enumerated yet costs the call.
        if (_onThisDesktop.TryGetValue(handle, out bool known))
        {
            return window with { OnCurrentVirtualDesktop = known };
        }

        return WithDesktop(window);
    }

    // One COM call to the shell, not two. DesktopOf is consumed by exactly one
    // caller -- the human-readable `query windows` -- and it was being paid for
    // every window of every enumeration, an out-of-process call each.
    private WindowSnapshot WithDesktop(WindowSnapshot window)
    {
        bool here = _desktops.IsOnCurrentDesktop(window.Handle);
        _onThisDesktop[window.Handle] = here;
        return window with { OnCurrentVirtualDesktop = here };
    }

    /// <summary>Which native virtual desktop, for the query that prints it.</summary>
    public string? VirtualDesktopOf(WindowHandle window) =>
        _desktops.DesktopOf(window)?.ToString("N")[..8];

    /// <summary>Hides or shows somebody else's window, through the shell.</summary>
    public string? SetCloak(WindowHandle window, bool cloaked) => _shell.SetCloak(window, cloaked);

    public int Place(IReadOnlyList<Placement> placements, bool activate = false) =>
        Win32Position.Place(placements, activate);

    public int PlaceEach(IReadOnlyList<Placement> placements, bool activate = false) =>
        Win32Position.PlaceEachAsync(placements, activate);

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

    public bool SetTopmost(WindowHandle window, bool topmost) => Win32Position.SetTopmost(window, topmost);

    public void Raise(WindowHandle window)
    {
        // A window still in the always-on-top band is above every tile
        // already; read now, not from the model, which learned the band when
        // it asked for it and not when the application took it off.
        if (!Win32Position.IsTopmost(window))
        {
            Win32Position.Raise(window);
        }
    }

    public void Lower(WindowHandle window) => Win32Position.Lower(window);

    public void PlaceBehind(WindowHandle window, WindowHandle behind) => Win32Position.PlaceBehind(window, behind);

    public void Unfocus() => Win32Focus.Unfocus();

    public void Outline(WindowHandle window, Rect? frame, uint colour, bool topmost) =>
        Win32Outline.Set(window, frame, colour, topmost);

    public bool Decorate(WindowHandle window, Decoration decoration, bool force = false) =>
        Win32Decorations.Apply(new Windows.Win32.Foundation.HWND((IntPtr)window.Value), decoration, force);

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
