using System.Text.Json.Nodes;
using AkuWM.Core.Desk;
using AkuWM.Core.Layout;
using AkuWM.Core.Model;

namespace AkuWM.Core.Compat;

/// <summary>
/// The desk, in the JSON the bar and the existing scripts already parse.
/// </summary>
/// <remarks>
/// <para>
/// A wire format AkuWM imitates, captured black-box from the running window
/// manager it replaces (plan section 7). It exists so that a bar nobody wants
/// to rewrite and 1,961 lines of working AutoHotkey keep working while what is
/// underneath is replaced -- which is the whole shape of M2: the suites guard
/// the new window manager before the new input layer exists.
/// </para>
/// <para>
/// <strong>Key order matters here and nowhere else in AkuWM.</strong> The
/// scripts parse this with regular expressions, not a JSON parser, and those
/// expect <c>type, id, parentId, hasFocus</c> together, and
/// <c>handle, title, className, processName</c> together. Reordering these
/// properties is a breaking change that no compiler will catch, so they are
/// built by hand, in order, with a test that says so.
/// </para>
/// <para>
/// One deliberate simplification: a workspace's children are its windows,
/// flat. The original nests split containers in here, which is why the
/// scripts that read this had to scan linearly for windows after each
/// workspace rather than trusting <c>parentId</c>. Flat makes
/// <c>parentId</c> the workspace for every window, which those scripts handle
/// and which is what a reader would expect anyway.
/// </para>
/// </remarks>
public static class GlazeView
{
    public static JsonObject Monitor(Desk.Desk desk, DeskMonitor monitor)
    {
        MonitorSnapshot snapshot = monitor.Snapshot;
        var children = new JsonArray();

        foreach (Workspace workspace in monitor.Workspaces)
        {
            children.Add(Workspace(desk, monitor, workspace));
        }

        return new JsonObject
        {
            ["type"] = "monitor",
            ["id"] = monitor.Id.ToString(),
            ["parentId"] = null,
            ["children"] = children,
            ["childFocusOrder"] = FocusOrder(monitor),
            ["hasFocus"] = ReferenceEquals(desk.FocusedMonitor, monitor),
            ["width"] = snapshot.Bounds.Width,
            ["height"] = snapshot.Bounds.Height,
            ["x"] = snapshot.Bounds.X,
            ["y"] = snapshot.Bounds.Y,
            ["dpi"] = snapshot.Dpi,
            ["scaleFactor"] = snapshot.ScaleFactor,
            ["handle"] = snapshot.Handle.Value,
            ["deviceName"] = snapshot.DeviceName,
            ["devicePath"] = snapshot.HardwareId,
            ["hardwareId"] = snapshot.HardwareId,
            ["workingRect"] = Rect(snapshot.WorkArea),

            // AkuWM's own, and the reason the roles exist: a monitor is what
            // it is, not where it happens to be in the enumeration.
            ["role"] = monitor.Role,
        };
    }

    public static JsonObject Workspace(Desk.Desk desk, DeskMonitor monitor, Workspace workspace)
    {
        var children = new JsonArray();

        foreach (WindowHandle handle in Ordered(workspace, monitor))
        {
            if (desk.Window(handle) is { Managed: true } window)
            {
                children.Add(Window(desk, workspace, window));
            }
        }

        Rect area = monitor.TilingArea;

        return new JsonObject
        {
            ["type"] = "workspace",
            ["id"] = workspace.Id.ToString(),
            ["name"] = workspace.Name,
            ["displayName"] = workspace.DisplayName ?? workspace.Name,
            ["parentId"] = monitor.Id.ToString(),
            ["children"] = children,
            ["childFocusOrder"] = FocusOrder(desk, workspace),
            ["hasFocus"] = workspace.Contains(desk.Focused),
            ["isDisplayed"] = workspace.Displayed,
            ["width"] = area.Width,
            ["height"] = area.Height,
            ["x"] = area.X,
            ["y"] = area.Y,
            ["tilingDirection"] = Direction(workspace.Tiling.Root?.Direction ?? workspace.Direction),
        };
    }

    public static JsonObject Window(Desk.Desk desk, Workspace? workspace, DeskWindow window)
    {
        WindowSnapshot snapshot = window.Snapshot;
        Rect frame = snapshot.FrameBounds;
        (int top, int right, int bottom, int left) = snapshot.BorderDelta;

        // The order of these is load-bearing: see the remarks above.
        return new JsonObject
        {
            ["type"] = "window",
            ["id"] = window.Id.ToString(),
            ["parentId"] = workspace?.Id.ToString(),
            ["hasFocus"] = desk.Focused == window.Handle,
            ["tilingSize"] = TilingSize(desk, workspace, window),
            ["width"] = frame.Width,
            ["height"] = frame.Height,
            ["x"] = frame.X,
            ["y"] = frame.Y,
            ["state"] = new JsonObject { ["type"] = State(window.State) },
            ["prevState"] = window.PreviousState == window.State
                ? null
                : new JsonObject { ["type"] = State(window.PreviousState) },
            ["displayState"] = window.Hidden ? "hidden" : "shown",
            ["sticky"] = window.Sticky,
            ["borderDelta"] = new JsonObject
            {
                ["top"] = top,
                ["right"] = right,
                ["bottom"] = bottom,
                ["left"] = left,
            },
            ["floatingPlacement"] = Rect(window.FloatingRect ?? frame),
            ["handle"] = snapshot.Handle.Value,
            ["title"] = snapshot.Title,
            ["className"] = snapshot.ClassName,
            ["processName"] = snapshot.ProcessName,
            ["activeDrag"] = null,
        };
    }

