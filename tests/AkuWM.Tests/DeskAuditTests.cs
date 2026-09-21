using AkuWM.Core.Config;
using AkuWM.Core.Desk;
using AkuWM.Core.Layout;
using AkuWM.Core.Model;
using Xunit;

namespace AkuWM.Tests;

/// <summary>
/// The combinations the first pass did not cover: every window state crossed
/// with every layer it can leave and arrive in, a monitor going away and
/// coming back, and the rule actions that were written but never exercised.
/// </summary>
/// <remarks>
/// A window manager is mostly transitions. The bugs are not in "a tiled window
/// tiles"; they are in "a fullscreen window was minimised, and the workspace
/// still thinks something is covering it".
/// </remarks>
public class DeskAuditTests
{
    private readonly DeskFixture _fixture = new();

    private Desk Desk => _fixture.Desk;

    private static WindowHandle W(long handle) => new(handle);

    // ---- minimising, from every state --------------------------------------

    [Fact]
    public void Minimising_a_fullscreen_window_leaves_nothing_covering_the_workspace()
    {
        _fixture.Open(1);
        _fixture.Open(2);
        _fixture.Turn();
        Desk.SetFullscreen(W(1), true);
        _fixture.Turn();

        _fixture.Platform.SetMinimized(W(1), true);
        _fixture.Sync();
        _fixture.Turn();

        Assert.Equal(WindowHandle.None, Desk.Workspace("11")!.Fullscreen);
        // And the other window gets the screen rather than sitting behind a
        // window that is not there any more.
        Assert.Equal(new Rect(0, 42, 3840, 2118), _fixture.FrameOf(2));
    }

    [Fact]
    public void A_minimised_fullscreen_window_comes_back_as_one_thing_not_two()
    {
        _fixture.Open(1);
        _fixture.Turn();
        Desk.SetFullscreen(W(1), true);
        _fixture.Turn();

        _fixture.Platform.SetMinimized(W(1), true);
        _fixture.Sync();
        _fixture.Platform.SetMinimized(W(1), false);
        _fixture.Sync();
        _fixture.Turn();

        Workspace workspace = Desk.Workspace("11")!;
        int places = (workspace.Tiling.Contains(W(1)) ? 1 : 0)
                     + (workspace.Floating.Contains(W(1)) ? 1 : 0)
                     + (workspace.Fullscreen == W(1) ? 1 : 0);

        Assert.Equal(1, places);
    }

    [Fact]
    public void Minimising_a_floating_window_keeps_it_floating()
    {
        _fixture.Open(1, resizable: false, frame: new Rect(500, 500, 400, 300));
        _fixture.Turn();

        _fixture.Platform.SetMinimized(W(1), true);
        _fixture.Sync();
        _fixture.Platform.SetMinimized(W(1), false);
        _fixture.Sync();
        _fixture.Turn();

        Assert.Equal(WindowState.Floating, _fixture.Managed(1)!.State);
        Assert.Equal(new Rect(500, 500, 400, 300), _fixture.FrameOf(1));
    }

    [Fact]
    public void A_minimised_sticky_window_is_still_sticky_when_it_comes_back()
    {
        _fixture.Open(1, resizable: false, frame: new Rect(3000, 1500, 600, 400));
        Desk.SetSticky(W(1), true);
        _fixture.Turn();

        _fixture.Platform.SetMinimized(W(1), true);
        _fixture.Sync();
        _fixture.Platform.SetMinimized(W(1), false);
        _fixture.Sync();
        _fixture.Turn();

        Assert.True(_fixture.Managed(1)!.Sticky);
        Assert.Contains(W(1), Desk.MonitorByRole("main")!.Sticky);
    }

    // ---- fullscreen, in every place ---------------------------------------

    [Fact]
    public void A_fullscreen_window_moved_out_of_sight_stops_holding_the_taskbar_down()
    {
        _fixture.Open(1);
        _fixture.Turn();
        Desk.SetFullscreen(W(1), true);
        Redraw first = _fixture.Turn();
        Assert.Contains((W(1), true), first.TaskbarMark);

        Desk.MoveToWorkspace(W(1), "13");
        Redraw second = _fixture.Turn();

        // Nobody can see it any more, so the taskbar has to be told -- or it
        // stays behind everything for ever, which looks exactly like a broken
        // shell.
        Assert.Contains((W(1), false), second.TaskbarMark);
    }

