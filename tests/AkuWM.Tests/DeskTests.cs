using AkuWM.Core.Config;
using AkuWM.Core.Desk;
using AkuWM.Core.Layout;
using AkuWM.Core.Model;
using Xunit;

namespace AkuWM.Tests;

/// <summary>
/// The window manager itself, on a desk made of the numbers this machine
/// actually reports: a 4K screen at 150 % and a portrait one at 125 %.
/// </summary>
public class DeskTests
{
    private readonly DeskFixture _fixture = new();

    private Desk Desk => _fixture.Desk;

    // ---- tiling -----------------------------------------------------------

    [Fact]
    public void One_window_fills_the_work_area_not_the_screen()
    {
        _fixture.Open(1);
        _fixture.Turn();

        // The taskbar keeps its strip: 42 pixels at the top of the work area.
        Assert.Equal(new Rect(0, 42, 3840, 2118), _fixture.FrameOf(1));
    }

    [Fact]
    public void Two_windows_share_the_wide_monitor_side_by_side()
    {
        _fixture.Open(1);
        _fixture.Open(2);
        _fixture.Turn();

        Rect left = _fixture.FrameOf(1);
        Rect right = _fixture.FrameOf(2);

        Assert.Equal(left.Top, right.Top);
        // The 8 px written in the configuration is 12 px at 150 %, so the air
        // between two windows looks the same on both screens.
        Assert.Equal(left.Right + 12, right.Left);
        Assert.Equal(3840, left.Width + right.Width + 12);
    }

    [Fact]
    public void The_portrait_monitor_stacks()
    {
        // The person goes to the portrait monitor first, then opens two
        // windows: that is the order it happens in, and since 2026-09-21 it is
        // also what decides which screen they are born on.
        Desk.FocusWorkspace("21");
        _fixture.Open(1, monitor: new MonitorHandle(2));
        _fixture.Open(2, monitor: new MonitorHandle(2));
        _fixture.Turn();

        Rect top = _fixture.FrameOf(1);
        Rect bottom = _fixture.FrameOf(2);

        Assert.Equal(top.Left, bottom.Left);
        Assert.True(bottom.Top > top.Bottom, "the second window should be below the first");
    }

    [Fact]
    public void Closing_a_window_gives_its_space_back()
    {
        _fixture.Open(1);
        _fixture.Open(2);
        _fixture.Turn();

        _fixture.Close(2);
        _fixture.Turn();

        Assert.Equal(new Rect(0, 42, 3840, 2118), _fixture.FrameOf(1));
    }

    [Fact]
    public void Nothing_is_asked_for_twice()
    {
        _fixture.Open(1);
        _fixture.Open(2);
        _fixture.Turn();

        // The desk already looks like the model; a second turn of the crank
        // must touch nothing at all, or the screen would flicker for ever.
        Assert.True(Desk.Compute().IsNothing, Desk.Compute().ToString());
    }

    // ---- workspaces -------------------------------------------------------

    [Fact]
    public void A_window_opens_on_the_workspace_that_is_showing()
    {
        _fixture.Open(1);

        Assert.Equal("11", _fixture.Managed(1)!.Workspace);
    }

    [Fact]
    public void Switching_workspace_hides_one_set_and_shows_the_other()
    {
        _fixture.Open(1);
        _fixture.Turn();

        Desk.FocusWorkspace("12");
        _fixture.Open(2);
        _fixture.Turn();

        Assert.True(_fixture.IsHidden(1), "the outgoing window should be cloaked");
        Assert.False(_fixture.IsHidden(2));
        Assert.Equal(new Rect(0, 42, 3840, 2118), _fixture.FrameOf(2));
    }

    [Fact]
    public void Coming_back_to_a_workspace_shows_it_again()
    {
        _fixture.Open(1);
        _fixture.Turn();
        Desk.FocusWorkspace("12");
        _fixture.Turn();
        Assert.True(_fixture.IsHidden(1));

        Desk.FocusWorkspace("11");
        _fixture.Turn();

        Assert.False(_fixture.IsHidden(1));
        Assert.Equal(new Rect(0, 42, 3840, 2118), _fixture.FrameOf(1));
    }

