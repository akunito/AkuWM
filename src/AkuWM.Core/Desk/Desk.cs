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

    /// <summary>
    /// The names in the order the configuration gives them.
    /// </summary>
    /// <remarks>
    /// A dictionary has no order worth relying on -- it reuses the slot a
    /// removed key freed -- and this order is the one a person sees on the bar.
    /// </remarks>
    private readonly List<string> _order = [];

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
    private Func<(int X, int Y)>? _cursor;

    /// <summary>Where the pointer was when AkuWM last moved anything, and when.</summary>
    private (int X, int Y)? _cursorWhenPlaced;
    private long _placedAnythingAt;

    /// <summary>
    /// How long after a move a focus change is treated as the layout's doing
    /// rather than the person's, and how far the pointer may drift and still
    /// count as still.
    /// </summary>
    /// Short on purpose. The settle bursts that cause this are a tenth of a
    /// second apart, and the longer this is the likelier it swallows a click
    /// the person meant.
    internal const int FocusHoldMs = 250;
    internal const int PointerSlack = 4;

    /// <summary>
    /// True while the pointer is exactly where it was when AkuWM last moved
    /// windows, and that was a moment ago.
    /// </summary>
    internal bool WindowsMovedUnderAStillPointer()
    {
        if (_cursorWhenPlaced is not { } was || _cursor is null)
        {
            return false;
        }

        if (Now - _placedAnythingAt > FocusHoldMs)
        {
            return false;
        }

        (int X, int Y) now = _cursor();
        return Math.Abs(now.X - was.X) <= PointerSlack && Math.Abs(now.Y - was.Y) <= PointerSlack;
    }

    internal void MovedWindowsAt(long when) => (_placedAnythingAt, _cursorWhenPlaced) = (when, _cursor?.Invoke());

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

    /// <summary>Where the pointer is, so the focus can follow it and not the windows.</summary>
    public void ReadsTheCursorWith(Func<(int X, int Y)> cursor) => _cursor = cursor;

    /// <param name="clock">
    /// Milliseconds from somewhere monotonic. Injected so a test can let two
    /// seconds pass without taking two seconds.
    /// </param>
    // Hoisted out of Adopt: a fresh RuleMatcher per window recompiles every
    // re: pattern in the config, measured at 57 % of Adopt's time and 62 % of
    // its allocation. The matcher holds no per-window state.
    private readonly Matching.RuleMatcher _matcher = new();
    private List<RuleConfig> _activeRules = [];
    private Dictionary<MonitorHandle, MonitorSnapshot> _monitorSnapshots = [];
    private Dictionary<MonitorHandle, string> _monitorRoles = [];
    private readonly HashSet<WindowHandle> _hidden = [];

    public Desk(AkuWmConfig config, Func<WindowSnapshot, bool>? isOurs = null, Func<long>? clock = null)
    {
        Config = config;
        _isOurs = isOurs;
        _clock = clock ?? (() => Environment.TickCount64);

        List<RuleConfig> rules = [];
        foreach (RuleConfig rule in config.Rules ?? [])
        {
            if (rule.Enabled != false)
            {
                rules.Add(rule);
            }
        }

        _activeRules = rules;
        BuildWorkspaces();
    }

    /// <param name="Added">Workspaces the new configuration has and the old one did not.</param>
    /// <param name="Removed">Workspaces that are gone.</param>
    /// <param name="Rehomed">Windows that were on a workspace that is gone.</param>
    public readonly record struct ReloadResult(int Added, int Removed, int Rehomed)
    {
        public override string ToString() =>
            $"{Added} workspace(s) added, {Removed} removed, {Rehomed} window(s) rehomed";
    }

    /// <summary>
    /// Takes a new configuration without losing the desk.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Reconciled, not rebuilt. <see cref="BuildWorkspaces"/> clears the
    /// dictionary, and every tiling tree, floating rectangle and focus order
    /// lives in the workspaces it would throw away -- so a reload written that
    /// way would answer "applied" and leave the desk in a heap.
    /// </para>
    /// <para>
    /// Rules are re-read, and they decide at the moment a window is adopted,
    /// so an edited rule reaches windows opened from now on and not the ones
    /// already on screen. That is deliberate: re-deciding a window the person
    /// has since floated and placed would undo their work to honour a rule
    /// they were editing for the next window. The caller says so out loud
    /// rather than leaving it to be discovered.
    /// </para>
    /// <para>
    /// The configuration must be valid before it gets here. A reload that
    /// fails halfway is a desk in a state neither file describes.
    /// </para>
    /// </remarks>
    public ReloadResult Reload(AkuWmConfig config)
    {
        Config = config;

        List<RuleConfig> rules = [];
        foreach (RuleConfig rule in config.Rules ?? [])
        {
            if (rule.Enabled != false)
            {
                rules.Add(rule);
            }
        }

        _activeRules = rules;

        var wanted = new Dictionary<string, WorkspaceConfig>(StringComparer.OrdinalIgnoreCase);
        foreach (WorkspaceConfig configured in config.Workspaces ?? [])
        {
            if (configured.Name is { Length: > 0 } name && configured.Enabled != false)
            {
                wanted[name] = configured;
            }
        }

        List<Workspace>? gone = null;
        foreach (Workspace workspace in _workspaces.Values)
        {
            if (!wanted.ContainsKey(workspace.Name))
            {
                (gone ??= []).Add(workspace);
            }
        }

        int rehomed = 0;
        if (gone is not null)
        {
            // Somewhere that still exists, on the same screen when there is
            // one. A window left pointing at a workspace nobody has is a
            // window no redraw will ever account for.
            foreach (Workspace leaving in gone)
            {
                Workspace? home = null;
                foreach (WorkspaceConfig candidate in wanted.Values)
                {
                    if (string.Equals(candidate.Monitor, leaving.MonitorRole, StringComparison.OrdinalIgnoreCase)
                        && candidate.Name is { } name && _workspaces.TryGetValue(name, out Workspace? found))
                    {
                        home = found;
                        break;
                    }
                }

                home ??= _workspaces.Values.FirstOrDefault(w => wanted.ContainsKey(w.Name));

                if (home is not null)
                {
                    foreach (WindowHandle handle in leaving.Windows.ToArray())
                    {
                        if (Window(handle) is { } window)
                        {
                            Place(window, home);
                            rehomed++;
                        }
                    }
                }

                _workspaces.Remove(leaving.Name);
            }
        }

        int added = 0;
        foreach ((string name, WorkspaceConfig configured) in wanted)
        {
            if (_workspaces.TryGetValue(name, out Workspace? existing))
            {
                // A surviving workspace keeps its tree; only what the file
                // actually says about it is refreshed -- including which
                // screen it is on. Renaming the workspaces of this desk moved
                // one from the second monitor to the first, and without this
                // it stayed where it was: the screen it had left kept it, and
                // the one that should have had it was a workspace short.
                existing.MonitorRole = configured.Monitor ?? existing.MonitorRole;
                existing.DisplayName = configured.DisplayName;
                existing.KeepAlive = configured.KeepAlive == true;

                if (ParseDirection(configured.Direction) is { } direction)
                {
                    existing.Direction = direction;
                }

                continue;
            }

            _workspaces[name] = new Workspace(
                name,
                configured.Monitor ?? "main",
                ParseDirection(configured.Direction) ?? SplitDirection.Horizontal)
            {
                DisplayName = configured.DisplayName,
                KeepAlive = configured.KeepAlive == true,
            };

            added++;
        }

        // The order the file gives them, which is the order they are drawn in.
        // Without this the list is whatever the dictionary happens to hold:
        // removing 10 and adding 30 put 30 in the slot 10 had freed, so the
        // bar read 30, 21, 22, ... 29, 20.
        _order.Clear();
        foreach (WorkspaceConfig configured in config.Workspaces ?? [])
        {
            if (configured.Name is { Length: > 0 } name && wanted.ContainsKey(name))
            {
                _order.Add(name);
            }
        }

        // Roles may have been renamed or re-matched, and every monitor's list
        // of workspaces is rebuilt from the dictionary above.
        SetMonitors([.. _monitors.Select(m => m.Snapshot)]);

        return new ReloadResult(added, gone?.Count ?? 0, rehomed);
    }

    /// <summary>Milliseconds, for the timings the model itself has to judge.</summary>
    private long Now => _clock();

    public AkuWmConfig Config { get; private set; }

    public IReadOnlyList<DeskMonitor> Monitors => _monitors;

    public IReadOnlyCollection<DeskWindow> Windows => _windows.Values;

    /// <summary>
    /// Whether AkuWM has taken stock of the desk at least once.
    /// </summary>
    /// <remarks>
    /// Before it has, every window is "new" and belongs where it already is.
    /// After, a window that appears belongs where the person is looking.
    /// </remarks>
    public bool Settled { get; private set; }

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

    /// <summary>
    /// Told to leave the desk alone. The model still follows what happens; it
    /// just stops asking for anything to move.
    /// </summary>
    /// <remarks>
    /// `wm-toggle-pause` used to set a flag on the executor that nothing read,
    /// so a script that paused the window manager to drag something got
    /// success and a window manager that kept arranging.
    /// </remarks>
    public bool Paused { get; set; }

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

    private DeskMonitor? _focusedMonitor;

    /// <summary>
    /// The monitor the person is on.
    /// </summary>
    /// <remarks>
    /// Remembered, not derived from the focused window. Deriving it meant that
    /// switching to an EMPTY workspace on the second monitor left the answer
    /// at the primary -- there was no focused window there to derive it from --
    /// so every window opened afterwards was born on the wrong screen. Found
    /// by tests/wm 2026-09-21, and it is what a person does all day: go to an
    /// empty workspace and start something.
    /// </remarks>
    public DeskMonitor? FocusedMonitor
    {
        get
        {
            if (_focusedMonitor is { } remembered && _monitors.Contains(remembered))
            {
                return remembered;
            }

            return (Window(Focused) is { } window ? MonitorByHandle(window.Snapshot.Monitor) : null)
                   ?? _monitors.FirstOrDefault(m => m.Snapshot.IsPrimary)
                   ?? _monitors.FirstOrDefault();
        }
    }

    /// <summary>Records which screen the person is on; whichever happened last wins.</summary>
    private void LookingAt(DeskMonitor? monitor)
    {
        if (monitor is not null)
        {
            _focusedMonitor = monitor;
        }
    }

    // ---- building --------------------------------------------------------

    private void BuildWorkspaces()
    {
        _workspaces.Clear();
        _order.Clear();

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
            _order.Add(name);
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

        _monitorSnapshots = new Dictionary<MonitorHandle, MonitorSnapshot>(_monitors.Count);
        _monitorRoles = new Dictionary<MonitorHandle, string>(_monitors.Count);

        foreach (DeskMonitor monitor in _monitors)
        {
            _monitorSnapshots[monitor.Handle] = monitor.Snapshot;
            _monitorRoles[monitor.Handle] = monitor.Role;

            monitor.Workspaces.Clear();

            for (int i = 0; i < _order.Count; i++)
            {
                if (_workspaces.TryGetValue(_order[i], out Workspace? workspace)
                    && string.Equals(workspace.MonitorRole, monitor.Role, StringComparison.OrdinalIgnoreCase))
                {
                    monitor.Workspaces.Add(workspace);
                }
            }

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
        bool first = !Settled;

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

        // Only now is "where the person is looking" a question with an
        // answer: before the first sync every window is new.
        if (first)
        {
            Settled = true;
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
    /// <summary>What the rules and the platform make of one window.</summary>
    private ManagedWindow Decide(WindowSnapshot snapshot) => ShadowModel.Decide(
        snapshot,
        _activeRules,
        _matcher,
        _monitorSnapshots,
        _monitorRoles,
        Config,
        _isOurs,
        _hidden);

    /// <summary>Decides a window again, now that the reason it was refused is gone.</summary>
    private void Reconsider(DeskWindow window, WindowSnapshot snapshot)
    {
        ManagedWindow decision = Decide(snapshot);

        window.Managed = decision.Managed;
        window.Reason = decision.Reason;
        window.ReasonDetail = decision.ReasonDetail;
        window.Rules = decision.Rules;
        window.Effects = EffectsFor(decision.Rules);

        if (!decision.Managed)
        {
            return;
        }

        window.State = decision.State;
        window.PreviousState = decision.State == WindowState.Fullscreen ? WindowState.Tiling : decision.State;
        window.Sticky = false;

        Log.Info($"adopting {snapshot.ProcessName} \"{snapshot.Title}\" now that it is no longer cloaked");

        if (TargetWorkspace(decision, snapshot) is { } workspace)
        {
            Place(window, workspace);
        }

        if (decision.Sticky)
        {
            SetSticky(window.Handle, true);
        }
    }

    public DeskWindow Adopt(WindowSnapshot snapshot)
    {
        ManagedWindow decision = Decide(snapshot);

        var window = new DeskWindow(snapshot)
        {
            Managed = decision.Managed,
            Reason = decision.Reason,
            ReasonDetail = decision.ReasonDetail,
            State = decision.State,
            PreviousState = decision.State == WindowState.Fullscreen ? WindowState.Tiling : decision.State,
            Sticky = decision.Sticky,
            Rules = decision.Rules,
            Effects = EffectsFor(decision.Rules),
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

    /// <summary>
    /// Reasons a window is refused that can stop being true.
    /// </summary>
    /// <remarks>
    /// Everything else -- a rule that says ignore, a window that is not a
    /// window -- is a fact about the window and does not change while it is
    /// open. These two are facts about the MOMENT it was looked at.
    /// </remarks>
    private static bool Temporary(UnmanagedReason reason) =>
        reason is UnmanagedReason.CloakedElsewhere or UnmanagedReason.OtherVirtualDesktop;

    private void Update(DeskWindow window, WindowSnapshot snapshot)
    {
        WindowSnapshot was = window.Snapshot;
        window.Snapshot = snapshot;

        if (was.FrameBounds != snapshot.FrameBounds)
        {
            window.MovedAt = Now;
        }

        if (window.Placed is { } asked && asked.CloseTo(snapshot.FrameBounds, PlacementSlack))
        {
            window.Landed = true;
        }

        if (!window.Managed)
        {
            // A window refused because it was cloaked when AkuWM first saw it,
            // and is not cloaked any more, gets asked again. Deciding once was
            // right for rules and wrong for this: a UWP application is cloaked
            // while it starts, so the Calculator, Settings, the Store and
            // everything else of that kind was adopted at exactly the wrong
            // moment and stayed unmanaged for ever. Found by tests/wm.
            if (Temporary(window.Reason)
                && !snapshot.Cloak.HasFlag(CloakKind.Shell)
                && !snapshot.Cloak.HasFlag(CloakKind.InheritedOrOtherDesktop)
                && snapshot.OnCurrentVirtualDesktop != false)
            {
                Reconsider(window, snapshot);
            }

            return;
        }

        if (snapshot.IsMinimized && window.State != WindowState.Minimized)
        {
            window.PreviousState = window.State;
            window.State = WindowState.Minimized;

            // The shell lets the taskbar back up the moment a window it was
            // told is fullscreen goes to the taskbar, and it does not ask
            // again. Keeping Marked=true across that meant the mark was never
            // re-sent when the window came back -- the game was fullscreen
            // again with the bar on top of it, composed, 0% of frames direct
            // (tests/fullscreen 8-gamelike step 2, measured 2026-09-21).
            window.Marked = false;

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
        // A MAXIMISED window is never "where AkuWM put it": AkuWM tiles by
        // moving and never maximises (the only SetMaximized calls are the
        // geometry journal's, on the way out). The rectangles can be identical
        // -- a lone tiled window fills the work area and so does a maximised
        // one -- so the rectangle comparison alone called it ours, and a window
        // un-maximised and maximised again (what Alt+drag does) stayed tiling
        // for the rest of the run. tests/fullscreen 8-startmax steps 3-5.
        bool weMovedItThere = !snapshot.IsMaximized && window.Placed == snapshot.FrameBounds;

        // A floating window keeps where the person put it. Without this the
        // next redraw asked for the rectangle AkuWM still remembered and
        // dragged it straight back, so moving or resizing a floating window --
        // by its title bar, or with Alt+drag -- looked like it did not work at
        // all (reported from the desk, 2026-09-21).
        //
        // Within the slack counts as ours: a window that rounds its own size
        // to character cells lands near what was asked for, not on it, and
        // learning that as a move would drift the remembered rectangle a
        // little further every redraw.
        if ((window.State == WindowState.Floating || window.Sticky)
            && was.FrameBounds != snapshot.FrameBounds
            && window.Placed?.CloseTo(snapshot.FrameBounds, PlacementSlack) != true)
        {
            window.FloatingRect = snapshot.FrameBounds;
            Rehome(window, snapshot);
        }

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
        _hidden.Remove(handle);
        _asked.Remove(handle);

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

    /// <summary>The windows AkuWM currently has the cloak on. Maintained, not scanned.</summary>
    public IReadOnlySet<WindowHandle> HiddenByUs => _hidden;

    internal void RecordHidden(WindowHandle handle, bool hidden)
    {
        if (hidden)
        {
            _hidden.Add(handle);
        }
        else
        {
            _hidden.Remove(handle);
        }
    }

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

        // Out of every other monitor's set first: a restore that crossed
        // screens used to leave it in two, and which one drew it was then
        // decided by enumeration order rather than by StickyMonitor.
        for (int i = 0; i < _monitors.Count; i++)
        {
            _monitors[i].Sticky.Remove(window.Handle);
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

        // Where the person is looking, once AkuWM knows where that is. A window
        // that opens while the vertical monitor has the focus belongs there,
        // even though Windows put it on the primary -- which is what the stack
        // AkuWM replaces did, and what a person means by opening something
        // "here". Measured 2026-09-21: without this, focusing a workspace of
        // the second monitor and starting two windows put both on the first.
        //
        // Not during the first sync. Every window on the desk is new then, and
        // they belong where they already are, not piled onto one workspace.
        if (Settled && FocusedMonitor?.Displayed is { } here)
        {
            return here;
        }

        return MonitorByHandle(snapshot.Monitor)?.Displayed
               ?? FocusedMonitor?.Displayed
               ?? _workspaces.Values.FirstOrDefault();
    }

    /// <summary>Puts a window into a workspace, in the layer its state says.</summary>
    /// <summary>
    /// A window the person dragged onto another monitor joins the workspace
    /// shown there.
    /// </summary>
    /// <remarks>
    /// Without this the drag cannot work at all, and not because anything
    /// fights it: the window keeps the rectangle it was given, but its
    /// workspace still belongs to the monitor it left, and a placement is
    /// clamped into the work area of the workspace's monitor. So the window
    /// was dragged back to the edge of the screen it had just come from --
    /// 3082 asked for, 3832 arrived at, that monitor's left edge less the
    /// border (measured from the desk, 2026-09-21).
    ///
    /// The monitor comes from the snapshot, which is what Windows itself says
    /// about the window, rather than from comparing rectangles: a window
    /// straddling the boundary belongs to whichever screen Windows will send
    /// its DPI changes for, and second-guessing that is how the two sides stop
    /// agreeing.
    ///
    /// Only for a move the PERSON made -- the caller has already established
    /// that -- so the desk moving a window to a workspace of the other monitor
    /// does not read as a drag and send it round again.
    /// </remarks>
    private void Rehome(DeskWindow window, WindowSnapshot snapshot)
    {
        if (window.Sticky)
        {
            // It follows its monitor by being sticky; changing that here would
            // silently un-stick it.
            return;
        }

        if (window.Workspace is not { } name || Workspace(name) is not { } workspace)
        {
            return;
        }

        DeskMonitor? landed = MonitorByHandle(snapshot.Monitor);
        if (landed is null || ReferenceEquals(landed, MonitorOf(workspace)))
        {
            return;
        }

        if (landed.Displayed is { } destination)
        {
            Place(window, destination);
        }
    }

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

    /// <summary>
    /// The look a window's rules ask for, layered over the global block.
    /// </summary>
    /// <remarks>
    /// In the order the rules are written, so the last rule to mention a field
    /// is the one that decides it -- the same way every other layer in this
    /// configuration works. A rule that says nothing about a field leaves it
    /// to the one underneath, which is what makes "this one app, dimmed"
    /// a three-line rule instead of a copy of the whole block.
    /// </remarks>
    private Config.EffectsConfig? EffectsFor(IReadOnlyList<string> rules)
    {
        if (rules.Count == 0)
        {
            return null;
        }

        Config.EffectsConfig? resolved = null;

        for (int i = 0; i < _activeRules.Count; i++)
        {
            Config.RuleConfig rule = _activeRules[i];

            if (rule.Effects is { } effects && rule.Id is { Length: > 0 } id && rules.Contains(id))
            {
                resolved = ConfigMerge.MergeObject(resolved, effects);
            }
        }

        return resolved;
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
        // This screen's own numbers over the global ones, field by field, so
        // "just the vertical monitor, wider" does not mean restating the rest.
        GapsConfig? gaps = Config.Gaps;

        for (int i = 0; i < (Config.Monitors?.Count ?? 0); i++)
        {
            MonitorConfig configured = Config.Monitors![i];

            if (configured.Gaps is { } mine
                && string.Equals(configured.Id, monitor.Role, StringComparison.OrdinalIgnoreCase))
            {
                gaps = ConfigMerge.MergeObject(gaps, mine);
                break;
            }
        }

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