    [Fact]
    public void A_mark_the_shell_refused_is_asked_for_again_on_the_next_pass()
    {
        _fixture.Open(1);
        _fixture.Turn();
        Desk.SetFullscreen(W(1), true);

        Redraw first = Desk.Compute();
        Assert.Contains((W(1), true), first.TaskbarMark);

        // The shell said no -- explorer restarting is the ordinary way that
        // happens. The model must not remember a mark it never made, or the
        // taskbar sits over the game for the rest of the session.
        Desk.Applied(first, refused: null, unmarked: new HashSet<WindowHandle> { W(1) });

        Assert.Contains((W(1), true), Desk.Compute().TaskbarMark);
    }

    [Fact]
    public void A_sticky_window_goes_under_a_game_on_the_screen_it_follows()
    {
        _fixture.Open(1);
        _fixture.Turn();
        Desk.SetSticky(W(1), true);
        Assert.Contains((W(1), true), _fixture.Turn().Band);

        _fixture.Open(2);
        _fixture.Turn();
        Desk.SetFullscreen(W(2), true);

        // A visible always-on-top window over a fullscreen game costs it the
        // direct path to the screen: DWM composes the frame instead, measured
        // at 45 fps and +60 ms on Aion 2. The floating windows of a workspace
        // already left the band for a game; every sticky window on the desk
        // did not. Found by tests/fullscreen 2026-09-21.
        Assert.Contains((W(1), false), _fixture.Turn().Band);
    }

    [Fact]
    public void And_comes_back_up_when_the_game_is_gone()
    {
        _fixture.Open(1);
        _fixture.Turn();
        Desk.SetSticky(W(1), true);
        _fixture.Open(2);
        _fixture.Turn();
        Desk.SetFullscreen(W(2), true);
        _fixture.Turn();

        _fixture.Close(2);

        Assert.Contains((W(1), true), _fixture.Turn().Band);
    }

    [Fact]
    public void A_window_that_closes_while_fullscreen_releases_the_taskbar()
    {
        _fixture.Open(1);
        _fixture.Turn();
        Desk.SetFullscreen(W(1), true);
        _fixture.Turn();

        _fixture.Close(1);
        _fixture.Turn();

        Assert.Null(_fixture.Managed(1));
        Assert.Equal(WindowHandle.None, Desk.Workspace("11")!.Fullscreen);
    }

    [Fact]
    public void Floating_a_fullscreen_window_takes_it_out_of_fullscreen()
    {
        _fixture.Open(1);
        _fixture.Turn();
        Desk.SetFullscreen(W(1), true);
        _fixture.Turn();

        Assert.True(Desk.SetFloating(W(1), true));
        _fixture.Turn();

        Assert.Equal(WindowState.Floating, _fixture.Managed(1)!.State);
        Assert.Equal(WindowHandle.None, Desk.Workspace("11")!.Fullscreen);
    }

    [Fact]
    public void A_second_window_going_fullscreen_replaces_the_first()
    {
        _fixture.Open(1);
        _fixture.Open(2);
        _fixture.Turn();

        Desk.SetFullscreen(W(1), true);
        _fixture.Turn();
        Desk.SetFullscreen(W(2), true);
        _fixture.Turn();

        Workspace workspace = Desk.Workspace("11")!;
        Assert.Equal(W(2), workspace.Fullscreen);
        // The first one is back in the layout rather than lost between states.
        Assert.Equal(WindowState.Tiling, _fixture.Managed(1)!.State);
        Assert.True(workspace.Tiling.Contains(W(1)));
    }

    // ---- sticky, both ways -------------------------------------------------

    [Fact]
    public void Making_a_tiled_window_sticky_gives_its_space_to_the_others()
    {
        _fixture.Open(1);
        _fixture.Open(2);
        _fixture.Turn();

        Desk.SetSticky(W(2), true);
        _fixture.Turn();

        Assert.Equal(new Rect(0, 42, 3840, 2118), _fixture.FrameOf(1));
        Assert.False(Desk.Workspace("11")!.Tiling.Contains(W(2)));
    }

