using AkuWM.Core.Config;
using Xunit;

namespace AkuWM.Tests;

public class ConfigValidatorTests
{
    private static AkuWmConfig Workable() => ConfigMerge.Merge(ConfigDefaults.Create(), new AkuWmConfig
    {
        Version = 1,
        Monitors = [new MonitorConfig { Id = "main", Match = new MonitorMatch { Edid = "SAM0F1E" } }],
        Workspaces = [new WorkspaceConfig { Name = "11", Monitor = "main" }],
        Rules =
        [
            new RuleConfig
            {
                Id = "r-1",
                Match = [new MatchCriteria { Process = "Telegram" }],
                Actions = ["float", "sticky"],
            },
        ],
        Shortcuts =
        [
            new ShortcutConfig { Id = "k-1", Keys = "Hyper+L", Kind = "app", App = "Telegram.exe", Command = "telegram" },
        ],
    });

    private static IEnumerable<string> ErrorPaths(AkuWmConfig config) =>
        ConfigValidator.Validate(config).Errors.Select(e => e.Path);

    [Fact]
    public void AWorkableConfigurationHasNoErrorsAndNoWarnings()
    {
        ValidationResult result = ConfigValidator.Validate(Workable());

        Assert.True(result.Ok);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void TwoEntriesWithTheSameIdAreRefused()
    {
        AkuWmConfig config = Workable();
        config.Rules!.Add(new RuleConfig
        {
            Id = "r-1",
            Match = [new MatchCriteria { Process = "Spotify" }],
            Actions = ["float"],
        });

        Assert.Contains("rules[1].id", ErrorPaths(config));
    }

    [Fact]
    public void AnUnknownActionIsRefused()
    {
        AkuWmConfig config = Workable();
        config.Rules![0].Actions = ["levitate"];

        Assert.Contains("rules[0].actions", ErrorPaths(config));
    }

    [Fact]
    public void FloatAndTileContradictEachOther()
    {
        AkuWmConfig config = Workable();
        config.Rules![0].Actions = ["float", "tile"];

        Assert.Contains("rules[0].actions", ErrorPaths(config));
    }

    [Fact]
    public void IgnoreWithOtherActionsIsOnlyAWarning()
    {
        AkuWmConfig config = Workable();
        config.Rules![0].Actions = ["ignore", "float"];

        ValidationResult result = ConfigValidator.Validate(config);

        Assert.True(result.Ok);
        Assert.Contains(result.Warnings, w => w.Path == "rules[0].actions");
    }

    [Fact]
    public void AnEmptyMatchWouldCatchEveryWindow()
    {
        AkuWmConfig config = Workable();
        config.Rules![0].Match = [new MatchCriteria()];

        Assert.Contains("rules[0].match[0]", ErrorPaths(config));
    }

    [Fact]
    public void ABadRegularExpressionIsRefusedBeforeItRuns()
    {
        AkuWmConfig config = Workable();
        config.Rules![0].Match = [new MatchCriteria { Title = "re:[unclosed" }];

        Assert.Contains("rules[0].match[0].title", ErrorPaths(config));
    }

    [Fact]
    public void AWorkspaceOnAnUndeclaredRoleIsRefused()
    {
        AkuWmConfig config = Workable();
        config.Workspaces!.Add(new WorkspaceConfig { Name = "21", Monitor = "second" });

        Assert.Contains("workspaces[1].monitor", ErrorPaths(config));
    }

    [Fact]
    public void TheSameChordCannotBeBoundTwice()
    {
        AkuWmConfig config = Workable();
        config.Shortcuts!.Add(new ShortcutConfig
        {
            Id = "k-2",
            Keys = "hyper+l",       // the same chord, spelled differently
            Kind = "exec",
            Command = "notepad",
        });

        Assert.Contains("shortcuts[1].keys", ErrorPaths(config));
    }

    [Fact]
    public void TheSameChordIsFineWhenTheTwoAreScopedToDifferentWindows()
    {
        AkuWmConfig config = Workable();
        config.Shortcuts![0].When = new MatchCriteria { Process = "WindowsTerminal" };
        config.Shortcuts.Add(new ShortcutConfig
        {
            Id = "k-2",
            Keys = "Hyper+L",
            Kind = "exec",
            Command = "notepad",
            When = new MatchCriteria { Process = "explorer" },
        });

        Assert.True(ConfigValidator.Validate(config).Ok);
    }

    [Fact]
    public void ADisabledShortcutDoesNotCollide()
    {
        AkuWmConfig config = Workable();
        config.Shortcuts!.Add(new ShortcutConfig
        {
            Id = "k-2",
            Keys = "Hyper+L",
            Kind = "exec",
            Command = "notepad",
            Enabled = false,
        });

        Assert.True(ConfigValidator.Validate(config).Ok);
    }

    [Fact]
    public void AChordThatCannotBeParsedIsRefused()
    {
        AkuWmConfig config = Workable();
        config.Shortcuts![0].Keys = "Hyper+Nonsense";

        Assert.Contains("shortcuts[0].keys", ErrorPaths(config));
    }

    [Fact]
    public void ABorderThatIsNotAColourIsRefused()
    {
        AkuWmConfig config = Workable();
        config.Effects = new EffectsConfig { FocusedBorder = "purple" };

        Assert.Contains("effects.focused_border", ErrorPaths(config));
    }

    [Fact]
    public void AConfigurationFromTheFutureIsRefusedRatherThanGuessedAt()
    {
        AkuWmConfig config = Workable();
        config.Version = ConfigDefaults.SchemaVersion + 1;

        Assert.Contains("version", ErrorPaths(config));
    }

    [Fact]
    public void AMonitorWithoutAnIdentityIsAWarningNotAnError()
    {
        AkuWmConfig config = Workable();
        config.Monitors![0].Match = null;

        ValidationResult result = ConfigValidator.Validate(config);

        Assert.True(result.Ok);
        Assert.Contains(result.Warnings, w => w.Path == "monitors[0].match");
    }

    [Fact]
    public void TwoPrimaryMonitorsAreRefused()
    {
        AkuWmConfig config = Workable();
        config.Monitors![0].Primary = true;
        config.Monitors.Add(new MonitorConfig
        {
            Id = "second",
            Primary = true,
            Match = new MonitorMatch { Edid = "NSL0001" },
        });

        Assert.Contains("monitors", ErrorPaths(config));
    }

    [Fact]
    public void AStartupEntryWithoutACommandIsRefused()
    {
        AkuWmConfig config = Workable();
        config.Startup = [new StartupConfig { Id = "u-1", Name = "nothing" }];

        Assert.Contains("startup[0].command", ErrorPaths(config));
    }

    [Fact]
    public void AnUnknownStartupPhaseIsRefused()
    {
        AkuWmConfig config = Workable();
        config.Startup = [new StartupConfig { Id = "u-1", Command = "zebar", After = "later" }];

        Assert.Contains("startup[0].after", ErrorPaths(config));
    }
}
