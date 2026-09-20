using AkuWM.Core.Config;
using AkuWM.Core.Layout;
using AkuWM.Core.Logging;
using AkuWM.Core.Model;

namespace AkuWM.Core.Desk;

/// <summary>
/// The whole desk: the monitors, the workspaces bound to them, and every
/// window AkuWM is responsible for.
/// </summary>
/// <remarks>
/// <para>
/// It holds no Win32 and calls nothing. Work comes in as snapshots and
/// commands; what comes out is a <see cref="Redraw"/> -- the list of moves,
/// cloaks and restacks that would make the screen match the model. The
/// platform applies it and says what actually happened.
/// </para>
/// <para>
/// That shape is the reason a window manager can be tested at all. Every
/// decision here -- which workspace a window opens on, what a workspace switch
/// costs, where a floating window sits when its monitor has gone away -- is
/// exercised on Linux against the pixel counts of the two real monitors.
/// </para>
/// </remarks>
public sealed partial class Desk
{
    private readonly Dictionary<WindowHandle, DeskWindow> _windows = [];
    private readonly Dictionary<string, Workspace> _workspaces = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The screens that are here now, in enumeration order.</summary>
    private readonly List<DeskMonitor> _monitors = [];

    /// <summary>
    /// Every role this desk has ever seen, whether its screen is plugged in or
    /// not.
    /// </summary>
    /// <remarks>
    /// A monitor that goes to sleep and comes back is the same monitor. Making
    /// a new one for it would lose which workspace it was showing, which
    /// windows were stuck to it and where the focus had been -- so the object
    /// outlives the cable.
    /// </remarks>
    private readonly Dictionary<string, DeskMonitor> _byRole = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<WindowSnapshot, bool>? _isOurs;

    private readonly Func<long> _clock;

    /// <summary>Is that handle still a window? Null means believe the enumeration.</summary>
    private Func<WindowHandle, bool>? _stillAWindow;

    /// <summary>
    /// Lets the desk check a handle before forgetting a window it has hidden.
    /// </summary>
    /// <remarks>
    /// The candidate filter flaps: a title that goes empty for a moment, a
    /// splash turning into a main window, a game loading. One pass without a
    /// window AkuWM has cloaked used to forget it, and the re-adopt then read
    /// the cloak as somebody else's -- unmanaged, invisible, off the taskbar
    /// and out of Alt+Tab, with nothing left that would ever take it off.
    /// </remarks>
    public void ChecksHandlesWith(Func<WindowHandle, bool> stillAWindow) => _stillAWindow = stillAWindow;

    /// <param name="clock">
    /// Milliseconds from somewhere monotonic. Injected so a test can let two
    /// seconds pass without taking two seconds.
    /// </param>
    public Desk(AkuWmConfig config, Func<WindowSnapshot, bool>? isOurs = null, Func<long>? clock = null)
    {
        Config = config;
        _isOurs = isOurs;
        _clock = clock ?? (() => Environment.TickCount64);
        BuildWorkspaces();
    }

    /// <summary>Milliseconds, for the timings the model itself has to judge.</summary>
    private long Now => _clock();

    public AkuWmConfig Config { get; private set; }

    public IReadOnlyList<DeskMonitor> Monitors => _monitors;

    public IReadOnlyCollection<DeskWindow> Windows => _windows.Values;

    public IEnumerable<Workspace> Workspaces => _workspaces.Values;

    public WindowHandle Focused { get; private set; } = WindowHandle.None;

    private WindowHandle _wantFocus = WindowHandle.None;

    /// <summary>
    /// Asks for the focus to end up on a window once the desk has been
    /// redrawn.
    /// </summary>
    /// <remarks>
    /// Not done immediately, and that is the point: a workspace switch focuses
    /// a window that is still cloaked at the moment the command runs. The
    /// focus travels with the redraw and is applied after the windows are
    /// visible.
    /// </remarks>
    public void WantFocus(WindowHandle handle) => _wantFocus = handle;

    /// <summary>
    /// Whether AkuWM may move windows that run at a higher integrity level.
    /// </summary>
    /// <remarks>
    /// True when Windows granted <c>uiAccess</c>. Without it, positioning an
    /// elevated window is refused by UIPI and the call fails silently, so the
    /// model does not ask: the window is still hidden and shown by cloak,
    /// which does work, and it is left where it is.
    /// </remarks>
    public bool CanPositionElevated { get; set; } = true;

    /// <summary>
    /// Whether hiding a window is something this machine lets AkuWM undo.
    /// </summary>
    /// <remarks>
    /// Set false by the platform when the round trip fails. Everything then
    /// stays on screen: the workspaces lose their point, which is a bad desk,
    /// and a window that cannot be brought back is a lost one.
    /// </remarks>
    public bool CanHide { get; set; } = true;

