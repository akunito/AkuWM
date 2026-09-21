namespace AkuWM.Core.Config;

/// <summary>
/// The bottom layer. Everything AkuWM needs to run has a value here, so a
/// missing key in <c>common.json</c> is never a crash, and the file on disk
/// only has to carry what differs from this.
/// </summary>
public static class ConfigDefaults
{
    public const int SchemaVersion = 1;

    public static AkuWmConfig Create() => new()
    {
        Version = SchemaVersion,
        General = new GeneralConfig
        {
            ToggleWorkspaceOnRefocus = true,
            FocusFollowsMouse = true,
            CursorJump = "off",
            ShowAllInTaskbar = false,
            StartupFoldVirtualDesktops = true,
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
        },
        Layout = new LayoutConfig
        {
            DefaultDirection = "auto",
            ResizeStepPpt = 5,
            FloatUnresizable = true,
        },
        Monitors = [],
        Workspaces = [],
        Rules = [],
        Shortcuts = [],
        Startup = [],
        Apps = new AppsConfig { Catalogue = "../winget-packages.json" },
        Tools = [],
        Nodes = [],
        Settings = new SettingsConfig
        {
            Git = new GitSettings { AutoCommit = true, AutoPush = false },
            Journal = new JournalSettings { IntervalS = 60 },
            Repair = new RepairSettings { SettleMs = 6000 },
        },
    };

    /// <summary>The monitor roles AkuWM knows, in the order they are preferred.</summary>
    public static readonly string[] MonitorRoles = ["main", "second", "tv", "left"];

    /// <summary>What a rule may ask for.</summary>
    public static readonly string[] RuleActions =
        ["float", "tile", "sticky", "unsticky", "fullscreen", "minimize", "ignore"];

    public static readonly string[] ShortcutKinds = ["app", "wm", "exec", "send"];

    public static readonly string[] StartupPhases = ["now", "ipc"];
}