    [Fact]
    public void Unsticking_a_window_puts_it_on_the_workspace_in_front_of_you()
    {
        _fixture.Open(1);
        Desk.SetSticky(W(1), true);
        _fixture.Turn();
        Desk.FocusWorkspace("12");
        _fixture.Turn();

        Assert.True(Desk.SetSticky(W(1), false));
        _fixture.Turn();

        Assert.Equal("12", _fixture.Managed(1)!.Workspace);
        Assert.Empty(Desk.MonitorByRole("main")!.Sticky);
        Assert.False(_fixture.IsHidden(1));
    }

    [Fact]
    public void Moving_a_sticky_window_to_a_workspace_stops_it_being_sticky()
    {
        _fixture.Open(1);
        Desk.SetSticky(W(1), true);
        _fixture.Turn();

        Assert.True(Desk.MoveToWorkspace(W(1), "13"));
        _fixture.Turn();

        Assert.False(_fixture.Managed(1)!.Sticky);
        Assert.Equal("13", _fixture.Managed(1)!.Workspace);
        Assert.True(_fixture.IsHidden(1));
    }

    [Fact]
    public void A_sticky_window_asked_to_go_fullscreen_joins_the_workspace_and_does()
    {
        _fixture.Open(1, resizable: false, frame: new Rect(3000, 1500, 600, 400));
        Desk.SetSticky(W(1), true);
        _fixture.Turn();

        Assert.True(Desk.SetFullscreen(W(1), true));
        _fixture.Turn();

        Assert.False(_fixture.Managed(1)!.Sticky);
        Assert.Equal("11", _fixture.Managed(1)!.Workspace);
        Assert.Equal(new Rect(0, 0, 3840, 2160), _fixture.FrameOf(1));
    }

    [Fact]
    public void A_sticky_window_asked_to_tile_joins_the_layout()
    {
        _fixture.Open(1);
        _fixture.Open(2, resizable: false, frame: new Rect(3000, 1500, 600, 400));
        Desk.SetSticky(W(2), true);
        _fixture.Turn();

        Assert.True(Desk.SetFloating(W(2), false));
        _fixture.Turn();

        Assert.False(_fixture.Managed(2)!.Sticky);
        Assert.Equal(WindowState.Tiling, _fixture.Managed(2)!.State);
        Assert.True(Desk.Workspace("11")!.Tiling.Contains(W(2)));
    }

    [Fact]
    public void A_sticky_window_asked_to_float_is_already_floating()
    {
        _fixture.Open(1, resizable: false, frame: new Rect(3000, 1500, 600, 400));
        Desk.SetSticky(W(1), true);

        Assert.False(Desk.SetFloating(W(1), true));
        Assert.True(_fixture.Managed(1)!.Sticky);
    }

    [Fact]
    public void A_hidden_window_that_misses_one_pass_is_not_abandoned_cloaked()
    {
        _fixture.Open(1);
        _fixture.Turn();
        Desk.FocusWorkspace("12");
        _fixture.Turn();
        Assert.True(_fixture.IsHidden(1));

        // One enumeration that does not list it -- an Electron app whose title
        // goes empty for a moment, a splash turning into a main window, a game
        // loading -- all of which fail the candidate filter briefly.
        Desk.Sync([]);
        _fixture.Sync();

        // Forgetting it means re-adopting it as CloakedElsewhere: unmanaged,
        // invisible, off the taskbar, out of Alt+Tab, and nothing left that
        // will ever take the cloak off.
        Assert.NotNull(_fixture.Managed(1));
        Assert.True(_fixture.Managed(1)!.Managed);

        Desk.FocusWorkspace("11");
        _fixture.Turn();
        Assert.False(_fixture.IsHidden(1));
    }

    [Fact]
    public void A_hidden_window_whose_handle_really_is_gone_is_forgotten()
    {
        _fixture.Open(1);
        _fixture.Turn();
        Desk.FocusWorkspace("12");
        _fixture.Turn();

        _fixture.Platform.WindowList.Clear();
        _fixture.Sync();

        Assert.Null(_fixture.Managed(1));
    }

