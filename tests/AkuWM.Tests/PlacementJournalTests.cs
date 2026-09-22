using AkuWM.Core.Config;
using AkuWM.Core.Desk;
using AkuWM.Core.Model;
using AkuWM.Core.State;
using Xunit;

namespace AkuWM.Tests;

/// <summary>
/// A restart of the daemon finds the desk as the person left it (2026-09-22).
/// </summary>
public class PlacementJournalTests
{
    private static WindowHandle W(long handle) => new(handle);

    private static DeskFixture Restarted(DeskFixture before, PlacementJournal journal, AkuWmConfig? config = null)
    {
        // A new daemon: the same windows on the desk, a new model.
        var after = new DeskFixture(config ?? DeskFixture.Configuration());
        after.Desk.RemembersPlacementsWith(journal);
        after.Platform.WindowList.AddRange(before.Platform.WindowList.Select(w => w with { Cloak = CloakKind.None }));
        after.Sync();
        after.Turn();
        return after;
    }

    [Fact]
    public void Windows_go_back_to_the_workspaces_they_were_on()
    {
        using var dir = new TempDir();
        var journal = new PlacementJournal(dir.File("placements.bin"));
        var before = new DeskFixture();
        before.Desk.RemembersPlacementsWith(journal);
        before.Open(1);
        before.Open(2);
        before.Open(3, monitor: new MonitorHandle(2));
        before.Turn();
        before.Desk.MoveToWorkspace(W(2), "13");
        before.Desk.MoveToWorkspace(W(3), "22");
        before.Turn();

        DeskFixture after = Restarted(before, journal);

        Assert.Equal("11", after.Managed(1)!.Workspace);
        Assert.Equal("13", after.Managed(2)!.Workspace);
        Assert.Equal("22", after.Managed(3)!.Workspace);
        Assert.True(after.IsHidden(2), "13 is not on screen, so its window is hidden again");
    }

    [Fact]
    public void A_floating_window_keeps_its_layer_and_its_rectangle()
    {
        using var dir = new TempDir();
        var journal = new PlacementJournal(dir.File("placements.bin"));
        var before = new DeskFixture();
        before.Desk.RemembersPlacementsWith(journal);
        before.Open(1);
        before.Open(2);
        before.Turn();
        before.Desk.SetFloating(W(2), true);
        before.Turn();
        before.Move(2, new Rect(700, 300, 640, 480));
        before.Turn();

        DeskFixture after = Restarted(before, journal);

        Assert.Equal(WindowState.Floating, after.Managed(2)!.State);
        Assert.Equal(new Rect(700, 300, 640, 480), after.FrameOf(2));
        Assert.Equal(WindowState.Tiling, after.Managed(1)!.State);
    }

    [Fact]
    public void A_sticky_window_follows_the_same_monitor_again()
    {
        using var dir = new TempDir();
        var journal = new PlacementJournal(dir.File("placements.bin"));
        var before = new DeskFixture();
        before.Desk.RemembersPlacementsWith(journal);
        before.Open(1, resizable: false);
        before.Turn();
        before.Desk.SetSticky(W(1), true);
        before.Turn();
        before.Move(1, new Rect(4000, 100, 600, 400));
        before.Turn();
        before.Turn();
        Assert.Equal("second", before.Managed(1)!.StickyMonitor);

        DeskFixture after = Restarted(before, journal);

        Assert.True(after.Managed(1)!.Sticky);
        Assert.Equal("second", after.Managed(1)!.StickyMonitor);
    }

    [Fact]
    public void A_rule_that_names_a_workspace_wins_over_the_memory()
    {
        using var dir = new TempDir();
        var journal = new PlacementJournal(dir.File("placements.bin"));
        var before = new DeskFixture();
        before.Desk.RemembersPlacementsWith(journal);
        before.Open(1, process: "telegram");
        before.Turn();
        before.Desk.MoveToWorkspace(W(1), "13");
        before.Turn();

        AkuWmConfig config = DeskFixture.Configuration();
        config.Rules =
        [
            new RuleConfig
            {
                Id = "r-tg",
                Name = "Telegram",
                Match = [new MatchCriteria { Process = "telegram" }],
                Target = new RuleTarget { Workspace = "12" },
            },
        ];

        DeskFixture after = Restarted(before, journal, config);

        Assert.Equal("12", after.Managed(1)!.Workspace);
    }

    [Fact]
    public void A_closed_window_is_forgotten_and_a_new_one_on_its_handle_is_placed_afresh()
    {
        using var dir = new TempDir();
        var journal = new PlacementJournal(dir.File("placements.bin"));
        var before = new DeskFixture();
        before.Desk.RemembersPlacementsWith(journal);
        before.Desk.Forgotten += journal.Forget;
        before.Open(1);
        before.Turn();
        before.Desk.MoveToWorkspace(W(1), "13");
        before.Turn();
        before.Close(1);

        Assert.Null(journal.Recall(W(1), "zen"));
        Assert.Equal(0, journal.Count);
    }

    [Fact]
    public void A_record_from_before_this_boot_is_dropped()
    {
        using var dir = new TempDir();
        string file = dir.File("placements.bin");
        var old = new PlacementJournal(file);
        old.Remember(W(1), "zen", new Placed("13", null, false, default));

        long tomorrow = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 86400;
        var rebooted = new PlacementJournal(file, droppedBefore: tomorrow);

        Assert.Null(rebooted.Recall(W(1), "zen"));
    }

    [Fact]
    public void Nothing_is_written_while_nothing_changes()
    {
        using var dir = new TempDir();
        var journal = new PlacementJournal(dir.File("placements.bin"));
        var fixture = new DeskFixture();
        fixture.Desk.RemembersPlacementsWith(journal);
        fixture.Open(1);
        fixture.Turn();

        Placed? first = journal.Recall(W(1), "zen");
        fixture.Turn();
        fixture.Turn();

        Assert.Equal(first, journal.Recall(W(1), "zen"));
        Assert.Equal(1, journal.Count);
    }
}
