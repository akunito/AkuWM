using System.Text.Json.Serialization;

namespace AkuWM.Core.Config;

/// <summary>
/// The whole of AkuWM's configuration, one layer of it.
/// </summary>
/// <remarks>
/// Three layers are merged, in this order: the built-in defaults, the shared
/// <c>common.json</c>, and the machine's <c>&lt;PROFILE&gt;.json</c>. Every
/// property is nullable on purpose -- absent means "inherit from the layer
/// below", which is what lets a machine file override one field of one rule
/// without repeating the rest.
/// </remarks>
public sealed class AkuWmConfig
{
    public int? Version { get; set; }
    public GeneralConfig? General { get; set; }
    public GapsConfig? Gaps { get; set; }
    public EffectsConfig? Effects { get; set; }
    public LayoutConfig? Layout { get; set; }
    public List<MonitorConfig>? Monitors { get; set; }
    public List<WorkspaceConfig>? Workspaces { get; set; }
    public List<RuleConfig>? Rules { get; set; }
    public List<ShortcutConfig>? Shortcuts { get; set; }
    public List<StartupConfig>? Startup { get; set; }
    public AppsConfig? Apps { get; set; }
    public List<ToolConfig>? Tools { get; set; }
    public List<NodeConfig>? Nodes { get; set; }
    public SettingsConfig? Settings { get; set; }
}

/// <summary>Anything the machine layer can override entry by entry.</summary>
public interface IConfigItem
{
    /// <summary>The key the layers are merged on. Minted once, never derived.</summary>
    string? Id { get; set; }

    bool? Enabled { get; set; }
    string? Notes { get; set; }
    long? UpdatedAt { get; set; }
}

public sealed class GeneralConfig
{
    /// <summary>sway's <c>workspace_auto_back_and_forth</c>.</summary>
    public bool? ToggleWorkspaceOnRefocus { get; set; }

    /// <summary>Native Windows tracking (<c>SPI_SETACTIVEWINDOWTRACKING</c>), no raise.</summary>
    public bool? FocusFollowsMouse { get; set; }

    /// <summary><c>off</c> | <c>monitor_focus</c> | <c>workspace_focus</c>.</summary>
    public string? CursorJump { get; set; }

    /// <summary>When false the taskbar shows only the windows of the visible workspaces.</summary>
    /// <summary>
    /// Whether the taskbar keeps a button for windows on workspaces nobody is
    /// looking at.
    /// </summary>
    /// <remarks>
    /// Only the workspace half. Whether each screen's bar shows that screen's
    /// windows or every window is a Windows setting -- Taskbar behaviours,
    /// "show my taskbar apps on" -- and AkuWM does not touch it.
    /// </remarks>
    public bool? ShowAllInTaskbar { get; set; }

    /// <summary>
    /// Whether a window that was open when AkuWM starts goes back to the
    /// workspace, layer and rectangle it had under the previous run (true),
    /// or is placed like a new one. A rule that names a workspace wins
    /// either way. Read at start; <c>akuwm daemon --fresh</c> ignores it once.
    /// </summary>
    public bool? RememberPlacements { get; set; }

    /// <summary>
    /// A window with no rule of its own opens as the last window of that
    /// application was closed: floating or tiled, the same size, at the same
    /// place on the screen it opens on. Default true. Elevated windows are
    /// left out.
    /// </summary>
    public bool? RememberApps { get; set; }

    /// <summary>
    /// A window with no rule of its own opens on the screen the pointer is
    /// on, not the one that has the focus. Default true.
    /// </summary>
    public bool? OpenUnderPointer { get; set; }

    /// <summary>
    /// Whether the bar lists a workspace that has no windows in it.
    /// </summary>
    /// <remarks>
    /// True -- the default -- lists every workspace the configuration gives a
    /// screen, so the bar is a fixed map of the number row. False lists only
    /// the ones with something in them, plus the one being looked at, plus any
    /// marked <c>keep_alive</c>: that is the middle ground, ten assigned and
    /// only the few you always want present.
    /// </remarks>
    public bool? ShowEmptyWorkspaces { get; set; }

