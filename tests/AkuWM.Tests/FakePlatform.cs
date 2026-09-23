using AkuWM.Core.Model;
using AkuWM.Core.Platform;

namespace AkuWM.Tests;

/// <summary>
/// A desk made of values, so the model is exercised on Linux.
/// </summary>
/// <remarks>
/// The snapshots the tests build with it are the shapes read off the real desk
/// -- a 3840x2160 monitor at 150 % and a vertical 1440x2560 at 125 %, the same
/// two this setup has -- so a test failing here means the decision is wrong,
/// not that the fake is unrealistic.
/// </remarks>
public sealed class FakePlatform : IPlatform, IPlatformActions
{
    public List<MonitorSnapshot> MonitorList { get; } = [];

    public List<WindowSnapshot> WindowList { get; } = [];

    public WindowHandle ForegroundWindow { get; set; } = WindowHandle.None;

    public (int X, int Y) Cursor { get; set; } = (0, 0);

    public IReadOnlyList<MonitorSnapshot> Monitors() => MonitorList;

    /// <summary>
    /// A copy, as the real one is: every call there builds a fresh list, and a
    /// caller that changes windows while walking them must not be able to
    /// break here in a way it could not break on the desk.
    /// </summary>
    public IReadOnlyList<WindowSnapshot> Windows() => [.. WindowList];

    public WindowSnapshot? Window(WindowHandle handle) =>
        WindowList.FirstOrDefault(w => w.Handle == handle);

    public CloakKind CloakOf(WindowHandle handle) => Window(handle)?.Cloak ?? CloakKind.None;

    public WindowHandle Foreground() => ForegroundWindow;

    public (int X, int Y) CursorPosition() => Cursor;

    // ---- the half that changes things -------------------------------------
    // It really changes them: the fake desk is mutated, so a test can assert
    // where a window ended up instead of which calls were made. A restore that
    // calls everything in the right order and leaves the window in the wrong
    // place is a restore that fails here, which is the point.

    /// <summary>Windows the fake refuses to uncloak, for the shell that lies.</summary>
    public HashSet<long> RefusesToUncloak { get; } = [];

    /// <summary>Where a placed window actually lands, when Windows does not give exactly what was asked.</summary>
    public Func<Rect, Rect>? Lands { get; set; }

    /// <summary>Windows that will not take the size they are given, like the real ones that do not.</summary>
    public HashSet<long> Stubborn { get; } = [];

    /// <summary>Windows the fake refuses to hide, reporting success as the shell does.</summary>
    public HashSet<long> RefusesToCloak { get; } = [];

    public List<string> Calls { get; } = [];

    public string? SetCloak(WindowHandle window, bool cloaked)
    {
        Calls.Add($"cloak {window.Value} {cloaked}");

        if (cloaked ? RefusesToCloak.Contains(window.Value) : RefusesToUncloak.Contains(window.Value))
        {
            return null; // reports success, does nothing: what the shell really does
        }

        Replace(window, w => w with
        {
            Cloak = cloaked ? w.Cloak | CloakKind.Shell : w.Cloak & ~CloakKind.Shell,
        });

        return null;
    }

    public int Place(IReadOnlyList<Placement> placements, bool activate = false)
    {
        foreach (Placement placement in placements)
        {
            Calls.Add($"place {placement.Window.Value} {placement.Frame}");

            if (Stubborn.Contains(placement.Window.Value))
            {
                continue;
            }

            Rect landed = Lands?.Invoke(placement.Frame) ?? placement.Frame;
            Replace(placement.Window, w => w with
            {
                FrameBounds = landed,
                WindowRect = landed.Inflate(9),

                // The monitor travels with the rectangle, as it does when
                // Windows is the one answering. Leaving it stale made a window
                // PLACED on another screen still read as being on the one it
                // left -- and a fullscreen window moved there stopped counting
                // as covering a screen, so the desk un-fullscreened it. The
                // same gap `Move` had for a window the person moves.
                Monitor = MonitorUnder(placement.Frame),
            });
        }

        return placements.Count;
    }

