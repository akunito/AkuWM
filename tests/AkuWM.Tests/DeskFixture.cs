using AkuWM.Core.Config;
using AkuWM.Core.Desk;
using AkuWM.Core.Model;

namespace AkuWM.Tests;

/// <summary>
/// A desk made of values, plus the loop that connects it to one: compute,
/// apply, look again.
/// </summary>
/// <remarks>
/// The point is that the tests exercise the real cycle rather than one half of
/// it. A model that produces a correct redraw and then does not notice the
/// window moved would pass every test written against <c>Compute</c> alone,
/// and would re-send the same move for ever on a real desk.
/// </remarks>
public sealed class DeskFixture
{
    private long _now;

    public DeskFixture(AkuWmConfig? config = null)
        : this(config ?? Configuration(), FakePlatform.MainMonitor(), FakePlatform.SecondMonitor())
    {
    }

    /// <summary>
    /// A desk with the screens named here instead of this machine's two.
    /// </summary>
    /// <remarks>
    /// The arrangement of the monitors is the one thing the driven suite on
    /// the real desk can never vary: it has the screens it has. Every geometry
    /// rule -- tiles filling the work area, a window staying on the monitor it
    /// was dropped on, gaps that scale with the DPI -- has to hold for a
    /// portrait screen, for one to the LEFT of the main one, above it, at
    /// another scale, and for three of them.
    /// </remarks>
    public DeskFixture(AkuWmConfig config, params MonitorSnapshot[] monitors)
    {
        Platform = new FakePlatform();
        foreach (MonitorSnapshot monitor in monitors)
        {
            Platform.MonitorList.Add(monitor);
        }

        Desk = new Desk(config, clock: () => _now);
        Desk.ChecksHandlesWith(h => Platform.Window(h) is not null);
        Desk.ReadsTheCursorWith(() => Platform.Cursor);
        Desk.SetMonitors(Platform.Monitors());
    }

    /// <summary>Lets time pass without taking any.</summary>
    public void Wait(int milliseconds) => _now += milliseconds;

    /// <summary>The screens as the platform now lists them, and the burst is over: placements may follow.</summary>
    public void Screens()
    {
        Desk.SetMonitors(Platform.Monitors());
        Wait(Desk.MonitorSettleMs);
    }

    public FakePlatform Platform { get; }

    public Desk Desk { get; }

    /// <summary>The two monitors of this machine and ten workspaces, as the imported configuration has them.</summary>
    public static AkuWmConfig Configuration() => new()
    {
        // The pointer sits at 0,0 in this fixture unless a test moves it, so
        // "open under the pointer" (the desk's default) would put every new
        // window on the main screen; the tests that want it turn it on.
        General = new GeneralConfig { ToggleWorkspaceOnRefocus = true, OpenUnderPointer = false },
        Gaps = new GapsConfig { Inner = 8, Outer = [0, 0, 0, 0], ScaleWithDpi = true },
        Layout = new LayoutConfig { DefaultDirection = "auto", FloatUnresizable = true },
        Monitors =
        [
            new MonitorConfig { Id = "main", Match = new MonitorMatch { Edid = "SAM7233" } },
            new MonitorConfig { Id = "second", Match = new MonitorMatch { Edid = "NSL2711" } },
        ],
        Workspaces =
        [
            new WorkspaceConfig { Name = "11", Monitor = "main" },
            new WorkspaceConfig { Name = "12", Monitor = "main" },
            new WorkspaceConfig { Name = "13", Monitor = "main" },
            new WorkspaceConfig { Name = "21", Monitor = "second" },
            new WorkspaceConfig { Name = "22", Monitor = "second" },
        ],
        Rules = [],
    };

    public WindowSnapshot Open(
        long handle,
        string process = "zen",
        MonitorHandle? monitor = null,
        Rect? frame = null,
        bool resizable = true,
        bool elevated = false,
        string className = "Window",
        string title = "a window",
        bool sync = true)
    {
        WindowSnapshot window = FakePlatform.Window(
            handle,
            process,
            className: className,
            title: title,
            frame: frame ?? new Rect(200, 200, 900, 700),
            monitor: monitor ?? new MonitorHandle(1),
            resizable: resizable,
            elevated: elevated);

        Platform.WindowList.Add(window);

        // `sync: false` for a window that is already on the desk when AkuWM
        // starts: those all arrive in ONE sync, and the desk tells them apart
        // from a window opened later by exactly that.
        if (sync)
        {
            Sync();
        }

        return window;
    }