    public DeskWindow? Window(WindowHandle handle) =>
        _windows.TryGetValue(handle, out DeskWindow? window) ? window : null;

    public Workspace? Workspace(string name) =>
        _workspaces.TryGetValue(name, out Workspace? workspace) ? workspace : null;

    public DeskMonitor? MonitorOf(Workspace workspace) =>
        _monitors.FirstOrDefault(m => string.Equals(m.Role, workspace.MonitorRole, StringComparison.OrdinalIgnoreCase));

    public DeskMonitor? MonitorByRole(string role) =>
        _monitors.FirstOrDefault(m => string.Equals(m.Role, role, StringComparison.OrdinalIgnoreCase));

    public DeskMonitor? MonitorByHandle(MonitorHandle handle) =>
        _monitors.FirstOrDefault(m => m.Handle == handle);

    /// <summary>The monitor the focused window is on, or the primary one.</summary>
    public DeskMonitor? FocusedMonitor =>
        (Window(Focused) is { } window ? MonitorByHandle(window.Snapshot.Monitor) : null)
        ?? _monitors.FirstOrDefault(m => m.Snapshot.IsPrimary)
        ?? _monitors.FirstOrDefault();

    // ---- building --------------------------------------------------------

    private void BuildWorkspaces()
    {
        _workspaces.Clear();

        foreach (WorkspaceConfig configured in Config.Workspaces ?? [])
        {
            if (configured.Name is not { Length: > 0 } name || configured.Enabled == false)
            {
                continue;
            }

            string role = configured.Monitor ?? "main";
            var workspace = new Workspace(name, role, ParseDirection(configured.Direction) ?? SplitDirection.Horizontal)
            {
                DisplayName = configured.DisplayName,
                KeepAlive = configured.KeepAlive == true,
            };

            _workspaces[name] = workspace;
        }
    }

    private static SplitDirection? ParseDirection(string? direction) => direction?.ToLowerInvariant() switch
    {
        "horizontal" => SplitDirection.Horizontal,
        "vertical" => SplitDirection.Vertical,
        _ => null,
    };

    /// <summary>
    /// Tells the desk which displays exist, and which role each one plays.
    /// </summary>
    /// <remarks>
    /// Called at startup and on every display change. Workspaces are not
    /// rebuilt: they belong to the desk, not to a screen, so a monitor that
    /// goes to sleep and comes back keeps its trees, its floating rectangles
    /// and which workspace was on it.
    /// </remarks>
    public void SetMonitors(IReadOnlyList<MonitorSnapshot> monitors)
    {
        Dictionary<MonitorHandle, string> roles = MonitorRoles.Resolve(Config.Monitors ?? [], monitors);
        _monitors.Clear();

        foreach (MonitorSnapshot snapshot in monitors)
        {
            if (!roles.TryGetValue(snapshot.Handle, out string? role))
            {
                // A screen the configuration does not name. Windows on it are
                // left alone rather than dragged onto a role that is not
                // theirs.
                continue;
            }

            if (!_byRole.TryGetValue(role, out DeskMonitor? monitor))
            {
                monitor = new DeskMonitor(snapshot, role);
                _byRole[role] = monitor;
            }

            monitor.Snapshot = snapshot;
            _monitors.Add(monitor);
        }

        foreach (DeskMonitor monitor in _monitors)
        {
            monitor.Workspaces.Clear();
            monitor.Workspaces.AddRange(
                _workspaces.Values.Where(w =>
                    string.Equals(w.MonitorRole, monitor.Role, StringComparison.OrdinalIgnoreCase)));

            if (monitor.Displayed is null && monitor.Workspaces.Count > 0)
            {
                monitor.Workspaces[0].Displayed = true;
            }
        }

        // A workspace whose monitor is away keeps its displayed flag: the
        // screen coming back should find the desk as it left it, and nothing
        // draws a workspace whose monitor is not in the list anyway.
    }

    // ---- windows coming and going ----------------------------------------

    /// <summary>
    /// Brings the model up to date with what the OS says is on the desk.
    /// </summary>
    /// <remarks>
    /// Rules decide what a window is <em>once</em>, when it first appears.
    /// After that its state belongs to AkuWM and to the person using it: a
    /// window someone floated by hand must not be tiled again by the same rule
    /// the next time anything happens. What is re-read every time is only what
    /// the window itself can change -- minimised, and whether it has covered
    /// the screen.
    /// </remarks>
    public void Sync(IReadOnlyList<WindowSnapshot> windows)
    {
        var present = new HashSet<WindowHandle>();

        foreach (WindowSnapshot snapshot in windows)
        {
            present.Add(snapshot.Handle);

            if (_windows.TryGetValue(snapshot.Handle, out DeskWindow? known))
            {
                Update(known, snapshot);
            }
            else
            {
                Adopt(snapshot);
            }
        }

        List<WindowHandle>? gone = null;
        foreach ((WindowHandle handle, DeskWindow window) in _windows)
        {
            if (present.Contains(handle))
            {
                continue;
            }

            // A window AkuWM has hidden is the one it must not lose: check the
            // handle before believing an enumeration that left it out.
            if (window.Hidden && _stillAWindow?.Invoke(handle) == true)
            {
                continue;
            }

            (gone ??= []).Add(handle);
        }

        if (gone is not null)
        {
            for (int i = 0; i < gone.Count; i++)
            {
                Forget(gone[i]);
            }
        }
    }

