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

    /// <summary>How long after adoption a window's own activation is believed.</summary>
    internal const int NewWindowMs = 1500;

    /// <summary>
    /// A change of size in one observation bigger than this, on a window that
    /// leaves its scaling to Windows, is Windows rescaling it, not a hand.
    /// </summary>
    internal const int RescaleJump = 200;

    /// <summary>
    /// How long after a crossing a resize belongs to Windows, not the person.
    /// </summary>
    /// <remarks>
    /// The DPI rescale lands 125 to 156 ms after the move, measured six times
    /// on this desk. A second is comfortably past that and far short of
    /// anything a person does deliberately after dropping a window.
    /// </remarks>
    internal const int CrossingMs = 1000;
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
                string role = configured.Monitor ?? existing.MonitorRole;
                if (!string.Equals(existing.MonitorRole, role, StringComparison.OrdinalIgnoreCase))
                {
                    // It leaves its screen as a workspace, not as the one on
                    // show: the target already has one displayed, and two
                    // displayed workspaces on one monitor answered isDisplayed
                    // for both while only one was drawn.
                    existing.Displayed = false;
                }

                existing.MonitorRole = role;
                existing.DisplayName = configured.DisplayName;
                existing.KeepAlive = configured.KeepAlive == true;
                existing.Direction = ParseDirection(configured.Direction);

                continue;
            }

            _workspaces[name] = new Workspace(
                name,
                configured.Monitor ?? "main",
                ParseDirection(configured.Direction))
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

    /// <summary>Says which screen the person is on, from a command that named it.</summary>
    public void LookAt(DeskMonitor monitor) => LookingAt(monitor);

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
            var workspace = new Workspace(name, role, ParseDirection(configured.Direction))
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
        // Not the first list of a run: the startup layout does not wait.
        if (_monitorSnapshots.Count > 0 && !SameScreens(monitors))
        {
            _screensChanged = true;
            _screensChangedAt = Now;
            // A screen that came, went or changed shape is a slow burst (a
            // monitor powering on takes seconds and passes through wrong
            // shapes -- the vertical one came back LANDSCAPE for 1.2 s,
            // 2026-09-22 18:06); a bar that moved is one notification.
            _screenSettleFor = SameScreenSet(monitors) ? ScreenSettleMs : MonitorSettleMs;
        }

        Dictionary<MonitorHandle, string> roles = MonitorRoles.Resolve(Config.Monitors ?? [], monitors);
        List<string> before = _monitors.Select(m => m.Role).ToList();
        _monitors.Clear();
        List<(DeskMonitor Monitor, Rect Was)>? moved = null;

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
            else if (monitor.Snapshot.WorkArea != snapshot.WorkArea)
            {
                // Noted here and acted on at the end: the workspaces have not
                // been handed to their monitors yet, and that is how a window
                // is found to belong to this screen.
                (moved ??= []).Add((monitor, monitor.Snapshot.WorkArea));
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
        }

        BindWorkspaces();

        // A workspace whose monitor is away keeps its displayed flag: the
        // screen coming back should find the desk as it left it, and nothing
        // draws a workspace whose monitor is not in the list anyway.
        if (moved is not null)
        {
            foreach ((DeskMonitor monitor, Rect was) in moved)
            {
                FollowTheScreen(monitor, was);
            }
        }

        // Screens that went, and screens that are back: their windows are
        // lent to a screen that is here, and taken back (Desk.Loans.cs).
        for (int i = 0; i < before.Count; i++)
        {
            if (!_monitors.Any(m => string.Equals(m.Role, before[i], StringComparison.OrdinalIgnoreCase)))
            {
                Lend(before[i]);
            }
        }

        for (int i = 0; i < _monitors.Count; i++)
        {
            if (!before.Contains(_monitors[i].Role, StringComparer.OrdinalIgnoreCase))
            {
                Reclaim(_monitors[i]);
            }
        }
    }

    private bool SameScreenSet(IReadOnlyList<MonitorSnapshot> monitors)
    {
        if (monitors.Count != _monitorSnapshots.Count)
        {
            return false;
        }

        for (int i = 0; i < monitors.Count; i++)
        {
            MonitorSnapshot now = monitors[i];
            if (!_monitorSnapshots.TryGetValue(now.Handle, out MonitorSnapshot? was)
                || was.Bounds != now.Bounds
                || was.Dpi != now.Dpi
                || !string.Equals(was.HardwareId, now.HardwareId, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private bool SameScreens(IReadOnlyList<MonitorSnapshot> monitors)
    {
        if (monitors.Count != _monitorSnapshots.Count)
        {
            return false;
        }

        for (int i = 0; i < monitors.Count; i++)
        {
            MonitorSnapshot now = monitors[i];
            if (!_monitorSnapshots.TryGetValue(now.Handle, out MonitorSnapshot? was)
                || was.Bounds != now.Bounds
                || was.WorkArea != now.WorkArea
                || was.Dpi != now.Dpi)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Hands every workspace to the present monitor its role names.</summary>
    private void BindWorkspaces()
    {
        foreach (DeskMonitor monitor in _monitors)
        {
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
    }

    /// <summary>
    /// A screen moved or changed size: its windows keep their place ON IT.
    /// </summary>
    /// <remarks>
    /// A tiled window gets this for free, because the layout is recomputed
    /// from the work area. A floating one does not -- its rectangle is
    /// absolute, so a screen moved 312 px up in the display settings leaves
    /// every floating window on it 312 px below where it was, which after a
    /// big enough move is another screen or nowhere at all.
    ///
    /// The place is kept as a FRACTION of the work area, so the one formula
    /// covers both things that can happen to a screen: when only the origin
    /// moved the ratio is exactly one and this is a plain translation, to the
    /// pixel; when the resolution changed as well, a window a third of the way
    /// across is still a third of the way across. Its size is left alone --
    /// that is the person's choice, and Windows rescales it itself when the
    /// DPI is what changed.
    ///
    /// Only the remembered rectangle is touched. Whether the window still fits
    /// is the redraw's business, and it already pulls back anything that ends
    /// up mostly off its screen.
    /// </remarks>
    private void FollowTheScreen(DeskMonitor monitor, Rect was)
    {
        Rect now = monitor.Snapshot.WorkArea;
        if (was.Width <= 0 || was.Height <= 0)
        {
            return;
        }

        foreach (DeskWindow window in _windows.Values)
        {
            if (window.FloatingRect is not { } rect || !BelongsTo(window, monitor))
            {
                continue;
            }

            window.FloatingRect = rect with
            {
                X = now.X + (int)Math.Round((rect.X - was.X) * (double)now.Width / was.Width),
                Y = now.Y + (int)Math.Round((rect.Y - was.Y) * (double)now.Height / was.Height),
            };
        }
    }

    private bool BelongsTo(DeskWindow window, DeskMonitor monitor) =>
        window.Sticky
            ? monitor.Sticky.Contains(window.Handle)
            : window.Workspace is { } name
              && Workspace(name) is { } workspace
              && ReferenceEquals(MonitorOf(workspace), monitor);

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

            // What Windows says, not "unknown": null differed from false, so
            // every window adopted was sent a synchronous SetWindowPos to leave
            // a band it was not in -- including a game that had put ITSELF in
            // the band, which is the swapchain-breaking call the band rule
            // exists to avoid.
            Banded = snapshot.IsTopmost,
            AdoptedAt = Now,
        };

        _windows[snapshot.Handle] = window;

        if (!window.Managed)
        {
            return window;
        }

        // What the previous run had it as, unless a rule names a workspace:
        // the desk a person arranged survives a restart of the daemon.
        if (decision.Target?.Workspace is not { Length: > 0 }
            && decision.Target?.Monitor is not { Length: > 0 }
            && _placements?.Recall(snapshot.Handle, snapshot.ProcessName) is { } remembered
            && Recall(window, remembered))
        {
            return window;
        }

        if (window.Sticky)
        {
            MakeSticky(window);
            return window;
        }

        Workspace? workspace = TargetWorkspace(decision, snapshot);

        // The second level of rules: what the person did to the last window
        // of this application, when no rule of theirs speaks. Not during the
        // first sync -- those windows are where they are.
        if (Settled && workspace is not null && MonitorOf(workspace) is { } on)
        {
            RecallApp(window, snapshot, on);
        }

        if (workspace is not null)
        {
            Place(window, workspace);
        }

        return window;
    }

    private State.PlacementJournal? _placements;

    /// <summary>Lets the desk remember where every window is across a restart.</summary>
    public void RemembersPlacementsWith(State.PlacementJournal journal) => _placements = journal;

    private State.AppMemory? _apps;

    /// <summary>Lets the desk open a window as the last one of its application was closed.</summary>
    public void RemembersAppsWith(State.AppMemory memory) => _apps = memory;

    private bool RemembersApps => _apps is not null && Config.General?.RememberApps != false;

    /// <summary>Whether the pointer is on (or within a hand's reach of) a rectangle; true when the desk cannot read it.</summary>
    private bool PointerOn(Rect frame)
    {
        if (_cursor is null)
        {
            return true;
        }

        (int x, int y) = _cursor();
        return frame.Inflate(64).Contains(x, y);
    }

    /// <summary>The screen the pointer is on, when the desk can read it and the person wants windows there.</summary>
    private DeskMonitor? MonitorUnderPointer()
    {
        if (Config.General?.OpenUnderPointer == false || _cursor is null)
        {
            return null;
        }

        (int x, int y) = _cursor();
        for (int i = 0; i < _monitors.Count; i++)
        {
            if (_monitors[i].FullArea.Contains(x, y))
            {
                return _monitors[i];
            }
        }

        return null;
    }

    /// <summary>
    /// Opens a window the way the last one of its application was closed,
    /// on the given screen. False when there is nothing remembered.
    /// </summary>
    private bool RecallApp(DeskWindow window, WindowSnapshot snapshot, DeskMonitor on)
    {
        if (!RemembersApps || snapshot.IsElevated || window.Rules.Count > 0
            || _apps!.Recall(State.AppMemory.KeyOf(snapshot)) is not { } memory)
        {
            return false;
        }

        if (memory.Floating)
        {
            Rect work = on.TilingArea;
            int width = Math.Min(memory.Width, work.Width);
            int height = Math.Min(memory.Height, work.Height);
            window.FloatingRect = new Rect(
                Math.Clamp(work.X + memory.OffsetX, work.Left, work.Right - width),
                Math.Clamp(work.Y + memory.OffsetY, work.Top, work.Bottom - height),
                width,
                height);
            window.State = WindowState.Floating;
            window.PreviousState = WindowState.Floating;
        }
        else
        {
            window.State = WindowState.Tiling;
            window.PreviousState = WindowState.Tiling;
        }

        return true;
    }

    /// <summary>Writes down how a window was when it closed, for the next one of its application.</summary>
    private void RememberApp(DeskWindow window)
    {
        if (!RemembersApps || !window.Managed || window.Sticky || window.Snapshot.IsElevated
            || window.Rules.Count > 0 || window.State == WindowState.Fullscreen)
        {
            return;
        }

        bool floating = window.State == WindowState.Floating
            || (window.State == WindowState.Minimized && window.PreviousState == WindowState.Floating);
        Rect frame = window.FloatingRect ?? window.Snapshot.FrameBounds;
        Rect work = (window.Workspace is { } name && Workspace(name) is { } workspace ? MonitorOf(workspace) : null)?.TilingArea
            ?? MonitorByHandle(window.Snapshot.Monitor)?.TilingArea
            ?? default;

        _apps!.Remember(
            State.AppMemory.KeyOf(window.Snapshot),
            new State.AppRecord(floating, frame.Width, frame.Height, frame.X - work.X, frame.Y - work.Y));
    }

    /// <summary>Puts an adopted window back where the last run had it. False when that place is gone.</summary>
    private bool Recall(DeskWindow window, in State.Placed remembered)
    {
        if (remembered.StickyTo is { } role)
        {
            if (MonitorByRole(role) is not { } monitor)
            {
                return false;
            }

            window.State = WindowState.Floating;
            window.PreviousState = WindowState.Floating;
            if (!remembered.FloatingRect.IsEmpty)
            {
                window.FloatingRect = remembered.FloatingRect;
            }

            MakeSticky(window, monitor);
            return true;
        }

        if (remembered.Workspace is not { } name || Workspace(name) is not { } workspace || MonitorOf(workspace) is null)
        {
            return false;
        }

        // A window covering its screen stays what the snapshot says it is;
        // any other one takes the layer it had.
        if (window.State != WindowState.Fullscreen)
        {
            window.State = remembered.Floating ? WindowState.Floating : WindowState.Tiling;
            window.PreviousState = window.State;
        }

        if (remembered.Floating && !remembered.FloatingRect.IsEmpty)
        {
            window.FloatingRect = remembered.FloatingRect;
        }

        window.Sticky = false;
        Place(window, workspace);
        return true;
    }

    /// <summary>Writes down where every managed window is now, for the next start.</summary>
    private void RememberPlacements()
    {
        if (_placements is null)
        {
            return;
        }

        foreach (DeskWindow window in _windows.Values)
        {
            if (!window.Managed)
            {
                continue;
            }

            var where = new State.Placed(
                window.Sticky ? null : window.Workspace,
                window.Sticky ? window.StickyMonitor : null,
                window.Sticky || window.State == WindowState.Floating
                    || (window.State == WindowState.Minimized && window.PreviousState == WindowState.Floating),
                window.FloatingRect ?? default);

            if (where.Workspace is null && where.StickyTo is null)
            {
                continue;
            }

            _placements.Remember(window.Handle, window.Snapshot.ProcessName, where);
        }
    }

    /// <summary>
    /// Reasons a window is refused that can stop being true.
    /// </summary>
    /// <remarks>
    /// Everything else -- a rule that says ignore, a window that is not a
    /// window -- is a fact about the window and does not change while it is
    /// open. These two are facts about the MOMENT it was looked at.
    /// </remarks>
    // SelfCloaked is temporary too: Zen (Firefox) shows a new window with
    // DWM_CLOAKED_APP set for its first ~200 ms of paint (measured 2026-09-22
    // 16:24: visible+cloaked at 661 ms after Ctrl+N, uncloaked at 863 ms), so
    // every new browser window was refused at exactly that instant and never
    // asked again -- floating, deaf to toggle-floating, until the daemon
    // restarted. A window that cloaks itself for the tray comes back the
    // same way, and that is the moment to take it.
    private static bool Temporary(UnmanagedReason reason) =>
        reason is UnmanagedReason.CloakedElsewhere or UnmanagedReason.OtherVirtualDesktop or UnmanagedReason.SelfCloaked;

    private void Update(DeskWindow window, WindowSnapshot snapshot)
    {
        WindowSnapshot was = window.Snapshot;
        window.Snapshot = snapshot;

        // Every change of rectangle, with whether it is where AkuWM put it:
        // the only way to tell a window moving itself from the desk moving it.
        if (window.Managed && was.FrameBounds != snapshot.FrameBounds && Log.DebugOn)
        {
            Log.Debug($"  moved {snapshot.Handle} {snapshot.ProcessName} {was.FrameBounds} -> {snapshot.FrameBounds}"
                + (window.Placed is { } put ? (put.CloseTo(snapshot.FrameBounds, PlacementSlack) ? " (where AkuWM put it)" : $" (AkuWM asked for {put})") : " (never placed)")
                + (snapshot.IsMaximized ? " maximized" : string.Empty));
        }

        // An application that puts ITSELF in the always-on-top band (NordVPN
        // does, on every activation) is not where the model thinks it is: a
        // tiled window up there sits over every floating one, and the model,
        // believing it had un-banded it, never asked again. The band is what
        // Windows says it is; Compute then re-asserts the wanted one.
        if (was.IsTopmost != snapshot.IsTopmost && window.Banded == was.IsTopmost)
        {
            window.Banded = snapshot.IsTopmost;
        }

        if (was.FrameBounds != snapshot.FrameBounds)
        {
            window.MovedAt = Now;

            // Moved by Windows in the seconds after a screen change: the
            // placement AkuWM made before is no longer where the window is,
            // and comparing the two read as the window refusing it ("will
            // not go to ... stopped asking"). A fresh request instead, once
            // the screens settle.
            if (ScreensMovingThings
                && window.Placed is { } put
                && !put.CloseTo(snapshot.FrameBounds, PlacementSlack))
            {
                window.Placed = null;
                window.PlacementRefused = false;
            }
        }

        // Whether this observation is the window ARRIVING where AkuWM put it,
        // or a later move that merely passes near that place. A drag through
        // the old rectangle read as "where AkuWM put it" and was undone by 65
        // px mid-drag (live, 13:17:23).
        bool arriving = !window.Landed
            && window.Placed is { } asked && asked.CloseTo(snapshot.FrameBounds, PlacementSlack);
        if (arriving)
        {
            window.Landed = true;
        }

        // A landed window whose SIZE alone shifts a little, still within the
        // slack of what was asked, is an application rounding itself (a
        // terminal to its cells); a change of position is a hand.
        bool rounding = !arriving
            && window.Landed
            && snapshot.FrameBounds.X == was.FrameBounds.X
            && snapshot.FrameBounds.Y == was.FrameBounds.Y
            && window.Placed is { } putAt && putAt.CloseTo(snapshot.FrameBounds, PlacementSlack);

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
                && !snapshot.Cloak.HasFlag(CloakKind.App)
                && !snapshot.Cloak.HasFlag(CloakKind.InheritedOrOtherDesktop)
                && snapshot.OnCurrentVirtualDesktop != false)
            {
                Reconsider(window, snapshot);
            }

            return;
        }

        if (snapshot.IsMinimized && window.State != WindowState.Minimized)
        {
            // Windows' doing, not the person's, when the screens have just
            // changed: every window of a lost or re-configured monitor is
            // parked in the taskbar (2026-09-22 17:41 and 18:06, all of the
            // main screen's windows "disappeared"). Brought back by Compute
            // once the screens settle.
            window.Parked = _screensChanged && Now - _screensChangedAt < ParkWindowMs;

            // Fullscreen is not a state to come back FROM: what the window was
            // before it covered the screen is what it goes back to when it
            // stops, and that is kept across the taskbar.
            window.WasFullscreen = window.State == WindowState.Fullscreen;
            if (!window.WasFullscreen)
            {
                window.PreviousState = window.State;
            }

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
                // A window Windows parked keeps its slot in the tree: put
                // back at the end of the tree, the two Zen tiles came back
                // swapped (2026-09-22 19:13). Compute skips a minimised tile.
                if (!window.Parked)
                {
                    leaving.Tiling.Remove(window.Handle);
                }

                if (leaving.Fullscreen == window.Handle)
                {
                    leaving.Fullscreen = WindowHandle.None;
                }
            }

            return;
        }

        if (!snapshot.IsMinimized && window.State == WindowState.Minimized)
        {
            window.Parked = false;
            // Back from the taskbar as something SMALLER than the screen: not
            // fullscreen any more, whatever it was when it went. Windows parks
            // every window of a monitor that is disabled and hands a maximised
            // one back un-maximised, 16 x 44 px short of the bounds (Brave,
            // 2026-09-22); taken as still fullscreen it was placed over the
            // bounds, marked to the taskbar, and could not be moved or
            // floated for the rest of the run. Near what AkuWM itself asked
            // for still counts: a console comes back a character cell short.
            // Only a placement that itself covered the screen vouches for
            // it: the tile rectangle from before it was maximised is within
            // the slack of the parked size, and is no evidence at all.
            MonitorSnapshot? on = MonitorByHandle(snapshot.Monitor)?.Snapshot;
            bool putOverTheScreen = on is not null
                && window.Placed is { } put
                && put.Contains(on.Bounds)
                && put.CloseTo(snapshot.FrameBounds, PlacementSlack);
            if (window.WasFullscreen && !ShadowModel.IsFullscreen(snapshot, on) && !putOverTheScreen)
            {
                window.WasFullscreen = false;
            }

            // Where Windows restored it is not where AkuWM put it, however
            // close: a tile handed back 26 px into the bar was within the
            // placement slack of its old rectangle and was left there.
            window.Placed = null;
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

        // A maximised rectangle seen while AkuWM has just asked Windows to
        // un-maximise the window is the old state on its way out, not the
        // window choosing to cover the screen.
        if (snapshot.IsMaximized && window.UnmaximizeAskedAt is { } askedAt && Now - askedAt < PlacementPatienceMs)
        {
            coversTheScreen = false;
        }

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
        // Not while HIDDEN: nobody can drag a cloaked window, and Windows
        // moves them itself on a display change; learning that put a
        // floating window somewhere the person never chose.
        // Nor while the screens are changing: Windows moves and clamps
        // windows itself for seconds after a monitor comes or goes (the
        // wake of 2026-09-22 19:02 sent NordVPN to the vertical screen and a
        // terminal to the main one, and cut a terminal's height to the
        // landscape screen it passed through); learned, that became where
        // the person put them. The rectangle the model has is put back once
        // the screens settle.
        // And only with the pointer on the window: a drag or an Alt+drag has
        // the hand on it, and every move Windows makes -- a screen change
        // it had not even reported yet sent Explorer to the vertical screen
        // (19:13) -- has the pointer somewhere else. An application moving
        // itself is not learned either; the placement puts it back.
        if ((window.State == WindowState.Floating || window.Sticky)
            && !window.Hidden
            && !ScreensMovingThings
            && PointerOn(snapshot.FrameBounds)
            && was.FrameBounds != snapshot.FrameBounds
            && !arriving
            && !rounding)
        {
            // Just crossed to another screen: what is arriving now is Windows
            // rescaling the window for the new DPI, a beat after the move, and
            // not the person resizing it. Reading it as theirs overwrote the
            // size the crossing had just chosen, which left
            // layout.across_monitors with no effect at all.
            // Zero is "never crossed", not "crossed at time zero" -- the same
            // trap MovedAt has, and it silently stopped every floating window
            // from ever learning where the person put it.
            // By SHAPE, not by time alone: the rescale changes the size and a
            // drag changes the position, so a move that keeps the size is the
            // person's however soon after the crossing it arrives. Time alone
            // threw away every drag in the second after a crossing and yanked
            // the window back to the point it crossed at.
            bool rescaling = window.CrossedAt != 0
                && Now - window.CrossedAt < CrossingMs
                && (snapshot.FrameBounds.Width != was.FrameBounds.Width
                    || snapshot.FrameBounds.Height != was.FrameBounds.Height);

            // A window that leaves its scaling to Windows is blown up in ONE
            // step the instant its border touches a screen of another scale
            // (1908 wide to 3318, 2026-09-22); a hand resizes a window by a
            // few pixels a tick. A jump that big in one observation is not
            // the person's: the size is kept and only the place follows.
            // GREW, in one step, while lying across two screens: that is the
            // whole signature. A shrink in one step is a command or a snap,
            // and a jump on one screen is a size somebody asked for.
            bool blownUp = !snapshot.PerMonitorDpi
                && !snapshot.IsMaximized
                && window.FloatingRect is not null
                && (snapshot.FrameBounds.Width - was.FrameBounds.Width > RescaleJump
                    || snapshot.FrameBounds.Height - was.FrameBounds.Height > RescaleJump)
                && SpansScreens(snapshot.FrameBounds);

            if (blownUp && window.SteadySize is null)
            {
                window.SteadySize = (window.FloatingRect!.Value.Width, window.FloatingRect.Value.Height);
            }

            if (window.SteadySize is { } steady)
            {
                // Blown up and still in the hand: the place follows, the size
                // it had is kept until AkuWM has put it down again.
                window.FloatingRect = new Rect(snapshot.FrameBounds.X, snapshot.FrameBounds.Y, steady.Width, steady.Height);
                Rehome(window, snapshot);
            }
            else if (!rescaling)
            {
                window.FloatingRect = snapshot.FrameBounds;
                Rehome(window, snapshot);
            }
        }

        if (coversTheScreen && window.State != WindowState.Fullscreen && !weMovedItThere)
        {
            SetFullscreen(window, true);
        }
        else if (!coversTheScreen && window.State == WindowState.Fullscreen
                 && was.FrameBounds != snapshot.FrameBounds
                 && window.Placed?.CloseTo(snapshot.FrameBounds, PlacementSlack) != true)
        {
            // Only when it is somewhere AkuWM did not put it. A console asked
            // to cover the screen lands a character cell short, which is
            // within the slack and not the window leaving fullscreen.
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

        if (window.Outlined is not null)
        {
            (_outlinedGone ??= []).Add(handle);
        }

        RememberApp(window);

        // Said out loud: a window closing while AkuWM had it hidden is a
        // window the person may not know is gone.
        if (window.Managed && window.Hidden)
        {
            Log.Info($"{window.Snapshot.ProcessName} \"{window.Snapshot.Title}\" closed while hidden on workspace {window.Workspace}");
        }
        else if (window.Managed)
        {
            Log.Debug(() => $"  closed {handle} {window.Snapshot.ProcessName} (workspace {window.Workspace ?? "sticky"})");
        }

        Forgotten?.Invoke(handle);
        _hidden.Remove(handle);
        _asked.Remove(handle);

        if (_wantFocus == handle)
        {
            _wantFocus = WindowHandle.None;
        }

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

    /// <param name="onto">
    /// The screen it becomes sticky to. Given by the caller when it knows
    /// better than the snapshot does -- a window dropped on a screen too small
    /// to hold it is still reported by Windows as being on the big one it
    /// covers most of, and re-deriving the monitor here threw that away.
    /// </param>
    private void MakeSticky(DeskWindow window, DeskMonitor? onto = null)
    {
        DeskMonitor? monitor = onto ?? MonitorByHandle(window.Snapshot.Monitor) ?? FocusedMonitor;
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

        if (window.State == WindowState.Fullscreen)
        {
            // Covering the screen is a workspace's, and it has just left its
            // workspace: the slot was released above, and a window left in
            // this state kept the taskbar mark and refused every toggle.
            window.State = WindowState.Floating;
            window.PreviousState = WindowState.Floating;
        }

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
        // The screen the pointer is on first (Diego: a window opens where the
        // mouse is), then the one with the focus.
        if (Settled && MonitorUnderPointer()?.Displayed is { } underPointer)
        {
            return underPointer;
        }

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
    /// <summary>The screen a window was just dropped on.</summary>
    /// <remarks>
    /// Windows' own answer, which is the screen the window covers most of,
    /// except when it does not FIT there. A window bigger than a screen can
    /// never win that comparison for it: most of such a window is always over
    /// the larger screen it came from, however deliberately it was dropped on
    /// the small one, so the area rule is structurally unable to express what
    /// the person did. Then the corner they dragged decides instead. Diego's
    /// terminal, 2020x2591, could not be put on the BenQ's 1920x1052 any other
    /// way (2026-09-21) -- it went back to the main monitor every time.
    /// </remarks>
    /// <summary>How geometry is carried between screens. See the config note.</summary>
    internal AcrossMode Across(bool tiling, bool dragged) =>
        Config.Layout?.AcrossMonitors?.Mode(tiling, dragged) ?? AcrossMode.Hybrid;

    /// <summary>
    /// The window keeps the point the person is holding it by.
    /// </summary>
    /// <remarks>
    /// A window that changes size as it crosses has to change size around
    /// something, and the cursor is the only thing on screen the person is
    /// actually looking at: grabbed a third of the way along its title bar, it
    /// is still a third of the way along after it shrinks, so the pointer never
    /// ends up outside the window it is dragging. Diego chose this over keeping
    /// the corner (2026-09-21).
    ///
    /// Only when the pointer is really on the window. A move that came from a
    /// command, or from a script, has nothing to anchor to and keeps the corner.
    /// </remarks>
    private Rect UnderTheCursor(Rect was, Rect now)
    {
        if ((was.Width == now.Width && was.Height == now.Height)
            || was.Width <= 0 || was.Height <= 0
            || _cursor?.Invoke() is not { } at
            || !was.Contains(at.X, at.Y))
        {
            return now;
        }

        return now with
        {
            X = at.X - (int)Math.Round((at.X - was.X) / (double)was.Width * now.Width),
            Y = at.Y - (int)Math.Round((at.Y - was.Y) / (double)was.Height * now.Height),
        };
    }

    private DeskMonitor? LandedOn(WindowSnapshot snapshot)
    {
        DeskMonitor? named = MonitorByHandle(snapshot.Monitor);
        Rect frame = snapshot.FrameBounds;

        if (named is null
            || (frame.Width <= named.TilingArea.Width && frame.Height <= named.TilingArea.Height))
        {
            return named;
        }

        // Too big for the screen Windows names: the hand decides. The top-left
        // corner was the tie-breaker before, and it is the LAST part of a
        // window to enter a screen to the right or below -- the terminal could
        // be dropped on the BenQ to the left and never on the vertical monitor
        // to the right.
        if (_cursor is not null)
        {
            (int x, int y) = _cursor();
            if (MonitorAtPoint(x, y) is { } underTheHand)
            {
                return underTheHand;
            }
        }

        for (int at = 0; at < _monitors.Count; at++)
        {
            if (_monitors[at].Snapshot.Bounds.Contains(frame.X, frame.Y))
            {
                return _monitors[at];
            }
        }

        return named;
    }

    /// <summary>The screen a window counts as being on now.</summary>
    private DeskMonitor? HomeOf(DeskWindow window) =>
        window.Sticky
            ? MonitorByRole(window.StickyMonitor ?? string.Empty)
            : window.Workspace is { } name && Workspace(name) is { } workspace
                ? MonitorOf(workspace)
                : null;

    private void Rehome(DeskWindow window, WindowSnapshot snapshot)
    {
        if (LandedOn(snapshot) is not { } landed)
        {
            return;
        }

        DeskMonitor? from = HomeOf(window);
        if (from is not null && !ReferenceEquals(from, landed) && window.FloatingRect is { } dropped)
        {
            // The size the new screen gives it, by layout.across_monitors. The
            // POSITION is left where the person dropped it in every mode --
            // that is the one thing about this move they chose themselves.
            Rect resized = AcrossMonitors.Resize(
                dropped,
                from.TilingArea,
                landed.TilingArea,
                Across(window.State == WindowState.Tiling, dragged: true));

            window.FloatingRect = UnderTheCursor(dropped, resized);
            window.CrossedAt = Now;
        }

        if (window.Sticky)
        {
            // Sticky means "on every workspace of ITS monitor", so a sticky
            // window dragged onto another screen follows that one from now on.
            // It was skipped here at first, for fear of un-sticking it, and the
            // result was the one window on this desk that could not be dragged
            // anywhere at all: clamped straight back onto the screen it was
            // stuck to, every time (Diego's PowerShell terminal, 2026-09-21).
            // MakeSticky re-binds it and leaves it sticky, which is the whole
            // difference from moving it to a workspace.
            if (!ReferenceEquals(MonitorByRole(window.StickyMonitor ?? string.Empty), landed))
            {
                MakeSticky(window, landed);
            }

            return;
        }

        if (window.Workspace is not { } name || Workspace(name) is not { } workspace)
        {
            return;
        }

        if (ReferenceEquals(landed, MonitorOf(workspace)))
        {
            return;
        }

        if (landed.Displayed is { } destination)
        {
            Place(window, destination);
        }
    }

    /// <summary>
    /// Puts a window on a workspace, carrying its geometry over when that is
    /// on another screen.
    /// </summary>
    /// <remarks>
    /// What a COMMAND does -- a workspace key, the raise-or-launch table, a
    /// monitor going away. Nobody chose a position, so the whole rectangle is
    /// mapped by <c>layout.across_monitors</c>, position included. A window
    /// the person DRAGGED goes through Rehome instead, which maps the size and
    /// leaves the corner they dropped it at alone.
    /// </remarks>
    private void PlaceAcross(DeskWindow window, Workspace destination)
    {
        if (HomeOf(window) is { } from
            && MonitorOf(destination) is { } to
            && !ReferenceEquals(from, to)
            && window.FloatingRect is { } rect)
        {
            window.FloatingRect = AcrossMonitors.Map(
                rect,
                from.TilingArea,
                to.TilingArea,
                Across(window.State == WindowState.Tiling, dragged: false));

            window.CrossedAt = Now;
        }

        Place(window, destination);
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
                // A maximised application that has the workspace to itself
                // makes room: un-maximised and tiled beside the newcomer,
                // which would otherwise be laid out behind a window covering
                // the screen (Notepad++ behind Brave on the vertical monitor,
                // 2026-09-22). Never a game.
                if (Config.Layout?.UnmaximizeToShare != false
                    && !workspace.Fullscreen.IsNone
                    && workspace.Fullscreen != window.Handle
                    && Window(workspace.Fullscreen) is { } covering
                    && covering.Snapshot.IsMaximized
                    && !LooksLikeAGame(covering))
                {
                    SetFullscreen(covering, false);
                }

                // A parked window kept its slot; it is simply back in it.
                if (!workspace.Tiling.Contains(window.Handle))
                {
                    WindowHandle beside = workspace.FocusOrder
                        .FirstOrDefault(h => workspace.Tiling.Contains(h));
                    workspace.Tiling.Add(window.Handle, beside, DirectionFor(workspace));
                }

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

    /// <summary>
    /// Which way the next window on a workspace splits: the workspace's own
    /// setting, then the layout default, then the shape of its screen.
    /// </summary>
    public SplitDirection DirectionFor(Workspace workspace)
    {
        if (workspace.Direction is { } own)
        {
            return own;
        }

        if (Config.Layout?.DefaultDirection is { Length: > 0 } configured
            && ParseDirection(configured) is { } explicitDirection)
        {
            return explicitDirection;
        }

        // "auto": the shape of the screen decides, so the portrait monitor
        // stacks and the wide one puts windows side by side.
        return MonitorOf(workspace)?.NaturalDirection ?? SplitDirection.Horizontal;
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