    [Fact]
    public void A_workspace_asked_for_twice_goes_back_to_the_one_before()
    {
        _fixture.Open(1);
        Desk.FocusWorkspace("12");
        _fixture.Open(2);
        _fixture.Turn();

        // sway's workspace_auto_back_and_forth: the same key again returns.
        Desk.FocusWorkspace("12");

        Assert.Equal("11", Desk.MonitorByRole("main")!.Displayed!.Name);
    }

    [Fact]
    public void The_focus_lands_on_the_window_that_had_it_last()
    {
        _fixture.Open(1);
        _fixture.Open(2);
        Desk.Focus(DeskFixture.W(2));
        Desk.FocusWorkspace("12");
        _fixture.Turn();

        Assert.Equal(DeskFixture.W(2), Desk.FocusWorkspace("11"));
    }

    [Fact]
    public void Each_monitor_shows_its_own_workspace()
    {
        _fixture.Open(1);
        _fixture.Open(2, monitor: new MonitorHandle(2));
        _fixture.Turn();

        Assert.Equal("11", Desk.MonitorByRole("main")!.Displayed!.Name);
        Assert.Equal("21", Desk.MonitorByRole("second")!.Displayed!.Name);
        Assert.False(_fixture.IsHidden(1));
        Assert.False(_fixture.IsHidden(2));
    }

    [Fact]
    public void A_window_moved_to_a_hidden_workspace_disappears()
    {
        _fixture.Open(1);
        _fixture.Open(2);
        _fixture.Turn();

        Assert.True(Desk.MoveToWorkspace(DeskFixture.W(2), "13"));
        _fixture.Turn();

        Assert.True(_fixture.IsHidden(2));
        // And the one left behind gets the whole screen back.
        Assert.Equal(new Rect(0, 42, 3840, 2118), _fixture.FrameOf(1));
    }

    [Fact]
    public void A_window_can_be_moved_to_the_other_monitor()
    {
        _fixture.Open(1);
        _fixture.Turn();

        Assert.True(Desk.MoveToWorkspace(DeskFixture.W(1), "21"));
        _fixture.Turn();

        Assert.False(_fixture.IsHidden(1));
        Assert.True(_fixture.FrameOf(1).Left >= 3840, $"it is at {_fixture.FrameOf(1)}");
    }

    // ---- states -----------------------------------------------------------

    [Fact]
    public void A_window_that_cannot_be_resized_floats_from_the_start()
    {
        _fixture.Open(1, resizable: false, frame: new Rect(500, 500, 400, 300));
        _fixture.Turn();

        Assert.Equal(WindowState.Floating, _fixture.Managed(1)!.State);
        // And it is left the size it chose.
        Assert.Equal(new Rect(500, 500, 400, 300), _fixture.FrameOf(1));
    }

    [Fact]
    public void Floating_a_tiled_window_gives_the_space_back_to_the_others()
    {
        _fixture.Open(1);
        _fixture.Open(2);
        _fixture.Turn();

        Assert.True(Desk.SetFloating(DeskFixture.W(2), true));
        _fixture.Turn();

        Assert.Equal(new Rect(0, 42, 3840, 2118), _fixture.FrameOf(1));
        Assert.Equal(WindowState.Floating, _fixture.Managed(2)!.State);
    }

    [Fact]
    public void A_floating_window_keeps_its_place_across_a_workspace_switch()
    {
        _fixture.Open(1, resizable: false, frame: new Rect(500, 500, 400, 300));
        _fixture.Turn();

        Desk.FocusWorkspace("12");
        _fixture.Turn();
        Desk.FocusWorkspace("11");
        _fixture.Turn();

        Assert.Equal(new Rect(500, 500, 400, 300), _fixture.FrameOf(1));
    }

    [Fact]
    public void A_fullscreen_window_covers_the_whole_screen_taskbar_included()
    {
        _fixture.Open(1);
        _fixture.Turn();

        Assert.True(Desk.SetFullscreen(DeskFixture.W(1), true));
        Redraw redraw = _fixture.Turn();

        Assert.Equal(new Rect(0, 0, 3840, 2160), _fixture.FrameOf(1));
        Assert.Contains((DeskFixture.W(1), true), redraw.TaskbarMark);
    }