    /// <summary>
    /// Every window of the desk, flat, as <c>query windows</c> answers.
    /// </summary>
    /// <remarks>
    /// Each window once. A sticky window has two good reasons to be listed --
    /// it belongs to the monitor, and it is drawn on the workspace that is on
    /// screen -- and it is still one window. Listing it twice makes the
    /// raise-or-launch scripts count two of something there is one of, which
    /// is how a gesture ends up opening a second copy.
    /// </remarks>
    public static JsonArray Windows(Desk.Desk desk)
    {
        var windows = new JsonArray();
        var seen = new HashSet<WindowHandle>();

        foreach (DeskMonitor monitor in desk.Monitors)
        {
            foreach (Workspace workspace in monitor.Workspaces)
            {
                foreach (WindowHandle handle in Ordered(workspace, monitor))
                {
                    if (desk.Window(handle) is { Managed: true } window && seen.Add(handle))
                    {
                        windows.Add(Window(desk, workspace, window));
                    }
                }
            }

            // Sticky windows of a monitor that is showing nothing: the loop
            // above reaches them through the displayed workspace, and there
            // is not one.
            foreach (WindowHandle handle in monitor.Sticky)
            {
                if (desk.Window(handle) is { Managed: true } window && seen.Add(handle))
                {
                    windows.Add(Window(desk, monitor.Displayed, window));
                }
            }
        }

        return windows;
    }

    public static JsonArray Monitors(Desk.Desk desk)
    {
        var monitors = new JsonArray();

        foreach (DeskMonitor monitor in desk.Monitors)
        {
            monitors.Add(Monitor(desk, monitor));
        }

        return monitors;
    }

    public static JsonArray Workspaces(Desk.Desk desk)
    {
        var workspaces = new JsonArray();

        foreach (DeskMonitor monitor in desk.Monitors)
        {
            foreach (Workspace workspace in monitor.Workspaces)
            {
                workspaces.Add(Workspace(desk, monitor, workspace));
            }
        }

        return workspaces;
    }

    /// <summary>
    /// A workspace's windows in the order they are drawn: tiled first, then
    /// floating, then whatever is covering the screen.
    /// </summary>
    /// <remarks>
    /// Sticky windows come with the monitor, not the workspace, so they are
    /// added by the caller that knows which monitor is being described.
    /// </remarks>
    private static IEnumerable<WindowHandle> Ordered(Workspace workspace, DeskMonitor monitor) =>
        workspace.Tiling.Windows
            .Concat(workspace.Floating)
            .Concat(workspace.Fullscreen.IsNone ? [] : new[] { workspace.Fullscreen })
            .Concat(ReferenceEquals(workspace, monitor.Displayed) ? monitor.Sticky : [])
            .Distinct();

    private static JsonObject Rect(Rect rect) => new()
    {
        ["left"] = rect.Left,
        ["top"] = rect.Top,
        ["right"] = rect.Right,
        ["bottom"] = rect.Bottom,
        ["width"] = rect.Width,
        ["height"] = rect.Height,
        ["x"] = rect.X,
        ["y"] = rect.Y,
    };

    /// <summary>
    /// The share of its split a tiled window has, as a plain number.
    /// </summary>
    /// <remarks>
    /// Built by the implicit conversion rather than <c>JsonValue.Create</c>:
    /// the generic one produces a node that needs a serializer context to
    /// write itself, which throws at the moment the bar asks for the desk --
    /// and nowhere earlier.
    /// </remarks>
    private static JsonNode? TilingSize(Desk.Desk desk, Workspace? workspace, DeskWindow window)
    {
        if (workspace is null || window.State != WindowState.Tiling)
        {
            return null;
        }

        Tile? tile = workspace.Tiling.Root?.Find(window.Handle);
        return tile is null ? null : tile.Share;
    }

    private static string State(WindowState state) => state switch
    {
        WindowState.Floating => "floating",
        WindowState.Fullscreen => "fullscreen",
        WindowState.Minimized => "minimized",
        _ => "tiling",
    };

    private static string Direction(SplitDirection direction) =>
        direction == SplitDirection.Vertical ? "vertical" : "horizontal";

    /// <summary>
    /// Adds a plain value to an array as a plain JSON value.
    /// </summary>
    /// <remarks>
    /// <c>JsonArray.Add(x)</c> takes the generic overload and produces a node
    /// that cannot write itself without a serializer context -- it throws at
    /// the moment something asks for the desk, and nowhere earlier. The cast
    /// picks the implicit conversion instead, which produces an ordinary
    /// value. Two tests would have shipped a bar that goes blank without it.
    /// </remarks>
    private static void AddValue(JsonArray array, string value) => array.Add((JsonNode)value);

    private static JsonArray FocusOrder(DeskMonitor monitor)
    {
        var order = new JsonArray();

        if (monitor.Displayed is { } displayed)
        {
            AddValue(order, displayed.Id.ToString());
        }

        foreach (Workspace workspace in monitor.Workspaces.Where(w => !w.Displayed))
        {
            AddValue(order, workspace.Id.ToString());
        }

        return order;
    }

    private static JsonArray FocusOrder(Desk.Desk desk, Workspace workspace)
    {
        var order = new JsonArray();

        foreach (WindowHandle handle in workspace.FocusOrder)
        {
            if (desk.Window(handle) is { Managed: true } window)
            {
                AddValue(order, window.Id.ToString());
            }
        }

        return order;
    }
}