    /// <summary>The person moves or resizes a window; Windows says where it went.</summary>
    /// <remarks>
    /// The monitor travels with the rectangle, because on a real desk it does:
    /// the platform reads it with MonitorFromWindow every time it looks. This
    /// used to keep the old one, so a window dragged to the other screen was
    /// still reported as being on the one it left -- and the desk could not
    /// have noticed the difference no matter what it did.
    /// </remarks>
    /// <param name="at">Where the hand is on the window. Defaults to a title-bar grab.</param>
    public void Move(long handle, Rect to, (int X, int Y)? hand = null)
    {
        int at = Platform.WindowList.FindIndex(w => w.Handle.Value == handle);
        Platform.WindowList[at] = Platform.WindowList[at] with
        {
            FrameBounds = to,
            WindowRect = to,
            Monitor = Platform.MonitorUnder(to),
        };

        // The pointer goes with it, near the top-left, where a title bar is.
        // A person moving a window has their hand on it, and the desk anchors
        // a window that changes size on the point they are holding -- so a
        // fixture whose cursor stays at 0,0 for ever is testing a drag nobody
        // is doing.
        Platform.Cursor = hand ?? (to.X + 40, to.Y + 40);
        Sync();
    }


    public void Close(long handle)
    {
        Platform.WindowList.RemoveAll(w => w.Handle.Value == handle);
        Sync();
    }

    public void Sync() => Desk.Sync(Platform.Windows());

    /// <summary>
    /// One full turn of the crank: what has to change, change it, look again.
    /// </summary>
    public Redraw Turn()
    {
        Redraw redraw = Desk.Compute();

        // Before the placements, exactly as DeskApplier does it.
        foreach (WindowHandle handle in redraw.Restore)
        {
            Platform.SetMinimized(handle, false);
        }

        foreach (WindowHandle handle in redraw.Unmaximize)
        {
            Platform.SetMaximized(handle, false);
        }

        foreach (WindowHandle handle in redraw.Remaximize)
        {
            Platform.SetMaximized(handle, false);
            Platform.SetMaximized(handle, true);
        }

        Platform.Place(redraw.Place);

        foreach (WindowHandle handle in redraw.Hide)
        {
            Platform.SetCloak(handle, true);
        }

        foreach (WindowHandle handle in redraw.Show)
        {
            Platform.SetCloak(handle, false);
        }

        HashSet<WindowHandle>? unbanded = null;
        foreach ((WindowHandle handle, bool topmost) in redraw.Band)
        {
            if (!Platform.SetTopmost(handle, topmost))
            {
                (unbanded ??= []).Add(handle);
            }
        }

        foreach (WindowHandle handle in redraw.Raise)
        {
            Platform.Raise(handle);
        }

        foreach (WindowHandle handle in redraw.Lower)
        {
            Platform.Lower(handle);
        }

        HashSet<WindowHandle>? undecorated = null;
        foreach ((WindowHandle handle, Decoration how) in redraw.Decorate)
        {
            if (!Platform.Decorate(handle, how, redraw.Forced.Contains(handle)))
            {
                (undecorated ??= []).Add(handle);
            }
        }

        foreach ((WindowHandle handle, WindowHandle behind) in redraw.Behind)
        {
            Platform.PlaceBehind(handle, behind);
        }

        foreach (Outline o in redraw.Outline)
        {
            Platform.Outline(o.Window, o.Frame, o.Colour, o.Topmost, o.Corner, o.Width);
        }

        // The focus half of the applier, which this fixture used to skip: the
        // model then only ever learned the focus from direct calls, and the
        // whole class of "where does the next window open" faults the desk
        // showed could not be written down here.
        bool focusRefused = false;
        if (!redraw.Focus.IsNone)
        {
            focusRefused = !Platform.Focus(redraw.Focus);
        }
        else if (redraw.Unfocus)
        {
            Platform.Unfocus();
        }

        Desk.Applied(redraw, undecorated: undecorated, focusRefused: focusRefused, unbanded: unbanded);
        Sync();
        return redraw;
    }

    /// <summary>Windows hands the foreground to a window: what the hook reports.</summary>
    public bool Foreground(long handle)
    {
        Platform.ForegroundWindow = W(handle);
        return Desk.Focus(W(handle));
    }

    public Rect FrameOf(long handle) => Platform.Window(new WindowHandle(handle))!.FrameBounds;

    public bool IsHidden(long handle) =>
        Platform.Window(new WindowHandle(handle))!.Cloak.HasFlag(CloakKind.Shell);

    public DeskWindow? Managed(long handle) => Desk.Window(new WindowHandle(handle));

    public static WindowHandle W(long handle) => new(handle);
}
