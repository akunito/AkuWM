namespace AkuWM.Core.Config;

/// <summary>
/// The bottom layer. Everything AkuWM needs to run has a value here, so a
/// missing key in <c>common.json</c> is never a crash, and the file on disk
/// only has to carry what differs from this.
/// </summary>
public static class ConfigDefaults
{
    public const int SchemaVersion = 1;

    /// <summary>
    /// The bottom layer, under both files.
    /// </summary>
    /// <remarks>
    /// Nothing here is a default for an option this build does not act on.
    /// Shipping one is a promise not kept: it writes a value into the file a
    /// person then reads, sets, and wonders about. The options themselves stay
    /// in the schema -- the milestone that implements each is planned -- and
    /// the validator says so when one is set. Add the default back in the
    /// commit that makes it true.
    /// </remarks>
    public static AkuWmConfig Create() => new()
    {
        Version = SchemaVersion,
        General = new GeneralConfig
        {
            ToggleWorkspaceOnRefocus = true,
            ShowAllInTaskbar = false,
            ShowEmptyWorkspaces = true,
            RememberPlacements = true,
        },
        Gaps = new GapsConfig
        {
            Inner = 8,
            Outer = [0, 0, 0, 0],
            ScaleWithDpi = true,
        },
        Effects = new EffectsConfig
        {
            FocusedBorder = "#c4a7e7",
            OtherBorder = "none",
            Corners = "square",
            ReassertMs = 300,
        },
        Layout = new LayoutConfig
        {
            DefaultDirection = "auto",
            FloatUnresizable = true,
            FloatCentered = true,
            FloatingAboveMaximized = true,
            UnmaximizeToShare = true,
            WhenMonitorLeaves = "leave",
            WhenMonitorReturns = "restore",
            DragToTop = "fullscreen",
            AcrossMonitors = new AcrossConfig { All = "hybrid" },
        },
        Monitors = [],
        Workspaces = [],
        Rules = [],
        Shortcuts = [],
        Startup = [],
    };

    /// <summary>The monitor roles AkuWM knows, in the order they are preferred.</summary>
    public static readonly string[] MonitorRoles = ["main", "second", "tv", "left"];

    /// <summary>What a rule may ask for.</summary>
    public static readonly string[] RuleActions =
        ["float", "center", "tile", "sticky", "unsticky", "fullscreen", "minimize", "ignore", "anticheat"];

    public static readonly string[] ShortcutKinds = ["app", "wm", "exec", "send"];

    public static readonly string[] StartupPhases = ["now", "ipc"];

    public static readonly string[] DockerDaemons = ["rootful", "rootless"];

    /// <summary>
    /// Options this build reads, validates, and does not act on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every one of these is in the schema because the milestone that
    /// implements it is planned, and every one of them currently does
    /// nothing. Left unsaid, they are switches a GUI would offer and a person
    /// would set and wonder about -- and two of them are even range-checked,
    /// which makes them look more real than the rest.
    /// </para>
    /// <para>
    /// The validator warns on each one that is actually set. Take an entry out
    /// of this list in the same commit that makes it true; the test below this
    /// file's belt is that a key here must still exist in the model.
    /// </para>
    /// </remarks>
    public static readonly (string Path, string When)[] NotImplemented =
    [
        ("general.focus_follows_mouse", "click to focus was kept on 2026-09-15; nothing reads it"),
        ("general.cursor_jump", "nothing reads it"),
        ("general.startup_fold_virtual_desktops", "nothing reads it; AkuWM manages windows on every native desktop"),
        ("layout.resize_step_ppt", "the step comes from the command, not the configuration"),
        ("monitors[].primary", "AkuWM resolves monitors by identity, not by which is primary"),
        ("monitors[].orientation", "written by `monitors identify`, read by nobody yet"),
        ("settings.git.auto_push", "nothing pushes on its own; the Git section or `akuwm-cli git push` does"),
        ("settings.journal.interval_s", "the journal is written as it happens, not on a timer"),
        ("settings.repair.settle_ms", "the desk settles on its own measured constant, not this"),
    ];
}