    /// <summary>The fake does not care which way they were sent.</summary>
    public int PlaceEach(IReadOnlyList<Placement> placements, bool activate = false) =>
        Place(placements, activate);

    public void SetMaximized(WindowHandle window, bool maximized)
    {
        Calls.Add($"maximize {window.Value} {maximized}");
        Replace(window, w => w with { IsMaximized = maximized, IsMinimized = false });
    }

    public void SetMinimized(WindowHandle window, bool minimized)
    {
        Calls.Add($"minimize {window.Value} {minimized}");
        Replace(window, w => w with { IsMinimized = minimized, IsMaximized = false });
    }

    /// <summary>Windows that strip HWND_TOPMOST off themselves, as Windows Terminal does.</summary>
    public HashSet<long> RefusesBand { get; } = [];

    public bool SetTopmost(WindowHandle window, bool topmost)
    {
        Calls.Add($"topmost {window.Value} {topmost}");
        if (topmost && RefusesBand.Contains(window.Value))
        {
            return false;
        }

        Replace(window, w => w with { IsTopmost = topmost });
        return true;
    }

    /// <summary>The outlines on screen, by window: frame, colour, band.</summary>
    public Dictionary<long, (Rect Frame, uint Colour, bool Topmost, int Corner, int Width)> Outlines { get; } = [];

    public void Outline(WindowHandle window, Rect? frame, uint colour, bool topmost, int corner, int width)
    {
        Calls.Add($"outline {window.Value} {(frame is null ? "off" : frame.ToString())}");
        if (frame is { } f)
        {
            Outlines[window.Value] = (f, colour, topmost, corner, width);
        }
        else
        {
            Outlines.Remove(window.Value);
        }
    }

    public List<WindowHandle> Raised { get; } = [];

    public List<WindowHandle> Lowered { get; } = [];

    public void Raise(WindowHandle window)
    {
        Calls.Add($"raise {window.Value}");
        // As the platform does: one in the band is above the tiles already.
        if (Window(window) is { IsTopmost: true })
        {
            return;
        }

        Raised.Add(window);
    }

    public void Lower(WindowHandle window)
    {
        Calls.Add($"lower {window.Value}");
        Lowered.Add(window);
    }

    /// <summary>The tiles asked to go behind the floating windows, per call.</summary>
    public List<WindowHandle> TilesLowered { get; } = [];

    public void RaiseOver(IReadOnlyList<WindowHandle> floating, IReadOnlyList<WindowHandle> tiles)
    {
        Calls.Add($"raise-over {floating.Count} over {tiles.Count}");
        for (int i = 0; i < floating.Count; i++)
        {
            Raise(floating[i]);
        }

        TilesLowered.AddRange(tiles);
    }

    /// <summary>What the shell was last asked to draw, by window.</summary>
    public Dictionary<WindowHandle, Decoration> Decorations { get; } = [];

    /// <summary>An old Windows build, which takes none of it.</summary>
    public bool RefusesDecoration { get; set; }

    public bool Decorate(WindowHandle window, Decoration decoration, bool force = false)
    {
        Calls.Add($"decorate{(force ? "!" : string.Empty)} {window.Value} {decoration.Border:x8} {decoration.Corners}");

        if (RefusesDecoration)
        {
            return false;
        }

        Decorations[window] = decoration;
        return true;
    }

    /// <summary>Windows that will not take the foreground (an elevated game holds it).</summary>
    public HashSet<long> RefusesFocus { get; } = [];

    public bool Focus(WindowHandle window)
    {
        Calls.Add($"focus {window.Value}");

        if (RefusesFocus.Contains(window.Value))
        {
            return false;
        }

        ForegroundWindow = window;
        return true;
    }

    public void Unfocus()
    {
        Calls.Add("unfocus");
        ForegroundWindow = WindowHandle.None;
    }

    /// <summary>Front to back, as far as the fake tracks it: only what PlaceBehind said.</summary>
    public List<(WindowHandle Window, WindowHandle Behind)> Behinds { get; } = [];

    public void PlaceBehind(WindowHandle window, WindowHandle behind)
    {
        Calls.Add($"behind {window.Value} {behind.Value}");
        Behinds.Add((window, behind));
    }