    [Fact]
    public void After_a_workspace_switch_the_model_knows_where_the_focus_went()
    {
        _fixture.Open(1);
        Desk.FocusWorkspace("12");
        _fixture.Open(2);
        _fixture.Turn();
        Desk.Focus(W(2));

        Desk.WantFocus(Desk.FocusWorkspace("11"));
        Redraw redraw = _fixture.Turn();

        // The focus was recorded before the un-hide, so the model refused it
        // and kept pointing at the window on the workspace just hidden -- and
        // the next chord with no --id acted on a window nobody could see.
        Assert.Equal(W(1), redraw.Focus);
        Assert.Equal(W(1), Desk.Focused);
    }

    [Fact]
    public void A_raised_hidden_window_hands_the_keyboard_back_to_a_visible_one()
    {
        _fixture.Open(1);
        _fixture.Turn();
        Desk.FocusWorkspace("12");
        _fixture.Open(2);
        _fixture.Turn();
        Desk.Focus(W(2));

        // Something raised the cloaked window: a taskbar click, a toast, an app
        // calling SetForegroundWindow on itself.
        Assert.False(Desk.Focus(W(1)));
        Redraw redraw = _fixture.Turn();

        Assert.Equal(W(2), redraw.Focus);
    }

    [Fact]
    public void A_window_dragged_away_long_after_it_settled_still_goes_back()
    {
        _fixture.Open(1);
        _fixture.Open(2);
        _fixture.Turn();
        Rect was = _fixture.FrameOf(2);

        // A minute of the window sitting where it was put.
        _fixture.Wait(60_000);
        _fixture.Sync();
        Assert.True(Desk.Compute().IsNothing);

        _fixture.Platform.Place([new Placement(W(2), new Rect(300, 900, 800, 600))]);
        _fixture.Sync();
        _fixture.Turn();

        // The patience clock only ever started; it never stopped when the
        // window complied, so the first drag after that was read as a refusal.
        Assert.False(_fixture.Managed(2)!.PlacementRefused);
        Assert.Equal(was, _fixture.FrameOf(2));
    }

    [Fact]
    public void A_window_is_never_sticky_to_two_monitors_at_once()
    {
        _fixture.Open(1, resizable: false, frame: new Rect(500, 500, 400, 300));
        Desk.SetSticky(W(1), true);
        _fixture.Turn();

        // Minimised and restored after moving to the other screen: the restore
        // used to add it to the new monitor's set without taking it out of the
        // old one, and which screen drew it was then decided by enumeration
        // order.
        _fixture.Platform.SetMinimized(W(1), true);
        _fixture.Sync();
        _fixture.Platform.WindowList[0] = _fixture.Platform.WindowList[0] with
        {
            Monitor = new MonitorHandle(2),
            IsMinimized = false,
        };
        _fixture.Sync();
        _fixture.Turn();

        int sets = Desk.Monitors.Count(m => m.Sticky.Contains(W(1)));
        Assert.Equal(1, sets);
        Assert.Equal(Desk.Monitors.First(m => m.Sticky.Contains(W(1))).Role, _fixture.Managed(1)!.StickyMonitor);
    }

    [Fact]
    public void Unsticking_always_lands_the_window_on_a_workspace()
    {
        _fixture.Open(1, monitor: new MonitorHandle(2));
        Desk.SetSticky(W(1), true);
        _fixture.Turn();

        // Its monitor goes away while it is stuck to it. Unsticking then found
        // no displayed workspace and left the window belonging to nothing:
        // managed, in no layer, never placed, hidden or shown again.
        _fixture.Platform.MonitorList.RemoveAll(m => m.HardwareId == "NSL2711");
        Desk.SetMonitors(_fixture.Platform.Monitors());

        Assert.True(Desk.SetSticky(W(1), false));

        Assert.NotNull(_fixture.Managed(1)!.Workspace);
        Assert.False(_fixture.Managed(1)!.Sticky);
    }

    // ---- monitors coming and going ----------------------------------------

    [Fact]
    public void A_window_is_never_left_hidden_on_a_monitor_that_is_gone()
    {
        _fixture.Open(1, monitor: new MonitorHandle(2));
        _fixture.Turn();
        Desk.FocusWorkspace("22");
        _fixture.Turn();
        Assert.True(_fixture.IsHidden(1));

        // The second monitor is unplugged. Its workspaces have nowhere to be
        // drawn, and a window AkuWM cannot account for must not stay invisible
        // because of AkuWM.
        _fixture.Platform.MonitorList.RemoveAll(m => m.HardwareId == "NSL2711");
        Desk.SetMonitors(_fixture.Platform.Monitors());
        _fixture.Turn();

        Assert.False(_fixture.IsHidden(1));
    }