    [Fact]
    public void A_fullscreen_window_takes_the_others_out_of_the_top_band()
    {
        _fixture.Open(1, resizable: false, frame: new Rect(500, 500, 400, 300));
        _fixture.Open(2);
        _fixture.Turn();
        Assert.True(_fixture.Platform.Window(DeskFixture.W(1))!.IsTopmost);

        Desk.SetFullscreen(DeskFixture.W(2), true);
        _fixture.Turn();

        // An always-on-top window cannot be put behind a normal one; it has to
        // leave the band first.
        Assert.False(_fixture.Platform.Window(DeskFixture.W(1))!.IsTopmost);
    }

    [Fact]
    public void Leaving_fullscreen_goes_back_to_what_it_was_before()
    {
        _fixture.Open(1, resizable: false, frame: new Rect(500, 500, 400, 300));
        _fixture.Turn();
        Assert.Equal(WindowState.Floating, _fixture.Managed(1)!.State);

        Desk.SetFullscreen(DeskFixture.W(1), true);
        _fixture.Turn();
        Desk.SetFullscreen(DeskFixture.W(1), false);
        _fixture.Turn();

        // Floating, not tiling: a window that was floating before the game
        // went fullscreen must not be swallowed by the layout afterwards.
        Assert.Equal(WindowState.Floating, _fixture.Managed(1)!.State);
    }

    [Fact]
    public void A_window_that_covers_the_screen_by_itself_becomes_fullscreen()
    {
        _fixture.Open(1);
        _fixture.Turn();

        // A game going fullscreen on its own, which AkuWM did not ask for.
        _fixture.Platform.Place([new Placement(DeskFixture.W(1), new Rect(0, 0, 3840, 2160))]);
        _fixture.Sync();

        Assert.Equal(WindowState.Fullscreen, _fixture.Managed(1)!.State);
    }

    [Fact]
    public void A_minimised_window_is_left_alone()
    {
        _fixture.Open(1);
        _fixture.Open(2);
        _fixture.Turn();

        _fixture.Platform.SetMinimized(DeskFixture.W(2), true);
        _fixture.Sync();
        Redraw redraw = _fixture.Turn();

        Assert.Equal(WindowState.Minimized, _fixture.Managed(2)!.State);
        Assert.DoesNotContain(redraw.Hide, h => h == DeskFixture.W(2));
        // And the one still on screen takes the space.
        Assert.Equal(new Rect(0, 42, 3840, 2118), _fixture.FrameOf(1));
    }

    [Fact]
    public void A_window_coming_back_from_the_taskbar_rejoins_the_layout()
    {
        _fixture.Open(1);
        _fixture.Open(2);
        _fixture.Turn();
        _fixture.Platform.SetMinimized(DeskFixture.W(2), true);
        _fixture.Sync();
        _fixture.Turn();

        _fixture.Platform.SetMinimized(DeskFixture.W(2), false);
        _fixture.Sync();
        _fixture.Turn();

        Assert.Equal(WindowState.Tiling, _fixture.Managed(2)!.State);
        Assert.True(_fixture.FrameOf(1).Width < 3000, "the two should be sharing the screen again");
    }

    // ---- sticky -----------------------------------------------------------

    [Fact]
    public void A_sticky_window_is_on_every_workspace_of_its_monitor()
    {
        _fixture.Open(1, resizable: false, frame: new Rect(3000, 1500, 600, 400));
        Assert.True(Desk.SetSticky(DeskFixture.W(1), true));
        _fixture.Turn();

        Desk.FocusWorkspace("12");
        _fixture.Turn();

        Assert.False(_fixture.IsHidden(1));
        Assert.Equal(new Rect(3000, 1500, 600, 400), _fixture.FrameOf(1));
    }

    [Fact]
    public void A_sticky_window_never_moves_to_the_other_screen()
    {
        _fixture.Open(1, resizable: false, frame: new Rect(3000, 1500, 600, 400));
        Desk.SetSticky(DeskFixture.W(1), true);
        _fixture.Turn();

        // Switching a workspace on the other monitor must not touch it: it
        // belongs to its monitor, not to a workspace.
        Desk.FocusWorkspace("22");
        _fixture.Turn();

        Assert.Equal("main", _fixture.Managed(1)!.StickyMonitor);
        Assert.Equal(new Rect(3000, 1500, 600, 400), _fixture.FrameOf(1));
    }