    /// <summary>
    /// One window changed. Reads that one rather than the whole desk.
    /// </summary>
    /// <remarks>
    /// The difference between this and <see cref="Sync"/> is the difference
    /// between a gesture that costs microseconds and one that costs
    /// milliseconds: moving a window produces dozens of events, and reading a
    /// hundred windows to learn about one of them is the cost that made the
    /// stack this replaces feel slow.
    /// </remarks>
    public DeskWindow Observe(WindowSnapshot snapshot)
    {
        if (!_windows.TryGetValue(snapshot.Handle, out DeskWindow? known))
        {
            return Adopt(snapshot);
        }

        Update(known, snapshot);
        return known;
    }

    /// <summary>Takes a new window in, and decides where it belongs.</summary>
    public DeskWindow Adopt(WindowSnapshot snapshot)
    {
        ManagedWindow decision = ShadowModel.Decide(
            snapshot,
            (Config.Rules ?? []).Where(r => r.Enabled != false).ToList(),
            new Matching.RuleMatcher(),
            _monitors.ToDictionary(m => m.Handle, m => m.Snapshot),
            _monitors.ToDictionary(m => m.Handle, m => m.Role),
            Config,
            _isOurs,
            HiddenByUs);

        var window = new DeskWindow(snapshot)
        {
            Managed = decision.Managed,
            Reason = decision.Reason,
            ReasonDetail = decision.ReasonDetail,
            State = decision.State,
            PreviousState = decision.State == WindowState.Fullscreen ? WindowState.Tiling : decision.State,
            Sticky = decision.Sticky,
            Rules = decision.Rules,
        };

        _windows[snapshot.Handle] = window;

        if (!window.Managed)
        {
            return window;
        }

        if (window.Sticky)
        {
            MakeSticky(window);
            return window;
        }

        Workspace? workspace = TargetWorkspace(decision, snapshot);
        if (workspace is not null)
        {
            Place(window, workspace);
        }

        return window;
    }

    private void Update(DeskWindow window, WindowSnapshot snapshot)
    {
        WindowSnapshot was = window.Snapshot;
        window.Snapshot = snapshot;

        if (!window.Managed)
        {
            return;
        }

        if (snapshot.IsMinimized && window.State != WindowState.Minimized)
        {
            window.PreviousState = window.State;
            window.State = WindowState.Minimized;

            if (Workspace(window.Workspace ?? string.Empty) is { } leaving)
            {
                // Out of the tree, and out of the fullscreen slot: a workspace
                // that still believes something is covering it puts every
                // other window behind a window nobody can see. Its place in
                // the floating band is kept, because that is where it goes
                // back to.
                leaving.Tiling.Remove(window.Handle);

                if (leaving.Fullscreen == window.Handle)
                {
                    leaving.Fullscreen = WindowHandle.None;
                }
            }

            return;
        }

        if (!snapshot.IsMinimized && window.State == WindowState.Minimized)
        {
            Restore(window);
            return;
        }

        // A window that covered its monitor by itself -- a game going
        // fullscreen -- as opposed to one AkuWM put exactly where it is.
        MonitorSnapshot? monitor = MonitorByHandle(snapshot.Monitor)?.Snapshot;
        bool coversTheScreen = ShadowModel.IsFullscreen(snapshot, monitor);
        bool weMovedItThere = window.Placed == snapshot.FrameBounds;

        if (coversTheScreen && window.State != WindowState.Fullscreen && !weMovedItThere)
        {
            SetFullscreen(window, true);
        }
        else if (!coversTheScreen && window.State == WindowState.Fullscreen
                 && was.FrameBounds != snapshot.FrameBounds)
        {
            SetFullscreen(window, false);
        }
    }

    /// <summary>
    /// Raised when a window closes, so whoever is keeping records about it can
    /// stop.
    /// </summary>
    /// <remarks>
    /// The journal of where windows were is written to disk and read at the
    /// next start. Without this it collects every window that has ever been
    /// opened and closed in a session -- harmless, because a stale entry is
    /// dropped when it is restored, and still a file that grows all day and a
    /// count in <c>doctor</c> that means nothing.
    /// </remarks>
    public event Action<WindowHandle>? Forgotten;

