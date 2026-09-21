using AkuWM.Core.Config;
using Xunit;

namespace AkuWM.Tests;

/// <summary>
/// Options this build reads, validates, and does not act on.
/// </summary>
/// <remarks>
/// A third of the schema was in this state and said nothing about it, which in
/// a GUI is a page of switches that lie. The rule here is the one that keeps
/// the list honest in both directions: a key on the list must exist, and a key
/// that has been implemented must come off it.
/// </remarks>
public class NotImplementedTests
{
    [Fact]
    public void Setting_one_of_them_is_a_warning_and_never_an_error()
    {
        AkuWmConfig config = DeskFixture.Configuration();
        config.General!.CursorJump = "monitor_focus";

        ValidationResult result = ConfigValidator.Validate(config);

        // Never an error: a configuration written for a newer AkuWM must still
        // boot an older one.
        Assert.True(result.Ok);
        Assert.Contains(result.Warnings, w => w.Path == "general.cursor_jump");
    }

    [Fact]
    public void Leaving_one_unset_says_nothing()
    {
        AkuWmConfig config = DeskFixture.Configuration();
        config.General!.CursorJump = null;

        // Warning about every unimplemented key on every start would train a
        // person to ignore the warnings, which is worse than not having them.
        Assert.DoesNotContain(
            ConfigValidator.Validate(config).Warnings,
            w => w.Path == "general.cursor_jump");
    }

    [Fact]
    public void A_key_inside_a_list_is_found()
    {
        AkuWmConfig config = DeskFixture.Configuration();
        config.Monitors![0].Orientation = "horizontal";

        Assert.Contains(
            ConfigValidator.Validate(config).Warnings,
            w => w.Path == "monitors[].orientation");
    }

    [Fact]
    public void Every_path_on_the_list_is_one_the_schema_actually_has()
    {
        // The list is how a person is told an option does nothing. A path on
        // it that the schema does not have would be a warning nobody could
        // ever act on, and one misspelling would hide a real key for ever.
        AkuWmConfig everything = Filled();
        ValidationResult result = ConfigValidator.Validate(everything);

        foreach ((string path, _) in ConfigDefaults.NotImplemented)
        {
            Assert.Contains(result.Warnings, w => w.Path == path);
        }
    }

    private static AkuWmConfig Filled()
    {
        AkuWmConfig config = DeskFixture.Configuration();

        config.General!.FocusFollowsMouse = true;
        config.General.CursorJump = "off";
        config.General.StartupFoldVirtualDesktops = true;
        config.Layout!.ResizeStepPpt = 5;
        config.Monitors![0].Primary = true;
        config.Monitors[0].Orientation = "horizontal";
        config.Workspaces![0].KeepAlive = true;
        config.Apps = new AppsConfig { Catalogue = "x.json" };
        config.Tools = [];
        config.Nodes = [];
        config.Settings = new SettingsConfig
        {
            Git = new GitSettings { AutoCommit = true, AutoPush = true },
            Journal = new JournalSettings { IntervalS = 60 },
            Repair = new RepairSettings { SettleMs = 6000 },
        };

        return config;
    }
}
