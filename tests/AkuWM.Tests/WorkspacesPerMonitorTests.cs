using AkuWM.Core.Config;
using Xunit;

namespace AkuWM.Tests;

/// <summary>
/// However many workspaces, on however many screens, named however this desk
/// names them. Written because two literals said otherwise, and a GUI would
/// have inherited both.
/// </summary>
public class WorkspacesPerMonitorTests
{
    private static AkuWmConfig Desk(params (string Role, int Count)[] screens)
    {
        var config = new AkuWmConfig
        {
            Version = 1,
            Monitors = [],
            Workspaces = [],
            Rules = [],
        };

        int n = 1;
        foreach ((string role, int count) in screens)
        {
            config.Monitors!.Add(new MonitorConfig
            {
                Id = role,
                Match = new MonitorMatch { Edid = "EDID" + n++ },
            });

            for (int i = 1; i <= count; i++)
            {
                config.Workspaces!.Add(new WorkspaceConfig { Name = $"{role}-{i}", Monitor = role });
            }
        }

        return config;
    }

    [Fact]
    public void A_screen_can_be_called_whatever_this_desk_calls_it()
    {
        // The four roles AkuWM ships are a starting point, not a vocabulary.
        // A fifth screen is a person buying a monitor, not a mistake, and it
        // used to be a warning on every start.
        ValidationResult result = ConfigValidator.Validate(
            Desk(("main", 2), ("vertical", 2), ("the-telly", 2), ("wacom", 2), ("above", 2)));

        Assert.True(result.Ok);
        Assert.DoesNotContain(result.Warnings, w => w.Path.EndsWith(".id", StringComparison.Ordinal));
    }

    [Fact]
    public void A_screen_can_have_more_than_ten_workspaces()
    {
        AkuWmConfig config = Desk(("main", 14));
        config.Rules!.Add(new RuleConfig
        {
            Id = "the-eleventh",
            Match = [new MatchCriteria { Process = "zen" }],
            Actions = ["tile"],
            Target = new RuleTarget { Monitor = "main", Slot = 11 },
        });

        // A slot was 1-10, as a literal, so a person with twelve workspaces on
        // a screen could not aim a rule at the eleventh.
        Assert.True(ConfigValidator.Validate(config).Ok);
    }

    [Fact]
    public void A_slot_past_the_end_of_that_screen_is_still_refused()
    {
        AkuWmConfig config = Desk(("main", 3), ("second", 9));
        config.Rules!.Add(new RuleConfig
        {
            Id = "off-the-end",
            Match = [new MatchCriteria { Process = "zen" }],
            Actions = ["tile"],
            Target = new RuleTarget { Monitor = "main", Slot = 4 },
        });

        // Counted per screen, not across the desk: main has three even though
        // the desk has twelve.
        ValidationResult result = ConfigValidator.Validate(config);
        Assert.False(result.Ok);
        Assert.Contains(result.Errors, e => e.Message.Contains("1 to 3", StringComparison.Ordinal));
    }

    [Fact]
    public void Two_screens_can_have_different_numbers_of_them()
    {
        Assert.True(ConfigValidator.Validate(Desk(("main", 10), ("second", 3))).Ok);
    }
}