    /// <summary>Fold stray native virtual desktops into the first one at startup.</summary>
    public bool? StartupFoldVirtualDesktops { get; set; }

    /// <summary>
    /// The hotkey process (the AutoHotkey script) that is stopped while a
    /// window of a rule with the <c>anticheat</c> action exists, and started
    /// again when the last one is gone. Anti-cheats block or disconnect by
    /// process NAME (NCSoft on Aion 2, December 2025); a pause or an
    /// uninstalled hook hides nothing from a process scan, only absence does.
    /// </summary>
    public HotkeyHostConfig? HotkeyHost { get; set; }
}

public sealed class HotkeyHostConfig
{
    /// <summary>Process name without <c>.exe</c>, e.g. <c>AutoHotkey64_UIA</c>.</summary>
    public string? Process { get; set; }

    /// <summary>What starts it again: a <c>.lnk</c>, an executable, or a command the shell runs.</summary>
    public string? Command { get; set; }
}

public sealed class GapsConfig
{
    public int? Inner { get; set; }

    /// <summary>top, right, bottom, left.</summary>
    /// <summary>
    /// One number for every side, or four: top, right, bottom, left.
    /// </summary>
    [System.Text.Json.Serialization.JsonConverter(typeof(OuterGapsConverter))]
    public int[]? Outer { get; set; }

    public bool? ScaleWithDpi { get; set; }
}

public sealed class EffectsConfig
{
    /// <summary>
    /// <c>#rrggbb</c>, <c>"none"</c> for no border, or null to inherit.
    /// </summary>
    /// <remarks>
    /// Null and "none" are different on purpose. Null means the layer below
    /// decides, which is what every other option in this file means by null;
    /// "none" is a choice. Without the distinction a GUI could never clear a
    /// border -- unsetting the field would hand back the default colour from
    /// the layer underneath.
    /// </remarks>
    public string? FocusedBorder { get; set; }

    /// <summary>The same, for every window that does not have the focus.</summary>
    public string? OtherBorder { get; set; }

    /// <summary>
    /// <c>square</c> | <c>round</c> | <c>round_small</c> | <c>default</c>.
    /// </summary>
    /// <remarks>
    /// Square is what tiling wants: a rounded corner leaks the desktop through
    /// the corner of every gap. Windows 11 only; older builds ignore it.
    /// </remarks>
    public string? Corners { get; set; }

    /// <summary>
    /// <c>keep</c> | <c>hide</c>: whether the window has a title bar.
    /// </summary>
    /// <remarks>
    /// Hiding it takes the minimise, maximise and close buttons with it --
    /// they live in the bar -- and gives back about thirty pixels a window.
    /// It only works on windows that use the shell's frame: an application
    /// that draws its own chrome inside the client area, which is most modern
    /// ones, is unaffected. AkuWM puts the bar back when it stops managing the
    /// window, because a window with no title bar and no way to close it is
    /// worse than one with a title bar nobody wanted.
    /// </remarks>
    public string? TitleBar { get; set; }

    /// <summary>
    /// How solid the window is, 0.05 to 1.
    /// </summary>
    /// <remarks>
    /// Measured and written down in the plan: a window at zero is optimised
    /// away by the compositor and cannot be clicked, so the floor is not zero.
    /// </remarks>
    public double? Opacity { get; set; }

    /// <summary>
    /// Milliseconds after a focus change to send the focused window's
    /// decoration once more; 0 turns it off. Global block only.
    /// </summary>
    /// <remarks>
    /// Windows Terminal and every Chromium window (VS Code) set their own
    /// DWMWA_BORDER_COLOR on activation, a beat after AkuWM's: the border
    /// showed for an instant and went (live desk 2026-09-22). Live on reload.
    /// </remarks>
    public int? ReassertMs { get; set; }