    [Fact]
    public void A_monitor_that_comes_back_still_has_its_workspaces()
    {
        _fixture.Open(1, monitor: new MonitorHandle(2));
        Desk.FocusWorkspace("22");
        _fixture.Open(2, monitor: new MonitorHandle(2));
        _fixture.Turn();
        Assert.Equal("22", _fixture.Managed(2)!.Workspace);

        _fixture.Platform.MonitorList.RemoveAll(m => m.HardwareId == "NSL2711");
        Desk.SetMonitors(_fixture.Platform.Monitors());
        _fixture.Turn();

        _fixture.Platform.MonitorList.Add(FakePlatform.SecondMonitor());
        Desk.SetMonitors(_fixture.Platform.Monitors());
        _fixture.Turn();

        // The workspaces belong to the desk, not to a screen: the one that
        // was on it is the one that comes back, not the first in the list.
        Assert.Equal("22", _fixture.Managed(2)!.Workspace);
        Assert.Equal("22", Desk.MonitorByRole("second")!.Displayed!.Name);
    }

    [Fact]
    public void A_floating_window_left_off_screen_is_brought_back_onto_it()
    {
        _fixture.Open(1, resizable: false, frame: new Rect(4200, 900, 600, 400), monitor: new MonitorHandle(2));
        _fixture.Turn();

        // The screen it was on is gone; the rectangle it remembers is nowhere.
        _fixture.Platform.MonitorList.RemoveAll(m => m.HardwareId == "NSL2711");
        Desk.SetMonitors(_fixture.Platform.Monitors());
        Desk.MoveToWorkspace(W(1), "11");
        _fixture.Turn();

        Rect where = _fixture.FrameOf(1);
        Assert.True(
            where.FractionInside(new Rect(0, 42, 3840, 2118)) > 0.9,
            $"it is at {where}, which is not on the remaining screen");
    }

    [Fact]
    public void There_is_no_monitor_above_or_below_two_side_by_side()
    {
        DeskMonitor main = Desk.MonitorByRole("main")!;

        Assert.Equal("second", Desk.MonitorInDirection(main, Direction.Right)?.Role);
        Assert.Null(Desk.MonitorInDirection(main, Direction.Left));
        // They overlap vertically, so neither is above the other.
        Assert.Null(Desk.MonitorInDirection(main, Direction.Up));
        Assert.Null(Desk.MonitorInDirection(main, Direction.Down));
    }

    [Fact]
    public void With_one_screen_there_is_nowhere_else_to_go()
    {
        _fixture.Platform.MonitorList.RemoveAll(m => m.HardwareId == "NSL2711");
        Desk.SetMonitors(_fixture.Platform.Monitors());
        _fixture.Open(1);
        _fixture.Turn();
        Desk.Focus(W(1));

        Assert.Equal(WindowHandle.None, Desk.InDirection(Direction.Right));
        Assert.False(Desk.MoveFocused(Direction.Right));
    }

    // ---- rules that were written but never exercised -----------------------

    [Fact]
    public void A_rule_can_open_a_window_on_a_monitor_and_a_slot()
    {
        AkuWmConfig config = DeskFixture.Configuration();
        config.Rules =
        [
            new RuleConfig
            {
                Name = "chat on the second screen",
                Match = [new MatchCriteria { Process = "Telegram" }],
                Target = new RuleTarget { Monitor = "second", Slot = 2 },
            },
        ];

        var fixture = new DeskFixture(config);
        fixture.Open(1, process: "Telegram");
        fixture.Turn();

        // Slot 2 of the second monitor's workspaces, by role -- never by index.
        Assert.Equal("22", fixture.Managed(1)!.Workspace);
    }

    [Fact]
    public void A_rule_can_make_a_window_sticky_from_the_start()
    {
        AkuWmConfig config = DeskFixture.Configuration();
        config.Rules =
        [
            new RuleConfig
            {
                Name = "the chat window follows me",
                Match = [new MatchCriteria { Process = "Telegram" }],
                Actions = ["sticky"],
            },
        ];

        var fixture = new DeskFixture(config);
        fixture.Open(1, process: "Telegram");
        fixture.Turn();

        Assert.True(fixture.Managed(1)!.Sticky);
        Assert.Equal(WindowState.Floating, fixture.Managed(1)!.State);
    }

