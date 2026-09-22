using System.Text.Json.Nodes;
using AkuWM.Core.Compat;
using AkuWM.Core.Model;
using AkuWM.Core.Platform;
using Xunit;

namespace AkuWM.Tests;

/// <summary>
/// What the bar is told, and -- the part that matters -- what it is not told
/// twice. Captured 2026-09-21: Zebar subscribes with `sub --events all` and
/// answers EVERY event by asking for the monitors and the windows again, so an
/// event it did not need costs two full serialisations of the desk on the wm
/// thread, per connected widget.
/// </summary>
public class GlazeEventsTests
{
    private readonly DeskFixture _fixture = new();
    private readonly GlazeEvents _events = new();
    private readonly List<(string Type, JsonObject Payload)> _fired = [];

    private void Publish(bool listening = true) =>
        _events.Since(_fixture.Desk, listening, (type, payload) => _fired.Add((type, payload)));

    private string[] Types => [.. _fired.Select(f => f.Type)];

    [Fact]
    public void A_window_that_starts_being_managed_is_announced_once()
    {
        _fixture.Open(1);
        _fixture.Turn();
        Publish();

        // One activation per monitor on the very first pass -- both displayed
        // workspaces are new when there is nothing to compare them with -- and
        // the window once.
        Assert.Equal(
            ["workspace_activated", "workspace_activated", "window_managed"], Types);

        _fired.Clear();
        Publish();
        Assert.Empty(_fired);
    }

    [Fact]
    public void A_workspace_switch_says_which_went_away_and_which_came()
    {
        _fixture.Open(1);
        _fixture.Turn();
        Publish();
        _fired.Clear();

        _fixture.Desk.FocusWorkspace("12");
        _fixture.Turn();
        Publish();

        Assert.Contains("workspace_deactivated", Types);
        Assert.Contains("workspace_activated", Types);
        Assert.Equal(
            "11",
            (string?)_fired.First(f => f.Type == "workspace_deactivated")
                .Payload["deactivatedWorkspace"]!["name"]);
        Assert.Equal(
            "12",
            (string?)_fired.First(f => f.Type == "workspace_activated")
                .Payload["activatedWorkspace"]!["name"]);
    }

    [Fact]
    public void A_window_that_closes_is_announced_by_handle()
    {
        _fixture.Open(1);
        _fixture.Turn();
        Publish();
        _fired.Clear();

        _fixture.Close(1);
        _fixture.Turn();
        Publish();

        Assert.Contains("window_unmanaged", Types);
        Assert.Equal(
            DeskFixture.W(1).Value,
            (long)_fired.First(f => f.Type == "window_unmanaged").Payload["unmanagedHandle"]!);
    }

    [Fact]
    public void The_focus_moving_is_an_event_of_its_own()
    {
        _fixture.Open(1);
        _fixture.Open(2);
        _fixture.Turn();
        Publish();
        _fired.Clear();

        _fixture.Desk.Focus(DeskFixture.W(2));
        _fixture.Turn();
        Publish();

        Assert.Contains("focus_changed", Types);
    }

    [Fact]
    public void Nothing_is_replayed_to_a_bar_that_connects_after_the_fact()
    {
        // The fault this class was pulled out of App to guard. While nobody is
        // connected the desk still has to be followed; skipping the pass
        // altogether meant the first change after a bar connected reported
        // every window as newly managed and every workspace as newly
        // activated, and the bar asks for the whole desk again per event.
        _fixture.Open(1);
        _fixture.Open(2);
        _fixture.Open(3);
        _fixture.Turn();

        Publish(listening: false);
        Assert.Empty(_fired);

        // A bar connects here, and asks for the full state itself -- which is
        // what the capture shows it doing: focused, binding-modes,
        // tiling-direction, paused, monitors, windows, then `sub --events all`.
        _fixture.Desk.Focus(DeskFixture.W(2));
        _fixture.Turn();
        Publish(listening: true);

        Assert.Equal(["focus_changed"], Types);
    }

