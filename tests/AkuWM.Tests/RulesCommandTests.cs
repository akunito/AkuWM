using System.Text.Json.Nodes;
using AkuWM.Core.Commands;
using AkuWM.Core.Config;
using Xunit;

namespace AkuWM.Tests;

/// <summary>
/// What the GUI needs before it can exist: point at a window and be told the
/// words a rule for it is written in, pick a process off a list, and see which
/// configured rules are doing nothing.
/// </summary>
public class RulesCommandTests
{
    private readonly DeskFixture _fixture;
    private readonly RulesCommand _rules;

    public RulesCommandTests()
    {
        AkuWmConfig config = DeskFixture.Configuration();
        config.Rules =
        [
            new RuleConfig
            {
                Id = "float-the-terminal",
                Match = [new MatchCriteria { Process = "alacritty" }],
                Actions = ["float", "sticky"],
            },
            new RuleConfig
            {
                Id = "for-an-app-that-is-not-running",
                Match = [new MatchCriteria { Process = "nothing-here" }],
                Actions = ["ignore"],
            },
        ];

        _fixture = new DeskFixture(config);
        _rules = new RulesCommand(read => read(_fixture.Desk));
    }

    private JsonObject Run(params string[] tokens) =>
        (JsonObject)_rules.Execute(string.Join(' ', tokens), tokens).Data!;

    [Fact]
    public void A_window_is_turned_into_the_words_a_rule_is_written_in()
    {
        _fixture.Open(1, process: "zen", className: "MozillaWindowClass", title: "(2) Notes — Zen");
        _fixture.Turn();
        _fixture.Desk.Focus(DeskFixture.W(1));

        JsonObject answer = Run("rules", "for", "--focused");

        Assert.Equal("zen", (string?)answer["process"]);
        Assert.Equal("MozillaWindowClass", (string?)answer["class"]);

        JsonArray suggested = answer["suggested"]!.AsArray();
        Assert.Equal(3, suggested.Count);
        Assert.Equal("zen", (string?)suggested[0]!["match"]!["process"]);
        Assert.Equal("MozillaWindowClass", (string?)suggested[1]!["match"]!["class"]);
    }

    [Fact]
    public void A_title_full_of_punctuation_does_not_become_a_regular_expression_by_accident()
    {
        _fixture.Open(1, process: "vesktop", title: "(2) Discord | #general");
        _fixture.Turn();
        _fixture.Desk.Focus(DeskFixture.W(1));

        string? pattern = (string?)Run("rules", "for", "--focused")["suggested"]![2]!["match"]!["title"];

        Assert.StartsWith("re:", pattern);
        Assert.Matches(pattern![3..], "(2) Discord | #general");
    }

    [Fact]
    public void It_says_which_rules_caught_the_window()
    {
        _fixture.Open(1, process: "alacritty");
        _fixture.Turn();

        JsonObject answer = Run("rules", "for", "--handle", "1");

        Assert.Contains("float-the-terminal", answer["matchedBy"]!.AsArray().Select(r => (string?)r));
    }

    [Fact]
    public void A_rule_that_catches_nothing_says_so()
    {
        _fixture.Open(1, process: "alacritty");
        _fixture.Turn();

        JsonArray rules = Run("rules", "list")["rules"]!.AsArray();

        JsonNode busy = rules.First(r => (string?)r!["id"] == "float-the-terminal")!;
        JsonNode idle = rules.First(r => (string?)r!["id"] == "for-an-app-that-is-not-running")!;

        Assert.False((bool)busy["idle"]!);
        Assert.Single(busy["catching"]!.AsArray());

        // Waiting for an app that is not running, or quietly misspelled. The
        // GUI should be able to tell the person which.
        Assert.True((bool)idle["idle"]!);
    }

    [Fact]
    public void Every_process_on_the_desk_is_offered_with_whether_a_rule_covers_it()
    {
        _fixture.Open(1, process: "alacritty");
        _fixture.Open(2, process: "zen");
        _fixture.Turn();

        JsonArray processes = Run("rules", "processes")["processes"]!.AsArray();

        JsonNode covered = processes.First(p => (string?)p!["process"] == "alacritty")!;
        JsonNode bare = processes.First(p => (string?)p!["process"] == "zen")!;

        Assert.True((bool)covered["hasRule"]!);
        Assert.False((bool)bare["hasRule"]!);
    }

    [Fact]
    public void A_window_the_person_changed_by_hand_says_so()
    {
        _fixture.Open(1, process: "zen");
        _fixture.Turn();
        _fixture.Desk.Focus(DeskFixture.W(1));

        Assert.False((bool)Run("rules", "for", "--focused")["decidedByHand"]!);

        _fixture.Desk.PersonDecided(DeskFixture.W(1));

        // A chord that floats a window outranks the configuration that would
        // have tiled it, and the GUI has to show that the rule is not what is
        // in charge of this one.
        Assert.True((bool)Run("rules", "for", "--focused")["decidedByHand"]!);
    }
}