    /// <summary>Lets go of a window that has closed.</summary>
    public void Forget(WindowHandle handle)
    {
        if (!_windows.Remove(handle, out DeskWindow? window))
        {
            return;
        }

        Forgotten?.Invoke(handle);

        if (window.Workspace is { } name)
        {
            Workspace(name)?.Release(handle);
        }

        foreach (DeskMonitor monitor in _monitors)
        {
            monitor.Sticky.Remove(handle);
        }

        if (Focused == handle)
        {
            Focused = WindowHandle.None;
        }
    }

    /// <summary>The windows AkuWM currently has the cloak on.</summary>
    public IReadOnlySet<WindowHandle> HiddenByUs =>
        _windows.Values.Where(w => w.Hidden).Select(w => w.Handle).ToHashSet();

    private void MakeSticky(DeskWindow window)
    {
        DeskMonitor? monitor = MonitorByHandle(window.Snapshot.Monitor) ?? FocusedMonitor;
        if (monitor is null)
        {
            return;
        }

        if (window.Workspace is { } previous)
        {
            Workspace(previous)?.Release(window.Handle);
            window.Workspace = null;
        }

        window.Sticky = true;
        window.StickyMonitor = monitor.Role;
        window.FloatingRect ??= window.Snapshot.FrameBounds;

        if (window.State == WindowState.Tiling)
        {
            // A sticky window is drawn on whichever workspace its monitor
            // shows, so it cannot be in one workspace's tree. Sticky implies
            // floating, and says so rather than silently misplacing it.
            window.State = WindowState.Floating;
        }

        monitor.Sticky.Add(window.Handle);
    }

    /// <summary>Where a rule says this window opens, or where the person is looking.</summary>
    private Workspace? TargetWorkspace(ManagedWindow decision, WindowSnapshot snapshot)
    {
        if (decision.Target is { } target)
        {
            if (target.Workspace is { Length: > 0 } named && Workspace(named) is { } byName)
            {
                return byName;
            }

            if (target.Monitor is { Length: > 0 } role && MonitorByRole(role) is { } monitor)
            {
                int slot = (target.Slot ?? 1) - 1;
                if (slot >= 0 && slot < monitor.Workspaces.Count)
                {
                    return monitor.Workspaces[slot];
                }

                return monitor.Displayed;
            }
        }

        return MonitorByHandle(snapshot.Monitor)?.Displayed
               ?? FocusedMonitor?.Displayed
               ?? _workspaces.Values.FirstOrDefault();
    }

    /// <summary>Puts a window into a workspace, in the layer its state says.</summary>
    private void Place(DeskWindow window, Workspace workspace)
    {
        if (window.Workspace is { } previous && !string.Equals(previous, workspace.Name, StringComparison.OrdinalIgnoreCase))
        {
            Workspace(previous)?.Release(window.Handle);
        }

        window.Workspace = workspace.Name;

        switch (window.State)
        {
            case WindowState.Fullscreen:
                workspace.Fullscreen = window.Handle;
                break;

            case WindowState.Floating:
                window.FloatingRect ??= window.Snapshot.FrameBounds;
                if (!workspace.Floating.Contains(window.Handle))
                {
                    workspace.Floating.Add(window.Handle);
                }

                break;

            case WindowState.Minimized:
                break;

            default:
                WindowHandle beside = workspace.FocusOrder
                    .FirstOrDefault(h => workspace.Tiling.Contains(h));
                workspace.Tiling.Add(window.Handle, beside, DirectionFor(workspace));
                break;
        }

        workspace.Touch(window.Handle);
    }

    private SplitDirection DirectionFor(Workspace workspace)
    {
        if (Config.Layout?.DefaultDirection is { Length: > 0 } configured
            && ParseDirection(configured) is { } explicitDirection)
        {
            return explicitDirection;
        }

        // "auto": the shape of the screen decides, so the portrait monitor
        // stacks and the wide one puts windows side by side.
        return MonitorOf(workspace)?.NaturalDirection ?? workspace.Direction;
    }

    // ---- gaps -------------------------------------------------------------

    public Gaps GapsFor(DeskMonitor monitor)
    {
        GapsConfig? gaps = Config.Gaps;
        int[] outer = gaps?.Outer ?? [0, 0, 0, 0];
        var written = new Gaps(
            gaps?.Inner ?? 0,
            outer.Length > 0 ? outer[0] : 0,
            outer.Length > 1 ? outer[1] : 0,
            outer.Length > 2 ? outer[2] : 0,
            outer.Length > 3 ? outer[3] : 0);

        return gaps?.ScaleWithDpi == false ? written : written.Scaled(monitor.Snapshot.ScaleFactor);
    }

    public override string ToString() =>
        $"{_monitors.Count} monitor(s), {_workspaces.Count} workspace(s), {_windows.Count} window(s)";
}