    [Fact]
    public void A_rule_can_open_a_window_fullscreen()
    {
        AkuWmConfig config = DeskFixture.Configuration();
        config.Rules =
        [
            new RuleConfig
            {
                Name = "the game owns the screen",
                Match = [new MatchCriteria { Process = "aion" }],
                Actions = ["fullscreen"],
            },
        ];

        var fixture = new DeskFixture(config);
        fixture.Open(1, process: "aion");
        fixture.Turn();

        Assert.Equal(WindowState.Fullscreen, fixture.Managed(1)!.State);
        Assert.Equal(new Rect(0, 0, 3840, 2160), fixture.FrameOf(1));
    }

    [Fact]
    public void Ignore_beats_every_other_rule_that_fires()
    {
        AkuWmConfig config = DeskFixture.Configuration();
        config.Rules =
        [
            new RuleConfig
            {
                Name = "float anything of this app",
                Match = [new MatchCriteria { Process = "zebar" }],
                Actions = ["float"],
            },
            new RuleConfig
            {
                Name = "except do not manage it at all",
                Match = [new MatchCriteria { Process = "zebar" }],
                Actions = ["ignore"],
            },
        ];

        var fixture = new DeskFixture(config);
        fixture.Open(1, process: "zebar", frame: new Rect(0, 0, 3840, 40));
        fixture.Turn();

        Assert.False(fixture.Managed(1)!.Managed);
        Assert.Equal(new Rect(0, 0, 3840, 40), fixture.FrameOf(1));
    }

    [Fact]
    public void A_disabled_rule_does_nothing()
    {
        AkuWmConfig config = DeskFixture.Configuration();
        config.Rules =
        [
            new RuleConfig
            {
                Name = "switched off",
                Match = [new MatchCriteria { Process = "zen" }],
                Actions = ["ignore"],
                Enabled = false,
            },
        ];

        var fixture = new DeskFixture(config);
        fixture.Open(1, process: "zen");
        fixture.Turn();

        Assert.True(fixture.Managed(1)!.Managed);
    }

    // ---- gaps and direction ------------------------------------------------

    [Fact]
    public void Gaps_can_be_taken_literally_instead_of_scaled()
    {
        AkuWmConfig config = DeskFixture.Configuration();
        config.Gaps = new GapsConfig { Inner = 8, Outer = [0, 0, 0, 0], ScaleWithDpi = false };

        var fixture = new DeskFixture(config);
        fixture.Open(1);
        fixture.Open(2);
        fixture.Turn();

        Assert.Equal(fixture.FrameOf(1).Right + 8, fixture.FrameOf(2).Left);
    }

    [Fact]
    public void An_outer_gap_list_that_is_too_short_does_not_break_the_desk()
    {
        AkuWmConfig config = DeskFixture.Configuration();
        config.Gaps = new GapsConfig { Inner = 0, Outer = [10], ScaleWithDpi = false };

        var fixture = new DeskFixture(config);
        fixture.Open(1);
        fixture.Turn();

        // Ten at the top, nothing anywhere else, and no exception.
        Assert.Equal(52, fixture.FrameOf(1).Top);
        Assert.Equal(3840, fixture.FrameOf(1).Width);
    }

    [Fact]
    public void A_configured_direction_overrides_the_shape_of_the_screen()
    {
        AkuWmConfig config = DeskFixture.Configuration();
        config.Layout = new LayoutConfig { DefaultDirection = "vertical" };

        var fixture = new DeskFixture(config);
        fixture.Open(1);
        fixture.Open(2);
        fixture.Turn();

        // The wide monitor would split side by side left to itself.
        Assert.Equal(fixture.FrameOf(1).Left, fixture.FrameOf(2).Left);
        Assert.True(fixture.FrameOf(2).Top > fixture.FrameOf(1).Top);
    }

    // ---- the rest of the model's surface -----------------------------------

    [Fact]
    public void One_window_can_be_read_without_reading_the_whole_desk()
    {
        _fixture.Open(1);
        _fixture.Turn();

        WindowSnapshot moved = _fixture.Platform.Window(W(1))! with { Title = "a different title" };
        DeskWindow observed = Desk.Observe(moved);

        Assert.Equal("a different title", observed.Snapshot.Title);
        Assert.Equal("11", observed.Workspace);
    }

