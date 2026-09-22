using AkuWM.Core.Config;
using AkuWM.Core.Model;
using AkuWM.Core.State;
using Xunit;

namespace AkuWM.Tests;

/// <summary>
/// A rule with the anticheat action: the desk says "game mode" while any
/// window of that application exists, once on the way in and once on the
/// way out, managed or ignored.
/// </summary>
public class GameModeTests
{
    private static DeskFixture Fixture(params string[] actions)
    {
        AkuWmConfig config = DeskFixture.Configuration();
        config.General!.HotkeyHost = new HotkeyHostConfig { Process = "AutoHotkey64_UIA", Command = "hyper-desktops.lnk" };
        config.Rules = [new RuleConfig { Name = "aion2", Match = [new MatchCriteria { Process = "AION2" }], Actions = [.. actions] }];
        return new DeskFixture(config);
    }

    [Fact]
    public void Game_mode_follows_the_first_window_in_and_the_last_window_out()
    {
        var f = Fixture("anticheat");
        var changes = new List<bool>();
        f.Desk.GameModeChanged += changes.Add;
        Assert.False(f.Desk.GameMode);

        f.Open(1, process: "AION2");
        Assert.True(f.Desk.GameMode);
        Assert.Equal([true], changes);

        f.Open(2, process: "AION2");
        Assert.Equal([true], changes);

        f.Close(1);
        Assert.True(f.Desk.GameMode);
        Assert.Equal([true], changes);

        f.Close(2);
        Assert.False(f.Desk.GameMode);
        Assert.Equal([true, false], changes);
    }

    [Fact]
    public void An_ignored_game_still_counts()
    {
        var f = Fixture("ignore", "anticheat");
        f.Open(1, process: "AION2");
        Assert.False(f.Managed(1)!.Managed);
        Assert.True(f.Desk.GameMode);

        f.Close(1);
        Assert.False(f.Desk.GameMode);
    }

    [Fact]
    public void A_window_of_another_application_does_nothing()
    {
        var f = Fixture("anticheat");
        f.Open(1, process: "zen");
        Assert.False(f.Desk.GameMode);
    }

    [Fact]
    public void Anticheat_without_a_hotkey_host_is_a_warning()
    {
        AkuWmConfig config = DeskFixture.Configuration();
        config.Rules = [new RuleConfig { Name = "aion2", Match = [new MatchCriteria { Process = "AION2" }], Actions = ["anticheat"] }];
        List<ValidationIssue> issues = ConfigValidator.Validate(config).Issues.ToList();
        Assert.Contains(issues, i => i.Path == "rules[0].actions" && i.Message.Contains("hotkey_host"));

        config.General!.HotkeyHost = new HotkeyHostConfig { Process = "AutoHotkey64_UIA", Command = "x.lnk" };
        issues = ConfigValidator.Validate(config).Issues.ToList();
        Assert.DoesNotContain(issues, i => i.Message.Contains("hotkey_host"));
    }
}

public class ForgetAppTests
{
    [Fact]
    public void Forget_app_drops_every_record_of_the_process_and_saves()
    {
        string file = Path.Combine(Path.GetTempPath(), "akuwm-forget-" + Guid.NewGuid().ToString("N") + ".tsv");
        try
        {
            var memory = new AppMemory(file);
            memory.Remember("fliptest|FlipTestWnd", new AppRecord(true, 100, 100, 0, 0));
            memory.Remember("fliptest|Other", new AppRecord(false, 100, 100, 0, 0));
            memory.Remember("zen|MozillaWindowClass", new AppRecord(true, 100, 100, 0, 0));

            Assert.Equal(2, memory.ForgetProcess("fliptest"));
            Assert.Equal(0, memory.ForgetProcess("fliptest"));
            Assert.Null(memory.Recall("fliptest|FlipTestWnd"));
            Assert.NotNull(memory.Recall("zen|MozillaWindowClass"));

            var again = new AppMemory(file);
            Assert.Equal(1, again.Count);
        }
        finally
        {
            File.Delete(file);
        }
    }
}