    [Fact]
    public void A_sticky_window_is_never_in_a_tiling_tree()
    {
        _fixture.Open(1);
        Desk.SetSticky(DeskFixture.W(1), true);

        // It would have to be in every tree at once, so sticky means floating.
        Assert.Equal(WindowState.Floating, _fixture.Managed(1)!.State);
        Assert.All(Desk.Workspaces, w => Assert.False(w.Tiling.Contains(DeskFixture.W(1))));
    }

    // ---- rules ------------------------------------------------------------

    [Fact]
    public void A_rule_can_send_a_window_to_a_named_workspace()
    {
        AkuWmConfig config = DeskFixture.Configuration();
        config.Rules =
        [
            new RuleConfig
            {
                Id = "r-telegram",
                Name = "Telegram",
                Match = [new MatchCriteria { Process = "Telegram" }],
                Target = new RuleTarget { Workspace = "13" },
            },
        ];

        var fixture = new DeskFixture(config);
        fixture.Open(1, process: "Telegram");
        fixture.Turn();

        Assert.Equal("13", fixture.Managed(1)!.Workspace);
        Assert.True(fixture.IsHidden(1), "13 is not the workspace on screen, so it opens hidden");
    }

    [Fact]
    public void A_rule_can_float_a_window()
    {
        AkuWmConfig config = DeskFixture.Configuration();
        config.Rules =
        [
            new RuleConfig
            {
                Name = "calculator",
                Match = [new MatchCriteria { Process = "CalculatorApp" }],
                Actions = ["float"],
            },
        ];

        var fixture = new DeskFixture(config);
        fixture.Open(1, process: "CalculatorApp", frame: new Rect(100, 100, 500, 600));
        fixture.Turn();

        Assert.Equal(WindowState.Floating, fixture.Managed(1)!.State);
        Assert.Equal(new Rect(100, 100, 500, 600), fixture.FrameOf(1));
    }

    [Fact]
    public void An_ignored_window_is_never_touched()
    {
        AkuWmConfig config = DeskFixture.Configuration();
        config.Rules =
        [
            new RuleConfig
            {
                Name = "zebar",
                Match = [new MatchCriteria { Process = "zebar" }],
                Actions = ["ignore"],
            },
        ];

        var fixture = new DeskFixture(config);
        fixture.Open(1, process: "zebar", frame: new Rect(0, 0, 3840, 40));
        fixture.Open(2);
        fixture.Turn();

        Assert.False(fixture.Managed(1)!.Managed);
        Assert.Equal(new Rect(0, 0, 3840, 40), fixture.FrameOf(1));
        // And the bar's space is not reserved: the other window still tiles
        // over the whole work area.
        Assert.Equal(new Rect(0, 42, 3840, 2118), fixture.FrameOf(2));
    }

    // ---- what the platform will not allow ---------------------------------

    [Fact]
    public void An_elevated_window_is_hidden_but_not_moved_without_uiAccess()
    {
        Desk.CanPositionElevated = false;
        _fixture.Open(1, elevated: true, frame: new Rect(700, 700, 800, 600));
        _fixture.Turn();

        // UIPI refuses the move and says nothing, so the model does not ask.
        Assert.Equal(new Rect(700, 700, 800, 600), _fixture.FrameOf(1));

        Desk.FocusWorkspace("12");
        _fixture.Turn();

        // Hiding it still works, which is what matters for a game on a
        // workspace nobody is looking at.
        Assert.True(_fixture.IsHidden(1));
    }

    [Fact]
    public void The_focus_is_refused_for_a_window_nobody_can_see()
    {
        _fixture.Open(1);
        _fixture.Turn();
        Desk.FocusWorkspace("12");
        _fixture.Turn();

        // Something raised a cloaked window. Following it would leave the
        // keyboard pointing at a window that is not on the screen.
        Assert.False(Desk.Focus(DeskFixture.W(1)));
    }

