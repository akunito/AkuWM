using AkuWM.Core.Config;
using AkuWM.Core.Matching;
using Xunit;

namespace AkuWM.Tests;

public class RuleMatcherTests
{
    private readonly RuleMatcher _matcher = new();

    private static RuleConfig Rule(params MatchCriteria[] alternatives) => new()
    {
        Id = "r-test",
        Match = [.. alternatives],
        Actions = ["float"],
    };

    [Fact]
    public void ExactMatchIgnoresCase() =>
        Assert.True(_matcher.Matches(
            Rule(new MatchCriteria { Process = "Telegram" }),
            new WindowFacts("telegram", "Qt5152QWindowIcon", "Telegram")));

    [Fact]
    public void ProcessMatchesWithOrWithoutTheExeSuffix() =>
        Assert.True(_matcher.Matches(
            Rule(new MatchCriteria { Process = "Telegram" }),
            new WindowFacts("Telegram.exe", "X", "Y")));

    [Fact]
    public void EveryFieldOfOneAlternativeMustMatch()
    {
        RuleConfig calculator = Rule(new MatchCriteria
        {
            Class = "ApplicationFrameWindow",
            Title = "Calculator",
        });

        Assert.True(_matcher.Matches(calculator, new WindowFacts("ApplicationFrameHost", "ApplicationFrameWindow", "Calculator")));
        // Same host, another store app: the title is what tells them apart.
        Assert.False(_matcher.Matches(calculator, new WindowFacts("ApplicationFrameHost", "ApplicationFrameWindow", "Photos")));
    }

    [Fact]
    public void AnyAlternativeIsEnough()
    {
        RuleConfig rule = Rule(
            new MatchCriteria { Process = "zebar" },
            new MatchCriteria { Process = "ShareX" });

        Assert.True(_matcher.Matches(rule, new WindowFacts("ShareX", "X", "Y")));
        Assert.False(_matcher.Matches(rule, new WindowFacts("explorer", "X", "Y")));
    }

    [Fact]
    public void RegexPatternsAreMarkedAndCaseInsensitive()
    {
        RuleConfig pip = Rule(new MatchCriteria
        {
            Class = "re:Chrome_WidgetWin_1|MozillaDialogClass",
            Title = "re:[Pp]icture.in.[Pp]icture",
        });

        Assert.True(_matcher.Matches(pip, new WindowFacts("chrome", "Chrome_WidgetWin_1", "Picture-in-Picture")));
        Assert.False(_matcher.Matches(pip, new WindowFacts("chrome", "Chrome_WidgetWin_1", "YouTube")));
    }

    [Fact]
    public void ADisabledRuleNeverFires()
    {
        RuleConfig rule = Rule(new MatchCriteria { Process = "Telegram" });
        rule.Enabled = false;
        Assert.False(_matcher.Matches(rule, new WindowFacts("Telegram", "X", "Y")));
    }

    [Fact]
    public void AnEmptyAlternativeMatchesNothingRatherThanEverything() =>
        Assert.False(_matcher.Matches(Rule(new MatchCriteria()), new WindowFacts("anything", "at", "all")));

    [Fact]
    public void ABadRegexIsReportedNotThrown()
    {
        Assert.False(RuleMatcher.IsValidPattern("re:[unclosed", out string? error));
        Assert.NotNull(error);
        Assert.True(RuleMatcher.IsValidPattern("plain text", out _));
    }
}
