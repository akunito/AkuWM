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
    {
        Platform = new FakePlatform();
        Platform.MonitorList.Add(FakePlatform.MainMonitor());
        Platform.MonitorList.Add(FakePlatform.SecondMonitor());

        Desk = new Desk(config ?? Configuration(), clock: () => _now);
        Desk.SetMonitors(Platform.Monitors());
    }

    /// <summary>Lets time pass without taking any.</summary>
    public void Wait(int milliseconds) => _now += milliseconds;

    public FakePlatform Platform { get; }

    public Desk Desk { get; }

    /// <summary>The two monitors of this machine and ten workspaces, as the imported configuration has them.</summary>
    public static AkuWmConfig Configuration() => new()
    {
        General = new GeneralConfig { ToggleWorkspaceOnRefocus = true },
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
        string title = "a window")
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
        Sync();
        return window;
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

        Platform.Place(redraw.Place);

        foreach (WindowHandle handle in redraw.Hide)
        {
            Platform.SetCloak(handle, true);
        }

        foreach (WindowHandle handle in redraw.Show)
        {
            Platform.SetCloak(handle, false);
        }

        foreach ((WindowHandle handle, bool topmost) in redraw.Band)
        {
            Platform.SetTopmost(handle, topmost);
        }

        Desk.Applied(redraw);
        Sync();
        return redraw;
    }

    public Rect FrameOf(long handle) => Platform.Window(new WindowHandle(handle))!.FrameBounds;

    public bool IsHidden(long handle) =>
        Platform.Window(new WindowHandle(handle))!.Cloak.HasFlag(CloakKind.Shell);

    public DeskWindow? Managed(long handle) => Desk.Window(new WindowHandle(handle));

    public static WindowHandle W(long handle) => new(handle);
}