    /// <summary>
    /// The border AkuWM draws itself, in pixels at 100 % (1 to 8, default 2).
    /// </summary>
    /// <remarks>
    /// Only the outline (the border around windows the shell will not
    /// decorate, and every border once <c>border: outline</c> exists) has a
    /// width; the shell's own border is one pixel and not negotiable.
    /// </remarks>
    public int? BorderWidth { get; set; }

    /// <summary>
    /// <c>#rrggbb</c> for the focused border of an ELEVATED window, so a
    /// window nothing but the outline can decorate is told apart; null takes
    /// <see cref="FocusedBorder"/>.
    /// </summary>
    public string? ElevatedBorder { get; set; }

    /// <summary>A soft shadow under the window. Read and validated; drawn by a later build.</summary>
    public bool? Shadow { get; set; }

    /// <summary>A glow outside the border, in pixels at 100 % (0 to 32). Read and validated; drawn by a later build.</summary>
    public int? Glow { get; set; }
}

public sealed class LayoutConfig
{
    /// <summary><c>auto</c> (split the longer side) | <c>horizontal</c> | <c>vertical</c>.</summary>
    public string? DefaultDirection { get; set; }

    public int? ResizeStepPpt { get; set; }

    /// <summary>A window that refuses to be resized is floated instead of fighting the layout.</summary>
    public bool? FloatUnresizable { get; set; }

    /// <summary>
    /// Where a window goes the first time it floats.
    /// </summary>
    /// <remarks>
    /// True puts it in the middle of the screen at two thirds the size, which
    /// is what a window that filled half the screen needs -- it looks broken
    /// floating at tiling size. False leaves it exactly where it was, which is
    /// what somebody popping a window out of the layout to nudge it wants. A
    /// window that has floated before goes back where it was either way.
    /// </remarks>
    public bool? FloatCentered { get; set; }

    /// <summary>
    /// Whether floating windows stay above a MAXIMISED application window
    /// that happens to cover its monitor (true), or leave the always-on-top
    /// band for it as they do for a game (false). Live on reload.
    /// </summary>
    /// <remarks>
    /// On a monitor with no taskbar a maximised browser covers the whole
    /// screen and counted as fullscreen: every floating window left the
    /// band for it, and the next click on the browser put them behind it,
    /// lost. A game is told apart by its frame -- a borderless popup with no
    /// resize border, or an elevated one -- and still gets the shield that
    /// keeps its direct path to the screen.
    /// </remarks>
    public bool? FloatingAboveMaximized { get; set; }

    /// <summary>
    /// Whether a maximised application window alone on a workspace is
    /// un-maximised and tiled when a second tiling window arrives (true), or
    /// keeps covering the screen with the newcomer behind it (false). Live
    /// on reload; a window that looks like a game is never touched.
    /// </summary>
    public bool? UnmaximizeToShare { get; set; }

    /// <summary>
    /// What happens to a monitor's windows when it goes away (sleeps, is
    /// unplugged): <c>move_windows</c> lends them to the workspace on screen
    /// of the monitor the person is on, <c>move_workspaces</c> lends the
    /// whole workspaces to that monitor (put away, reachable by the
    /// workspace keys), <c>leave</c> only stops hiding them -- the default,
    /// Diego's choice: nothing changes place by itself, ever. The
    /// <c>fetch-windows</c> command borrows the other screens' windows on
    /// demand instead. Live on reload.
    /// </summary>
    public string? WhenMonitorLeaves { get; set; }

    /// <summary>
    /// What happens when it comes back: <c>restore</c> puts every window
    /// back on the workspace, in the layout and at the rectangle it had when
    /// the monitor went, whatever was done with it meanwhile; <c>keep</c>
    /// leaves the windows where they are now. Live on reload.
    /// </summary>
    public string? WhenMonitorReturns { get; set; }