    /// <summary>Windows letting go of a window it had cloaked while it started.</summary>
    public void Uncloak(long handle)
    {
        int at = WindowList.FindIndex(w => w.Handle.Value == handle);
        WindowList[at] = WindowList[at] with { Cloak = CloakKind.None };
    }

    /// <summary>
    /// The application moves its OWN window, behind the window manager's back.
    /// </summary>
    /// <remarks>
    /// What a game does coming out of fullscreen: it recreates its swapchain
    /// and sizes itself over the next second or so, arriving at neither the
    /// old rectangle nor the one it was asked for.
    /// </remarks>
    public void ApplicationMoves(WindowHandle handle, Rect to) =>
        Replace(handle, w => w with { FrameBounds = to, WindowRect = to.Inflate(9) });

    private void Replace(WindowHandle handle, Func<WindowSnapshot, WindowSnapshot> change)
    {
        int at = WindowList.FindIndex(w => w.Handle == handle);
        if (at >= 0)
        {
            WindowList[at] = change(WindowList[at]);
        }
    }

    /// <summary>The main monitor of this desk: 4K at 150 %, taskbar at the bottom.</summary>
    /// <summary>Which monitor Windows would say a rectangle is on: the one under its middle.</summary>
    public MonitorHandle MonitorUnder(Rect frame)
    {
        int x = frame.X + (frame.Width / 2);
        int y = frame.Y + (frame.Height / 2);

        foreach (MonitorSnapshot monitor in MonitorList)
        {
            Rect bounds = monitor.Bounds;
            if (x >= bounds.Left && x < bounds.Right && y >= bounds.Top && y < bounds.Bottom)
            {
                return monitor.Handle;
            }
        }

        return MonitorList.Count > 0 ? MonitorList[0].Handle : new MonitorHandle(1);
    }

    public static MonitorSnapshot MainMonitor(long handle = 1) => new()
    {
        Handle = new MonitorHandle(handle),
        DeviceName = @"\\.\DISPLAY2",
        FriendlyName = "Odyssey G70NC",
        HardwareId = "SAM7233",
        Bounds = new Rect(0, 0, 3840, 2160),
        WorkArea = new Rect(0, 42, 3840, 2118),
        Dpi = 144,
        IsPrimary = true,
    };

    /// <summary>The second one: portrait, 125 %.</summary>
    public static MonitorSnapshot SecondMonitor(long handle = 2) => new()
    {
        Handle = new MonitorHandle(handle),
        DeviceName = @"\\.\DISPLAY1",
        FriendlyName = "RGB-27QHDS",
        HardwareId = "NSL2711",
        Bounds = new Rect(3840, -408, 1440, 2560),
        WorkArea = new Rect(3840, -373, 1440, 2525),
        Dpi = 120,
        IsPrimary = false,
    };

    public static WindowSnapshot Window(
        long handle,
        string process,
        string className = "Window",
        string title = "a window",
        Rect? frame = null,
        MonitorHandle? monitor = null,
        bool resizable = true,
        bool minimized = false,
        bool maximized = false,
        bool elevated = false,
        CloakKind cloak = CloakKind.None,
        bool onCurrentDesktop = true,
        bool perMonitorDpi = true)
    {
        Rect bounds = frame ?? new Rect(100, 100, 800, 600);
        return new WindowSnapshot
        {
            Handle = new WindowHandle(handle),
            ProcessId = (uint)handle,
            ProcessName = process,
            ClassName = className,
            Title = title,
            WindowRect = bounds.Inflate(9),
            FrameBounds = bounds,
            Monitor = monitor ?? new MonitorHandle(1),
            IsVisible = true,
            IsMinimized = minimized,
            IsMaximized = maximized,
            Cloak = cloak,
            IsTopmost = false,
            IsResizable = resizable,
            IsToolWindow = false,
            IsElevated = elevated,
            OnCurrentVirtualDesktop = onCurrentDesktop,
            PerMonitorDpi = perMonitorDpi,
        };
    }
}