    [Fact]
    public void A_bar_that_goes_away_and_comes_back_is_not_told_the_desk_is_new()
    {
        _fixture.Open(1);
        _fixture.Turn();
        Publish();
        _fired.Clear();

        // Zebar restarts -- which happens on every GlazeWM config reload, and
        // will happen on every AkuWM restart too.
        _fixture.Open(2);
        _fixture.Turn();
        Publish(listening: false);

        _fixture.Turn();
        Publish(listening: true);

        Assert.Empty(_fired);
    }

    [Fact]
    public void The_monitors_of_the_first_pass_are_not_announced_as_new()
    {
        _fixture.Open(1);
        _fixture.Turn();
        Publish();

        // They were always there. Saying "added" would cost the bar a full
        // re-read of the desk per monitor, every time AkuWM starts.
        Assert.DoesNotContain("monitor_added", Types);
    }

    [Fact]
    public void A_monitor_that_naps_and_comes_back_is_announced_both_ways()
    {
        _fixture.Open(1);
        _fixture.Turn();
        Publish();
        _fired.Clear();

        // The Samsung goes to sleep: Windows says there is one screen.
        _fixture.Platform.MonitorList.Clear();
        _fixture.Platform.MonitorList.Add(FakePlatform.MainMonitor());
        _fixture.Screens();
        Publish();

        Assert.Contains("monitor_removed", Types);
        Assert.Equal("second", (string?)_fired.First(f => f.Type == "monitor_removed").Payload["removedId"]);

        _fired.Clear();
        _fixture.Platform.MonitorList.Add(FakePlatform.SecondMonitor());
        _fixture.Screens();
        Publish();

        // Without this the bar goes on drawing pills for a screen that is not
        // there, and then does not draw them for one that is: it refreshes
        // only when an event arrives.
        Assert.Contains("monitor_added", Types);
    }

    [Fact]
    public void A_monitor_that_changes_resolution_is_announced_as_changed()
    {
        _fixture.Open(1);
        _fixture.Turn();
        Publish();
        _fired.Clear();

        MonitorSnapshot smaller = FakePlatform.SecondMonitor() with
        {
            Bounds = new Rect(3840, -408, 1080, 1920),
            WorkArea = new Rect(3840, -373, 1080, 1885),
        };

        _fixture.Platform.MonitorList.Clear();
        _fixture.Platform.MonitorList.Add(FakePlatform.MainMonitor());
        _fixture.Platform.MonitorList.Add(smaller);
        _fixture.Screens();
        Publish();

        Assert.Contains("monitor_updated", Types);
    }

    [Fact]
    public void Nothing_is_said_about_monitors_that_did_not_move()
    {
        _fixture.Open(1);
        _fixture.Turn();
        Publish();
        _fired.Clear();

        _fixture.Screens();
        Publish();

        Assert.Empty(_fired);
    }

    [Fact]
    public void Pausing_and_unpausing_are_told_to_the_bar()
    {
        _fixture.Open(1);
        _fixture.Turn();
        Publish();
        _fired.Clear();

        _fixture.Desk.Paused = true;
        Publish();
        Assert.Contains("pause_changed", Types);
        Assert.True((bool)_fired.First(f => f.Type == "pause_changed").Payload["isPaused"]!);

        _fired.Clear();
        _fixture.Desk.Paused = false;
        Publish();
        Assert.False((bool)_fired.First(f => f.Type == "pause_changed").Payload["isPaused"]!);
    }

    [Fact]
    public void Workspaces_on_two_monitors_are_followed_apart()
    {
        _fixture.Open(1);
        _fixture.Turn();
        Publish();
        _fired.Clear();

        _fixture.Desk.FocusWorkspace("22");
        _fixture.Turn();
        Publish();

        // The second monitor changed; the first did not, and must not be
        // re-announced because a shared key lost track of which was which.
        Assert.Equal(
            "22",
            (string?)_fired.First(f => f.Type == "workspace_activated")
                .Payload["activatedWorkspace"]!["name"]);
        Assert.Single(_fired, f => f.Type == "workspace_activated");
    }
}