    /// <summary>
    /// What dragging a TILED window to the top edge does.
    /// </summary>
    /// <remarks>
    /// <c>fullscreen</c> (the default) covers the screen without leaving the
    /// layout: the window keeps its place and drops back into it when it stops
    /// being fullscreen. <c>float_maximized</c> and <c>float_fullscreen</c>
    /// take it out of the layout first, so the tiles close over the gap and it
    /// has to be put back by hand. <c>none</c> ignores the gesture.
    ///
    /// A floating window is snapped by Windows itself and never reaches this.
    /// </remarks>
    public string? DragToTop { get; set; }

    /// <summary>
    /// How a window's geometry is carried from one screen to another.
    /// </summary>
    /// <remarks>
    /// <c>absolute</c> keeps the pixels: a window of 990x990 arrives as
    /// 990x990 whatever the screen. <c>proportional</c> keeps the share of the
    /// screen: the same window, off a 1000x1000 screen onto a 2000x2000 one,
    /// arrives as 1980x1980, and at the same fraction across it.
    /// <c>hybrid</c> (the default) is absolute until the window would not fit,
    /// and proportional then -- so the size a person chose is respected except
    /// where respecting it means a window hanging off the screen.
    ///
    /// A window DRAGGED across keeps the corner it was dropped at in every
    /// mode: the position on the new screen is the one thing the person did
    /// choose. The mode governs the size for those, and both for a window
    /// moved by a command.
    /// </remarks>
    public AcrossConfig? AcrossMonitors { get; set; }
}

public sealed class MonitorConfig : IConfigItem
{
    /// <summary>
    /// Gaps for this screen only, over the top of the global block.
    /// </summary>
    /// <remarks>
    /// A portrait monitor and a 4K one do not want the same numbers, and the
    /// taskbar is on one of them. A field left out here is taken from
    /// <c>gaps</c>, so "just the vertical one, wider" is two lines.
    /// </remarks>
    public GapsConfig? Gaps { get; set; }

    /// <summary>The role: <c>main</c>, <c>second</c>, <c>tv</c>, <c>left</c>.</summary>
    public string? Id { get; set; }

    public string? Name { get; set; }
    public MonitorMatch? Match { get; set; }
    public bool? Primary { get; set; }

    /// <summary><c>horizontal</c> | <c>vertical</c>; informational, the OS owns the rotation.</summary>
    public string? Orientation { get; set; }

    public bool? Enabled { get; set; }
    public string? Notes { get; set; }
    public long? UpdatedAt { get; set; }
}

/// <summary>How a physical monitor is recognised. By identity, never by index.</summary>
public sealed class MonitorMatch
{
    /// <summary>Manufacturer + product + serial, from the display device path.</summary>
    public string? Edid { get; set; }

    /// <summary>The friendly name, only as a fallback and only when it is unique.</summary>
    public string? Name { get; set; }

    public string? DevicePath { get; set; }
}

public sealed class WorkspaceConfig : IConfigItem
{
    /// <summary>sway's numbering: 11-10 on the first role, 21-20 on the second.</summary>
    public string? Name { get; set; }

    /// <summary>A monitor <em>role</em>, not an index.</summary>
    public string? Monitor { get; set; }

    public string? Direction { get; set; }
    public bool? KeepAlive { get; set; }
    public string? DisplayName { get; set; }

    /// <summary>Workspaces are merged on their name; the id field is never used.</summary>
    [JsonIgnore]
    public string? Id
    {
        get => Name;
        set => Name = value;
    }

    public bool? Enabled { get; set; }
    public string? Notes { get; set; }
    public long? UpdatedAt { get; set; }
}

public sealed class RuleConfig : IConfigItem
{
    public string? Id { get; set; }
    public string? Name { get; set; }

    /// <summary>Alternatives: the rule fires when ANY entry matches.</summary>
    public List<MatchCriteria>? Match { get; set; }

    /// <summary><c>float</c>, <c>tile</c>, <c>sticky</c>, <c>ignore</c>, <c>fullscreen</c>, <c>minimize</c>.</summary>
    public List<string>? Actions { get; set; }

    /// <summary>Where a matching window opens, symbolically: <c>{ "monitor": "main", "slot": 3 }</c>.</summary>
    public RuleTarget? Target { get; set; }