    [Fact]
    public void A_window_seen_for_the_first_time_by_itself_is_adopted()
    {
        _fixture.Open(1);
        _fixture.Turn();

        WindowSnapshot fresh = FakePlatform.Window(9, "code", frame: new Rect(10, 10, 400, 400));
        _fixture.Platform.WindowList.Add(fresh);
        DeskWindow adopted = Desk.Observe(fresh);

        Assert.True(adopted.Managed);
        Assert.Equal("11", adopted.Workspace);
    }

    [Fact]
    public void Raising_a_floating_window_puts_it_at_the_front()
    {
        _fixture.Open(1, resizable: false, frame: new Rect(100, 100, 400, 300));
        _fixture.Open(2, resizable: false, frame: new Rect(200, 200, 400, 300));
        _fixture.Turn();

        Assert.True(Desk.Raise(W(1)));

        Assert.Equal(W(1), Desk.Workspace("11")!.Floating.Last());
    }

    [Fact]
    public void A_floating_window_remembers_where_it_was_dragged()
    {
        _fixture.Open(1, resizable: false, frame: new Rect(100, 100, 400, 300));
        _fixture.Turn();

        Assert.True(Desk.SetFloatingRect(W(1), new Rect(900, 700, 500, 400)));
        Desk.FocusWorkspace("12");
        _fixture.Turn();
        Desk.FocusWorkspace("11");
        _fixture.Turn();

        Assert.Equal(new Rect(900, 700, 500, 400), _fixture.FrameOf(1));
    }

    [Fact]
    public void A_tiled_window_does_not_pretend_to_remember_a_floating_rectangle()
    {
        _fixture.Open(1);
        _fixture.Turn();

        Assert.False(Desk.SetFloatingRect(W(1), new Rect(900, 700, 500, 400)));
    }

    [Fact]
    public void Resizing_a_floating_window_changes_its_own_rectangle()
    {
        _fixture.Open(1, resizable: false, frame: new Rect(100, 100, 400, 300));
        _fixture.Turn();

        Assert.True(Desk.Resize(W(1), Direction.Right, 5));
        _fixture.Turn();

        Assert.True(_fixture.FrameOf(1).Width > 400, $"it is {_fixture.FrameOf(1).Width} wide");
        Assert.Equal(300, _fixture.FrameOf(1).Height);
    }

    [Fact]
    public void An_unmanaged_window_answers_no_to_every_command()
    {
        AkuWmConfig config = DeskFixture.Configuration();
        config.Rules =
        [
            new RuleConfig
            {
                Name = "not ours",
                Match = [new MatchCriteria { Process = "zebar" }],
                Actions = ["ignore"],
            },
        ];

        var fixture = new DeskFixture(config);
        fixture.Open(1, process: "zebar");

        Assert.False(fixture.Desk.SetFloating(W(1), true));
        Assert.False(fixture.Desk.SetFullscreen(W(1), true));
        Assert.False(fixture.Desk.SetSticky(W(1), true));
        Assert.False(fixture.Desk.MoveToWorkspace(W(1), "12"));
        Assert.False(fixture.Desk.Resize(W(1), Direction.Right, 5));
        Assert.False(fixture.Desk.ToggleDirection(W(1)));
        Assert.False(fixture.Desk.Raise(W(1)));
    }

    [Fact]
    public void A_window_that_closes_is_announced_so_its_records_can_be_dropped()
    {
        var forgotten = new List<WindowHandle>();
        Desk.Forgotten += forgotten.Add;

        _fixture.Open(1);
        _fixture.Open(2);
        _fixture.Turn();

        _fixture.Close(2);

        // The journal of where windows were is written to disk. Without this
        // it collects every window opened and closed all day.
        Assert.Equal([W(2)], forgotten);
    }

    [Fact]
    public void A_workspace_that_does_not_exist_is_refused_rather_than_invented()
    {
        _fixture.Open(1);

        Assert.Equal(WindowHandle.None, Desk.FocusWorkspace("99"));
        Assert.False(Desk.MoveToWorkspace(W(1), "99"));
        Assert.Equal("11", _fixture.Managed(1)!.Workspace);
    }
}