    [Fact]
    public void A_window_that_lands_a_few_pixels_off_is_left_where_it_landed()
    {
        _fixture.Open(1, frame: new Rect(0, 42, 3840, 2118));
        _fixture.Open(2);
        _fixture.Turn();

        // A terminal rounding its size to whole character cells: it takes the
        // move and settles a few pixels away, for ever.
        Rect landed = _fixture.FrameOf(2);
        _fixture.Platform.Place([new Placement(DeskFixture.W(2), landed with { Height = landed.Height - 11 })]);
        _fixture.Sync();

        Assert.True(Desk.Compute().IsNothing, Desk.Compute().ToString());
    }

    [Fact]
    public void A_window_somebody_dragged_away_is_put_back()
    {
        _fixture.Open(1);
        _fixture.Open(2);
        _fixture.Turn();

        Rect was = _fixture.FrameOf(2);
        _fixture.Platform.Place([new Placement(DeskFixture.W(2), new Rect(500, 500, 600, 400))]);
        _fixture.Sync();
        _fixture.Turn();

        Assert.Equal(was, _fixture.FrameOf(2));
    }

    [Fact]
    public void A_window_that_will_not_be_moved_is_not_argued_with_for_ever()
    {
        // A window with a minimum size of its own: it takes the position and
        // refuses the size, for ever.
        _fixture.Open(1, frame: new Rect(0, 42, 3840, 2118));
        _fixture.Open(2);
        _fixture.Platform.Stubborn.Add(2);

        _fixture.Turn();
        Assert.NotEmpty(Desk.Compute().Place);

        _fixture.Wait(3000);
        _fixture.Turn();

        // Asking again every time is an argument the window always wins, at
        // the cost of a window manager that never stops working.
        Assert.True(Desk.Compute().IsNothing, Desk.Compute().ToString());
        Assert.True(_fixture.Managed(2)!.PlacementRefused);
    }

    [Fact]
    public void When_a_hidden_window_cannot_be_brought_back_nothing_is_hidden()
    {
        // Measured on this machine: one spelling of the call that hides a
        // window cannot be undone by anything at all. If the platform finds
        // itself on the wrong side of that, every workspace shows everything
        // -- a bad desk, against a lost window.
        Desk.CanHide = false;
        _fixture.Open(1);
        _fixture.Turn();

        Desk.FocusWorkspace("12");
        Redraw redraw = _fixture.Turn();

        Assert.Empty(redraw.Hide);
        Assert.False(_fixture.IsHidden(1));
    }

    [Fact]
    public void A_window_can_still_be_shown_when_hiding_is_off()
    {
        _fixture.Open(1);
        _fixture.Turn();
        Desk.FocusWorkspace("12");
        _fixture.Turn();
        Assert.True(_fixture.IsHidden(1));

        // Whatever is already hidden must still be recoverable, or turning
        // this off would strand exactly the windows it exists to protect.
        Desk.CanHide = false;
        Desk.FocusWorkspace("11");
        Redraw redraw = _fixture.Turn();

        Assert.Contains(DeskFixture.W(1), redraw.Show);
        Assert.False(_fixture.IsHidden(1));
    }

    // ---- direction --------------------------------------------------------

    [Fact]
    public void Focus_moves_to_the_window_next_door()
    {
        _fixture.Open(1);
        _fixture.Open(2);
        _fixture.Turn();
        Desk.Focus(DeskFixture.W(1));

        Assert.Equal(DeskFixture.W(2), Desk.InDirection(Direction.Right));
        Assert.Equal(WindowHandle.None, Desk.InDirection(Direction.Left));
    }

    [Fact]
    public void Focus_crosses_to_the_other_monitor_when_there_is_nothing_left()
    {
        _fixture.Open(1);
        _fixture.Open(2, monitor: new MonitorHandle(2));
        _fixture.Turn();
        Desk.Focus(DeskFixture.W(1));

        // The portrait monitor is to the right of the main one, so "right"
        // from the only window on main lands on it -- sway's focus output.
        Assert.Equal(DeskFixture.W(2), Desk.InDirection(Direction.Right));
    }

    [Fact]
    public void A_window_moved_off_the_edge_lands_on_the_next_monitor()
    {
        _fixture.Open(1);
        _fixture.Turn();
        Desk.Focus(DeskFixture.W(1));

        Assert.True(Desk.MoveFocused(Direction.Right));
        _fixture.Turn();

        Assert.Equal("21", _fixture.Managed(1)!.Workspace);
        Assert.True(_fixture.FrameOf(1).Left >= 3840);
    }
}