    /// <summary>
    /// How a matching window should look, overriding the global <c>effects</c>.
    /// </summary>
    /// <remarks>
    /// The same shape as the global block on purpose, so the GUI renders one
    /// form in two places and a person reads one set of names. Actions are
    /// verbs -- do this to the window -- and these are properties: be like
    /// this. Folding them into the action list would have meant parsing
    /// <c>"opacity:0.9"</c> out of a string, which is a schema a GUI cannot
    /// render and a validator can only guess at.
    /// </remarks>
    public EffectsConfig? Effects { get; set; }

    public bool? Enabled { get; set; }
    public string? Notes { get; set; }
    public long? UpdatedAt { get; set; }
}

/// <summary>
/// One alternative of a rule. The fields present must ALL match (AND); a value
/// starting with <c>re:</c> is a .NET regular expression, anything else is an
/// exact, case-insensitive string.
/// </summary>
public sealed class MatchCriteria
{
    public string? Process { get; set; }
    public string? Class { get; set; }
    public string? Title { get; set; }
}

public sealed class RuleTarget
{
    public string? Monitor { get; set; }

    /// <summary>1-10, resolved through the workspace table of that monitor role.</summary>
    public int? Slot { get; set; }

    public string? Workspace { get; set; }
}

public sealed class ShortcutConfig : IConfigItem
{
    public string? Id { get; set; }

    /// <summary>e.g. <c>Hyper+Shift+C</c>. Hyper is Ctrl+Alt+Win, as in sway.</summary>
    public string? Keys { get; set; }

    /// <summary><c>app</c> | <c>wm</c> | <c>exec</c> | <c>send</c>.</summary>
    public string? Kind { get; set; }

    /// <summary>For <c>app</c>: the process to raise, e.g. <c>Telegram.exe</c>.</summary>
    public string? App { get; set; }

    /// <summary>What to run, the WM command, or the chord to inject.</summary>
    public string? Command { get; set; }

    public string? Category { get; set; }

    /// <summary>Scopes the chord to a window; absent means global.</summary>
    public MatchCriteria? When { get; set; }

    public string? Name { get; set; }
    public bool? Enabled { get; set; }
    public string? Notes { get; set; }
    public long? UpdatedAt { get; set; }
}

public sealed class StartupConfig : IConfigItem
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    public string? Command { get; set; }

    /// <summary>What must be up first: <c>now</c> (default) or <c>ipc</c>.</summary>
    public string? After { get; set; }

    public int? DelayMs { get; set; }
    public bool? Enabled { get; set; }
    public string? Notes { get; set; }
    public long? UpdatedAt { get; set; }
}

public sealed class AppsConfig
{
    /// <summary>Path to the winget catalogue, relative to the config directory.</summary>
    public string? Catalogue { get; set; }
}

public sealed class ToolConfig : IConfigItem
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    public string? Command { get; set; }
    public string? Icon { get; set; }
    public bool? Enabled { get; set; }
    public string? Notes { get; set; }
    public long? UpdatedAt { get; set; }
}

public sealed class NodeConfig : IConfigItem
{
    public string? Id { get; set; }
    public string? Name { get; set; }

    /// <summary><c>user@host:port</c>.</summary>
    public string? Ssh { get; set; }

    public List<string>? Daemons { get; set; }
    public string? PrometheusInstance { get; set; }
    public bool? Enabled { get; set; }
    public string? Notes { get; set; }
    public long? UpdatedAt { get; set; }
}

public sealed class SettingsConfig
{
    public GitSettings? Git { get; set; }
    public JournalSettings? Journal { get; set; }
    public RepairSettings? Repair { get; set; }
}

public sealed class GitSettings
{
    public bool? AutoCommit { get; set; }
    public bool? AutoPush { get; set; }
}

public sealed class JournalSettings
{
    public int? IntervalS { get; set; }
}

public sealed class RepairSettings
{
    /// <summary>How long a display change is left to settle before the repair runs.</summary>
    public int? SettleMs { get; set; }
}
