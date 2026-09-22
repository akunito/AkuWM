using AkuWM.Core.Compat;
using AkuWM.Core.Config;
using AkuWM.Core.Desk;
using AkuWM.Core.Layout;
using AkuWM.Core.Model;
using Xunit;

namespace AkuWM.Tests;

/// <summary>
/// The second adversarial pass over the model (2026-09-22): what the model
/// remembered that Windows had undone, and what it forgot to tell Windows.
/// </summary>
public class ModelAuditTests
{
    private readonly DeskFixture _fixture = new();

    private Desk Desk => _fixture.Desk;

    private static WindowHandle W(long handle) => new(handle);

    // ---- the focus after a switch to an empty workspace ---------------------

    [Fact]
    public void Switching_to_an_empty_workspace_leaves_nothing_focused_and_takes_the_keyboard_away()
    {
        _fixture.Open(1);
        _fixture.Turn();
        _fixture.Foreground(1);

        Desk.WantFocus(Desk.FocusWorkspace("13"));
        Redraw redraw = _fixture.Turn();

        Assert.True(redraw.Unfocus);
        Assert.Contains("unfocus", _fixture.Platform.Calls);
        Assert.Equal(WindowHandle.None, Desk.Focused);
        Assert.True(_fixture.IsHidden(1));

        DeskMonitor main = Desk.MonitorByRole("main")!;
        Assert.False(GlazeView.Workspace(Desk, main, Desk.Workspace("11")!)["hasFocus"]!.GetValue<bool>());
    }

    [Fact]
    public void A_chord_with_no_subject_never_acts_on_a_hidden_window()
    {
        _fixture.Open(1);
        _fixture.Turn();
        _fixture.Foreground(1);
        var executor = new GlazeExecutor(Desk, new FakeDeskPlatform());

        Desk.WantFocus(Desk.FocusWorkspace("13"));
        _fixture.Turn();

        ExecResult result = executor.Command("toggle-floating");

        Assert.False(result.Success);
        Assert.Equal(WindowState.Tiling, _fixture.Managed(1)!.State);
    }

    // ---- a minimised window brought back on a hidden workspace ---------------

    [Fact]
    public void Restoring_a_window_from_the_taskbar_shows_its_workspace_instead_of_hiding_the_window()
    {
        _fixture.Open(1);
        Desk.FocusWorkspace("12");
        _fixture.Open(2, resizable: false);
        _fixture.Turn();
        _fixture.Foreground(2);
        _fixture.Platform.SetMinimized(W(2), true);
        _fixture.Sync();

        Desk.WantFocus(Desk.FocusWorkspace("11"));
        _fixture.Turn();

        // The taskbar button is clicked.
        _fixture.Platform.SetMinimized(W(2), false);
        _fixture.Sync();
        Redraw redraw = _fixture.Turn();

        Assert.DoesNotContain(W(2), redraw.Hide);
        Assert.True(Desk.Workspace("12")!.Displayed);
        Assert.False(_fixture.IsHidden(2));
        Assert.Equal(W(2), Desk.Focused);
    }

    // ---- sticky over fullscreen -------------------------------------------

    [Fact]
    public void Sticking_a_fullscreen_window_takes_it_out_of_fullscreen_and_releases_the_taskbar()
    {
        _fixture.Open(1);
        _fixture.Turn();
        Desk.SetFullscreen(W(1), true);
        _fixture.Turn();

        Desk.SetSticky(W(1), true);
        Redraw redraw = _fixture.Turn();

        Assert.Equal(WindowState.Floating, _fixture.Managed(1)!.State);
        Assert.Contains((W(1), false), redraw.TaskbarMark);
        Assert.True(Desk.Workspace("11")!.Fullscreen.IsNone);
        Assert.True(Desk.SetFullscreen(W(1), true), "a sticky window can be asked to cover the screen again");
    }

    // ---- minimised windows and the focus order -----------------------------

    [Fact]
    public void Returning_to_a_workspace_focuses_a_visible_window_not_the_minimised_one()
    {
        _fixture.Open(1);
        _fixture.Open(2, resizable: false);
        _fixture.Turn();
        _fixture.Foreground(2);
        _fixture.Platform.SetMinimized(W(2), true);
        _fixture.Sync();

        Desk.WantFocus(Desk.FocusWorkspace("12"));
        _fixture.Turn();

        Assert.Equal(W(1), Desk.FocusWorkspace("11"));
    }

    [Fact]
    public void Crossing_a_monitor_edge_never_lands_on_a_minimised_window()
    {
        _fixture.Open(1);
        _fixture.Turn();
        Desk.FocusWorkspace("21");
        _fixture.Open(2, monitor: new MonitorHandle(2), resizable: false);
        _fixture.Open(3, monitor: new MonitorHandle(2));
        _fixture.Turn();
        _fixture.Foreground(2);
        _fixture.Platform.SetMinimized(W(2), true);
        _fixture.Sync();
        _fixture.Foreground(1);

        Assert.Equal(W(3), Desk.InDirection(Direction.Right));
    }

    // ---- fullscreen across the taskbar -------------------------------------

    [Fact]
    public void A_floating_window_that_went_fullscreen_and_minimised_comes_back_floating_when_it_leaves_fullscreen()
    {
        _fixture.Open(1, resizable: false);
        _fixture.Turn();
        Assert.Equal(WindowState.Floating, _fixture.Managed(1)!.State);

        Desk.SetFullscreen(W(1), true);
        _fixture.Turn();
        _fixture.Platform.SetMinimized(W(1), true);
        _fixture.Sync();
        _fixture.Platform.SetMinimized(W(1), false);
        _fixture.Sync();

        Assert.Equal(WindowState.Fullscreen, _fixture.Managed(1)!.State);
        Assert.Equal(W(1), Desk.Workspace("11")!.Fullscreen);

        Desk.SetFullscreen(W(1), false);
        Assert.Equal(WindowState.Floating, _fixture.Managed(1)!.State);
    }

    [Fact]
    public void A_fullscreen_console_that_rounds_to_its_cells_stays_fullscreen()
    {
        _fixture.Open(1);
        _fixture.Turn();
        Desk.SetFullscreen(W(1), true);
        _fixture.Turn();

        // A character cell short of the monitor, which is where a console lands.
        _fixture.Platform.ApplicationMoves(W(1), new Rect(0, 0, 3839, 2160));
        _fixture.Sync();

        Assert.Equal(WindowState.Fullscreen, _fixture.Managed(1)!.State);
    }

    [Fact]
    public void A_game_that_leaves_fullscreen_by_itself_is_still_noticed()
    {
        _fixture.Open(1, resizable: false);
        _fixture.Turn();
        Desk.SetFullscreen(W(1), true);
        _fixture.Turn();

        _fixture.Platform.ApplicationMoves(W(1), new Rect(100, 100, 1280, 720));
        _fixture.Sync();

        Assert.Equal(WindowState.Floating, _fixture.Managed(1)!.State);
    }

    // ---- a focus the platform refused ---------------------------------------

    [Fact]
    public void A_refused_focus_is_not_recorded_as_taken()
    {
        _fixture.Open(1);
        _fixture.Open(2);
        _fixture.Turn();
        _fixture.Foreground(1);

        _fixture.Platform.RefusesFocus.Add(2);
        Desk.WantFocus(W(2));
        _fixture.Turn();

        Assert.Equal(W(1), Desk.Focused);
        Assert.True(Desk.Compute().Focus.IsNone, "and it is not asked for again on every redraw");
    }

    [Fact]
    public void A_window_that_closes_before_the_focus_reaches_it_is_not_focused()
    {
        _fixture.Open(1);
        _fixture.Open(2);
        _fixture.Turn();
        _fixture.Foreground(1);

        Desk.WantFocus(W(2));
        _fixture.Close(2);
        _fixture.Turn();

        Assert.NotEqual(W(2), Desk.Focused);
    }

    // ---- reload moving a displayed workspace --------------------------------

    [Fact]
    public void A_reload_that_moves_the_displayed_workspace_leaves_one_displayed_per_monitor()
    {
        _fixture.Open(1);
        Desk.FocusWorkspace("12");
        _fixture.Open(2);
        _fixture.Turn();

        AkuWmConfig config = DeskFixture.Configuration();
        config.Workspaces!.First(w => w.Name == "12").Monitor = "second";
        Desk.Reload(config);
        _fixture.Turn();

        Assert.Equal(1, Desk.MonitorByRole("second")!.Workspaces.Count(w => w.Displayed));
        Assert.Equal(1, Desk.MonitorByRole("main")!.Workspaces.Count(w => w.Displayed));
    }

    [Fact]
    public void Back_and_forth_never_goes_to_a_workspace_that_moved_to_another_monitor()
    {
        _fixture.Open(1);
        _fixture.Turn();
        Desk.FocusWorkspace("12");
        _fixture.Turn();

        AkuWmConfig config = DeskFixture.Configuration();
        config.Workspaces!.First(w => w.Name == "11").Monitor = "second";
        Desk.Reload(config);
        _fixture.Turn();

        Desk.FocusWorkspace("12");
        _fixture.Turn();

        Assert.NotNull(Desk.MonitorByRole("main")!.Displayed);
        Assert.False(_fixture.IsHidden(1) && Desk.MonitorByRole("main")!.Displayed is null);
    }

    // ---- behind the game ----------------------------------------------------

    [Fact]
    public void Windows_leaving_the_band_for_a_game_are_put_behind_it()
    {
        _fixture.Open(1, elevated: true); // a game: elevated, as they are
        _fixture.Open(2, resizable: false);
        _fixture.Turn();
        Desk.SetSticky(W(2), true);
        _fixture.Turn();
        Assert.True(_fixture.Managed(2)!.Banded);

        Desk.SetFullscreen(W(1), true);
        Redraw redraw = _fixture.Turn();

        Assert.Contains((W(2), false), redraw.Band);
        Assert.Contains((W(2), W(1)), redraw.Behind);
        Assert.Equal(W(1), _fixture.Managed(2)!.Behind);

        // Once per pair: the next pass does not send it again.
        Assert.Empty(_fixture.Turn().Behind);

        // Back in the band when the game goes, and owed again next time.
        Desk.SetFullscreen(W(1), false);
        Redraw back = _fixture.Turn();
        Assert.Contains((W(2), true), back.Band);
        Assert.True(_fixture.Managed(2)!.Behind.IsNone);
    }

    [Fact]
    public void The_fullscreen_window_itself_is_never_sent_a_band_change()
    {
        WindowSnapshot topmostGame = FakePlatform.Window(1, "game", frame: new Rect(0, 0, 3840, 2160)) with { IsTopmost = true };
        _fixture.Platform.WindowList.Add(topmostGame);
        _fixture.Sync();

        Redraw redraw = _fixture.Turn();

        Assert.Equal(WindowState.Fullscreen, _fixture.Managed(1)!.State);
        Assert.DoesNotContain(redraw.Band, b => b.Window == W(1));
        Assert.Equal(true, _fixture.Managed(1)!.Banded);
    }

    [Fact]
    public void A_window_adopted_outside_the_band_is_not_sent_out_of_it()
    {
        _fixture.Open(1);
        Redraw redraw = _fixture.Turn();

        Assert.DoesNotContain(redraw.Band, b => b.Window == W(1));
    }

    // ---- decoration ---------------------------------------------------------

    private static DeskFixture Decorated()
    {
        AkuWmConfig config = DeskFixture.Configuration();
        config.Effects = new EffectsConfig { FocusedBorder = "#c4a7e7", OtherBorder = "none", Corners = "square" };
        return new DeskFixture(config);
    }

    [Fact]
    public void A_decoration_the_shell_refuses_is_asked_for_once()
    {
        DeskFixture fixture = Decorated();
        fixture.Open(1);
        fixture.Turn();
        fixture.Foreground(1);
        fixture.Platform.RefusesDecoration = true;

        Redraw first = fixture.Turn();
        Assert.Contains(first.Decorate, d => d.Window == W(1));
        Assert.NotNull(fixture.Managed(1)!.DecorationRefused);

        Assert.DoesNotContain(fixture.Turn().Decorate, d => d.Window == W(1));

        // Asking again is a fresh question.
        fixture.Desk.Redecorate(W(1));
        Assert.Contains(fixture.Turn().Decorate, d => d.Window == W(1));
    }

    [Fact]
    public void Redecorating_sends_the_same_decoration_again()
    {
        DeskFixture fixture = Decorated();
        fixture.Open(1);
        fixture.Turn();
        fixture.Foreground(1);
        fixture.Turn();
        Assert.NotNull(fixture.Managed(1)!.Decorated);
        Assert.Empty(fixture.Turn().Decorate);

        fixture.Desk.Redecorate(W(1));
        Redraw redraw = fixture.Turn();

        Assert.Contains(redraw.Decorate, d => d.Window == W(1));
    }

    // ---- direction ----------------------------------------------------------

    [Fact]
    public void A_workspace_direction_in_the_configuration_beats_the_shape_of_the_screen()
    {
        AkuWmConfig config = DeskFixture.Configuration();
        config.Workspaces!.First(w => w.Name == "21").Direction = "horizontal";
        var fixture = new DeskFixture(config);

        fixture.Desk.FocusWorkspace("21");
        fixture.Open(1, monitor: new MonitorHandle(2));
        fixture.Open(2, monitor: new MonitorHandle(2));
        fixture.Turn();

        Assert.Equal(fixture.FrameOf(1).Top, fixture.FrameOf(2).Top);
        Assert.True(fixture.FrameOf(2).Left > fixture.FrameOf(1).Left);
    }

    [Fact]
    public void An_empty_workspace_reports_the_direction_its_next_window_will_take()
    {
        DeskMonitor second = Desk.MonitorByRole("second")!;
        Workspace workspace = Desk.Workspace("21")!;

        Assert.Equal("vertical", GlazeView.Workspace(Desk, second, workspace)["tilingDirection"]!.GetValue<string>());
    }

    // ---- naming a monitor -----------------------------------------------------

    [Fact]
    public void Focusing_an_empty_monitor_by_name_is_where_the_next_window_opens()
    {
        _fixture.Open(1);
        _fixture.Turn();
        _fixture.Foreground(1);
        var executor = new GlazeExecutor(Desk, new FakeDeskPlatform());

        Assert.True(executor.Command("focus --monitor 1").Success);
        _fixture.Turn();
        _fixture.Open(2);

        Assert.Equal("21", _fixture.Managed(2)!.Workspace);
    }
}

/// <summary>Floating windows and the windows that cover a screen (2026-09-22).</summary>
public class FloatingAboveTests
{
    private static WindowHandle W(long handle) => new(handle);

    [Fact]
    public void A_maximised_application_does_not_take_floating_windows_out_of_the_band()
    {
        var fixture = new DeskFixture();
        fixture.Open(1, resizable: false);
        fixture.Turn();
        Assert.True(fixture.Managed(1)!.Banded);

        // A browser maximised on a monitor with no taskbar: it covers the
        // screen, it keeps its resize border, and it is not a game.
        fixture.Platform.WindowList.Add(FakePlatform.Window(2, "zen", frame: new Rect(0, 0, 3840, 2160), maximized: true));
        fixture.Sync();
        Redraw redraw = fixture.Turn();

        Assert.Equal(WindowState.Fullscreen, fixture.Managed(2)!.State);
        Assert.DoesNotContain(redraw.Band, b => b.Window == W(1));
        Assert.Empty(redraw.Behind);
        Assert.True(fixture.Managed(1)!.Banded);
    }

    [Fact]
    public void A_borderless_window_covering_the_screen_still_shields_itself()
    {
        var fixture = new DeskFixture();
        fixture.Open(1, resizable: false);
        fixture.Turn();

        fixture.Platform.WindowList.Add(FakePlatform.Window(2, "game", frame: new Rect(0, 0, 3840, 2160), resizable: false));
        fixture.Sync();
        Redraw redraw = fixture.Turn();

        Assert.Contains((W(1), false), redraw.Band);
        Assert.Contains((W(1), W(2)), redraw.Behind);
    }

    [Fact]
    public void The_old_behaviour_is_one_setting_away()
    {
        AkuWmConfig config = DeskFixture.Configuration();
        config.Layout!.FloatingAboveMaximized = false;
        var fixture = new DeskFixture(config);
        fixture.Open(1, resizable: false);
        fixture.Turn();

        fixture.Platform.WindowList.Add(FakePlatform.Window(2, "zen", frame: new Rect(0, 0, 3840, 2160), maximized: true));
        fixture.Sync();
        Redraw redraw = fixture.Turn();

        Assert.Contains((W(1), false), redraw.Band);
    }

    [Fact]
    public void A_tiled_window_that_bands_itself_is_taken_out_again()
    {
        var fixture = new DeskFixture();
        fixture.Open(1);
        fixture.Turn();
        Assert.False(fixture.Managed(1)!.Banded);

        // NordVPN puts itself in the always-on-top band on activation.
        fixture.Platform.SetTopmost(W(1), true);
        fixture.Sync();
        Redraw redraw = fixture.Turn();

        Assert.Contains((W(1), false), redraw.Band);
        Assert.False(fixture.Platform.Window(W(1))!.IsTopmost);
    }
}

/// <summary>A maximised application is left where Windows keeps it (2026-09-22).</summary>
public class MaximisedApplicationTests
{
    private static WindowHandle W(long handle) => new(handle);

    [Fact]
    public void A_maximised_application_under_a_bar_is_neither_moved_nor_marked_fullscreen()
    {
        var fixture = new DeskFixture();

        // Zen maximised on the vertical monitor: Windows keeps it in the work
        // area, 35 px under the bar, and it ignores a move.
        Rect workArea = FakePlatform.SecondMonitor().WorkArea;
        fixture.Platform.WindowList.Add(FakePlatform.Window(1, "zen", frame: workArea, monitor: new MonitorHandle(2), maximized: true));
        fixture.Sync();
        Redraw redraw = fixture.Turn();

        Assert.Equal(WindowState.Fullscreen, fixture.Managed(1)!.State);
        Assert.DoesNotContain(redraw.Place, p => p.Window == W(1));
        Assert.DoesNotContain(redraw.TaskbarMark, m => m.Window == W(1));
    }

    [Fact]
    public void A_maximised_window_that_covers_the_whole_monitor_still_drops_the_taskbar()
    {
        var fixture = new DeskFixture();
        fixture.Platform.WindowList.Add(FakePlatform.Window(1, "game", frame: new Rect(0, 0, 3840, 2160), maximized: true, resizable: false));
        fixture.Sync();
        Redraw redraw = fixture.Turn();

        Assert.Contains((W(1), true), redraw.TaskbarMark);
        Assert.DoesNotContain(redraw.Place, p => p.Window == W(1));
    }
}

/// <summary>A window that outgrew its monitor is not a fullscreen one (2026-09-22).</summary>
public class OversizedWindowTests
{
    private static WindowHandle W(long handle) => new(handle);

    [Fact]
    public void A_window_bigger_than_the_screen_is_not_fullscreen_and_can_be_floated()
    {
        var fixture = new DeskFixture();
        fixture.Platform.WindowList.Add(FakePlatform.Window(1, "notepad++", frame: new Rect(-4, -213, 5766, 2373)));
        fixture.Sync();
        fixture.Turn();

        Assert.Equal(WindowState.Tiling, fixture.Managed(1)!.State);

        // The person floats it; the next observation of the same huge
        // rectangle must not put it back into fullscreen.
        Assert.True(fixture.Desk.SetFloating(W(1), true));
        fixture.Turn();
        fixture.Platform.ApplicationMoves(W(1), new Rect(-4, -213, 5766, 2373));
        fixture.Sync();

        Assert.Equal(WindowState.Floating, fixture.Managed(1)!.State);
    }

    [Fact]
    public void A_borderless_window_the_size_of_the_screen_still_is()
    {
        var fixture = new DeskFixture();
        fixture.Platform.WindowList.Add(FakePlatform.Window(1, "game", frame: new Rect(0, 0, 3840, 2160), resizable: false));
        fixture.Sync();
        fixture.Turn();

        Assert.Equal(WindowState.Fullscreen, fixture.Managed(1)!.State);
    }
}

/// <summary>
/// A window that leaves its scaling to Windows must never be placed with its
/// invisible border on another screen (Notepad++, 2026-09-22).
/// </summary>
public class SystemDpiAwareWindowTests
{
    private static WindowHandle W(long handle) => new(handle);

    [Fact]
    public void A_system_dpi_aware_window_alone_on_a_screen_keeps_its_border_on_that_screen()
    {
        var fixture = new DeskFixture();
        fixture.Platform.WindowList.Add(FakePlatform.Window(1, "notepad++", perMonitorDpi: false));
        fixture.Sync();
        Redraw redraw = fixture.Turn();

        Placement placement = Assert.Single(redraw.Place, p => p.Window == W(1));
        Rect screen = FakePlatform.MainMonitor().Bounds;

        // The fake's border is 9 px all round: frame + border must lie inside 0..3840 x 0..2160.
        Assert.True(placement.Frame.Left - 9 >= screen.Left, $"{placement.Frame}");
        Assert.True(placement.Frame.Right + 9 <= screen.Right, $"{placement.Frame}");
        Assert.True(placement.Frame.Bottom + 9 <= screen.Bottom, $"{placement.Frame}");
    }

    [Fact]
    public void A_per_monitor_window_is_placed_edge_to_edge_as_before()
    {
        var fixture = new DeskFixture();
        fixture.Open(1);
        Redraw redraw = fixture.Turn();

        Placement placement = Assert.Single(redraw.Place, p => p.Window == W(1));
        Assert.Equal(FakePlatform.MainMonitor().WorkArea, placement.Frame);
    }

    [Fact]
    public void The_fullscreen_placement_of_such_a_window_stays_inside_the_screen_too()
    {
        var fixture = new DeskFixture();
        fixture.Platform.WindowList.Add(FakePlatform.Window(1, "notepad++", perMonitorDpi: false));
        fixture.Sync();
        fixture.Turn();
        fixture.Desk.SetFullscreen(W(1), true);
        Redraw redraw = fixture.Turn();

        Placement placement = Assert.Single(redraw.Place, p => p.Window == W(1));
        Rect screen = FakePlatform.MainMonitor().Bounds;
        Assert.True(placement.Frame.Right + 9 <= screen.Right, $"{placement.Frame}");
        Assert.True(placement.Frame.Left - 9 >= screen.Left, $"{placement.Frame}");
    }
}

/// <summary>Notepad++ meets Brave on the vertical monitor (2026-09-22).</summary>
public class MaximisedNeighbourTests
{
    private static WindowHandle W(long handle) => new(handle);

    private static DeskFixture BraveMaximisedOnTheSecondMonitor()
    {
        var fixture = new DeskFixture();
        fixture.Platform.WindowList.Add(FakePlatform.Window(
            1, "brave", frame: FakePlatform.SecondMonitor().WorkArea, monitor: new MonitorHandle(2), maximized: true));
        fixture.Sync();
        fixture.Turn();
        Assert.Equal(WindowState.Fullscreen, fixture.Managed(1)!.State);
        return fixture;
    }

    [Fact]
    public void A_window_moved_beside_a_maximised_application_shares_the_screen_with_it()
    {
        DeskFixture fixture = BraveMaximisedOnTheSecondMonitor();
        fixture.Platform.WindowList.Add(FakePlatform.Window(2, "notepad++", perMonitorDpi: false));
        fixture.Sync();
        fixture.Turn();

        Assert.True(fixture.Desk.MoveToWorkspace(W(2), "21"));
        Redraw redraw = fixture.Turn();

        Assert.Contains(W(1), redraw.Unmaximize);
        Assert.Equal(WindowState.Tiling, fixture.Managed(1)!.State);
        Assert.Equal(WindowState.Tiling, fixture.Managed(2)!.State);
        Assert.True(fixture.Desk.Workspace("21")!.Fullscreen.IsNone);

        // Both get a tile, stacked on the portrait screen, and the maximised
        // rectangle Windows still reports for a beat does not put Brave back.
        fixture.Turn();
        fixture.Turn();
        Assert.False(fixture.Platform.Window(W(1))!.IsMaximized);
        Assert.Equal(WindowState.Tiling, fixture.Managed(1)!.State);
        Assert.True(fixture.FrameOf(2).Top > fixture.FrameOf(1).Top || fixture.FrameOf(1).Top > fixture.FrameOf(2).Top);
        Assert.True(fixture.FrameOf(1).Height < 2000 && fixture.FrameOf(2).Height < 2000, $"{fixture.FrameOf(1)} {fixture.FrameOf(2)}");
    }

    [Fact]
    public void A_maximised_game_keeps_the_screen()
    {
        var fixture = new DeskFixture();
        fixture.Platform.WindowList.Add(FakePlatform.Window(
            1, "game", frame: FakePlatform.SecondMonitor().Bounds, monitor: new MonitorHandle(2), maximized: true, resizable: false));
        fixture.Sync();
        fixture.Turn();
        fixture.Open(2);
        fixture.Turn();

        fixture.Desk.MoveToWorkspace(W(2), "21");
        Redraw redraw = fixture.Turn();

        Assert.Empty(redraw.Unmaximize);
        Assert.Equal(WindowState.Fullscreen, fixture.Managed(1)!.State);
    }

    [Fact]
    public void Tiling_a_maximised_window_by_chord_unmaximises_it_first()
    {
        DeskFixture fixture = BraveMaximisedOnTheSecondMonitor();

        Assert.True(fixture.Desk.SetFloating(W(1), false));
        Redraw redraw = fixture.Turn();

        Assert.Contains(W(1), redraw.Unmaximize);
        Assert.Equal(WindowState.Tiling, fixture.Managed(1)!.State);

        // The maximised rectangle is still what Windows reports for a moment.
        fixture.Platform.WindowList[0] = fixture.Platform.WindowList[0] with { IsMaximized = true };
        fixture.Sync();
        Assert.Equal(WindowState.Tiling, fixture.Managed(1)!.State);
    }

    [Fact]
    public void A_system_dpi_window_floated_is_placed_once_not_on_every_pass()
    {
        var fixture = new DeskFixture();
        fixture.Platform.WindowList.Add(FakePlatform.Window(1, "notepad++", perMonitorDpi: false));
        fixture.Sync();
        fixture.Turn();
        fixture.Desk.SetFloating(W(1), true);
        fixture.Turn();

        // Alone on a screen, its floating rectangle meets the screen's edge:
        // pulled in once, then left alone.
        fixture.Desk.SetFloatingRect(W(1), FakePlatform.MainMonitor().WorkArea);
        fixture.Turn();
        Assert.Empty(fixture.Turn().Place);
        Assert.Empty(fixture.Turn().Place);
    }
}

/// <summary>What Alt+RButton sends for a floating window (2026-09-22).</summary>
public class FloatingResizeByPixelsTests
{
    private static WindowHandle W(long handle) => new(handle);

    [Fact]
    public void A_pixel_resize_of_a_floating_window_changes_both_sides_by_that_much()
    {
        var fixture = new DeskFixture();
        fixture.Open(1, resizable: false, frame: new Rect(3848, -155, 1424, 1997), monitor: new MonitorHandle(2));
        fixture.Turn();
        Assert.Equal(WindowState.Floating, fixture.Managed(1)!.State);
        var executor = new GlazeExecutor(fixture.Desk, new FakeDeskPlatform());

        ExecResult result = executor.Command($"--id {fixture.Managed(1)!.Id} resize --width -300px --height -200px");
        Redraw redraw = fixture.Turn();

        Assert.True(result.Success, result.Error);
        Placement placement = Assert.Single(redraw.Place, p => p.Window == W(1));
        Assert.Equal(new Rect(3848, -155, 1124, 1797), placement.Frame);
        Assert.Equal(new Rect(3848, -155, 1124, 1797), fixture.FrameOf(1));
    }
}

/// <summary>How a hidden Zen window with 178 tabs got the keyboard, and Hyper+Escape (2026-09-22 11:47).</summary>
public class FocusNeverLeavesTheScreenTests
{
    private static WindowHandle W(long handle) => new(handle);

    [Fact]
    public void The_focus_is_never_handed_to_a_window_on_a_workspace_that_is_put_away()
    {
        var fixture = new DeskFixture();
        fixture.Open(1);
        fixture.Desk.FocusWorkspace("21");
        fixture.Open(2, monitor: new MonitorHandle(2));
        fixture.Turn();
        fixture.Foreground(2);

        // Moved to a workspace of the other monitor that is not on screen,
        // the other monitor's workspace switched, then something raises a
        // hidden window and AkuWM looks for something visible to focus.
        fixture.Desk.MoveToWorkspace(W(2), "12");
        fixture.Turn();
        fixture.Desk.WantFocus(fixture.Desk.FocusWorkspace("13"));
        fixture.Turn();
        Assert.True(fixture.IsHidden(2));

        fixture.Desk.WantFocus(W(2)); // whatever asked for it: it is hidden
        Redraw redraw = fixture.Turn();

        Assert.NotEqual(W(2), redraw.Focus);
        Assert.DoesNotContain("focus 2", fixture.Platform.Calls);
        Assert.NotEqual(W(2), fixture.Desk.Focused);
    }

    [Fact]
    public void A_window_being_shown_in_the_same_pass_may_be_focused()
    {
        var fixture = new DeskFixture();
        fixture.Open(1);
        fixture.Turn();
        fixture.Desk.WantFocus(fixture.Desk.FocusWorkspace("12"));
        fixture.Turn();

        fixture.Desk.WantFocus(fixture.Desk.FocusWorkspace("11"));
        Redraw redraw = fixture.Turn();

        Assert.Equal(W(1), redraw.Focus);
        Assert.Equal(W(1), fixture.Desk.Focused);
    }
}

/// <summary>A floating window in the person's hand is left alone (2026-09-22 12:59).</summary>
public class WindowInMotionTests
{
    private static WindowHandle W(long handle) => new(handle);

    [Fact]
    public void A_floating_window_that_is_still_moving_is_not_placed_until_it_rests()
    {
        var fixture = new DeskFixture();
        fixture.Wait(5000);
        fixture.Platform.WindowList.Add(FakePlatform.Window(1, "notepad++", frame: new Rect(100, 100, 1908, 1244), resizable: false, perMonitorDpi: false));
        fixture.Sync();
        fixture.Turn();
        Assert.Equal(WindowState.Floating, fixture.Managed(1)!.State);

        // Dragged right at mouse rate; at the seam Windows blows it up.
        for (int x = 200; x <= 1800; x += 200)
        {
            fixture.Wait(16);
            fixture.Move(1, new Rect(x, 100, 1908, 1244));
            Assert.Empty(fixture.Turn().Place);
        }

        fixture.Wait(16);
        fixture.Move(1, new Rect(1998, 183, 3843, 1495));
        Redraw redraw = fixture.Turn();

        Assert.Empty(redraw.Place);
        Assert.True(fixture.Desk.Unsettled);

        // At rest: one placement, inside the screen.
        fixture.Wait(Desk.SettleMs + 1);
        Redraw settled = fixture.Turn();
        Placement placement = Assert.Single(settled.Place, p => p.Window == W(1));
        Assert.True(placement.Frame.Right <= 3840, $"{placement.Frame}");
        Assert.False(fixture.Desk.Unsettled);
    }

    [Fact]
    public void A_window_dragged_against_the_top_edge_is_moved_down_not_cut()
    {
        var fixture = new DeskFixture();
        fixture.Wait(5000);
        fixture.Platform.WindowList.Add(FakePlatform.Window(1, "notepad++", frame: new Rect(768, -72, 2200, 1623), resizable: false, perMonitorDpi: false));
        fixture.Sync();
        fixture.Wait(Desk.SettleMs + 1);
        Redraw redraw = fixture.Turn();

        Placement placement = Assert.Single(redraw.Place, p => p.Window == W(1));
        Assert.Equal(1623, placement.Frame.Height);
        Assert.Equal(9, placement.Frame.Top); // the fake's border is 9 px, kept on this screen
    }
}

/// <summary>The size Windows blows a system-DPI window up to is not the person's (2026-09-22 13:11).</summary>
public class BlownUpWindowTests
{
    private static WindowHandle W(long handle) => new(handle);

    [Fact]
    public void A_window_blown_up_at_the_seam_comes_back_to_the_size_it_had()
    {
        var fixture = new DeskFixture();
        fixture.Wait(5000);
        fixture.Platform.WindowList.Add(FakePlatform.Window(1, "notepad++", frame: new Rect(100, 100, 1908, 1244), resizable: false, perMonitorDpi: false));
        fixture.Sync();
        fixture.Turn();

        for (int x = 165; x <= 2700; x += 65)
        {
            fixture.Wait(16);
            fixture.Move(1, new Rect(x, 100, 1908, 1244));
            fixture.Turn();
        }

        // Its border touched the vertical monitor: one observation, 1908 to 3318
        // wide -- and a few pixels more on every tick of the drag after that.
        fixture.Wait(16);
        fixture.Move(1, new Rect(1290, 100, 3318, 1244));
        fixture.Turn();
        for (int w = 3331; w <= 3370; w += 13)
        {
            fixture.Wait(16);
            fixture.Move(1, new Rect(1290, 100, w, 1244));
            Assert.Empty(fixture.Turn().Place);
        }

        fixture.Wait(Desk.SettleMs + 1);
        Redraw redraw = fixture.Turn();

        Placement placement = Assert.Single(redraw.Place, p => p.Window == W(1));
        Assert.Equal(1908, placement.Frame.Width);
        Assert.Equal(1244, placement.Frame.Height);
    }

    [Fact]
    public void A_big_shrink_in_one_step_on_one_screen_is_a_command_not_a_blow_up()
    {
        var fixture = new DeskFixture();
        fixture.Wait(5000);
        fixture.Platform.WindowList.Add(FakePlatform.Window(1, "notepad++", frame: new Rect(9, 42, 3822, 2109), resizable: false, perMonitorDpi: false));
        fixture.Sync();
        fixture.Turn();

        // A script sets it to 1908x1244 in one call, on the main screen.
        fixture.Wait(16);
        fixture.Move(1, new Rect(100, 100, 1908, 1244));
        fixture.Turn();
        fixture.Wait(Desk.SettleMs + 1);
        fixture.Turn();

        Assert.Equal(1908, fixture.FrameOf(1).Width);
        Assert.Null(fixture.Managed(1)!.SteadySize);
    }

    [Fact]
    public void A_hand_resizing_it_a_few_pixels_a_tick_is_still_the_hand()
    {
        var fixture = new DeskFixture();
        fixture.Wait(5000);
        fixture.Platform.WindowList.Add(FakePlatform.Window(1, "notepad++", frame: new Rect(100, 100, 1908, 1244), resizable: false, perMonitorDpi: false));
        fixture.Sync();
        fixture.Turn();

        for (int w = 1880; w >= 1500; w -= 20)
        {
            fixture.Wait(16);
            fixture.Move(1, new Rect(100, 100, w, 1244));
            fixture.Turn();
        }

        fixture.Wait(Desk.SettleMs + 1);
        fixture.Turn();

        Assert.Equal(1500, fixture.FrameOf(1).Width);
        Assert.Equal(1500, fixture.Managed(1)!.FloatingRect!.Value.Width);
    }
}

/// <summary>A drag that passes where AkuWM last put the window is still the drag (2026-09-22 13:17).</summary>
public class DragThroughTheOldPlaceTests
{
    private static WindowHandle W(long handle) => new(handle);

    [Fact]
    public void Passing_near_the_last_placement_mid_drag_does_not_send_the_window_back()
    {
        var fixture = new DeskFixture();
        fixture.Wait(5000);
        fixture.Open(1, resizable: false, frame: new Rect(1298, 100, 1890, 1235));
        fixture.Turn(); // placed at 1298,100 and landed

        Rect at = new(1879, 100, 1890, 1235);
        fixture.Move(1, at);
        fixture.Turn();
        for (int x = 1814; x >= 1034; x -= 65)
        {
            fixture.Wait(16);
            at = at with { X = x };
            fixture.Move(1, at);
            Redraw redraw = fixture.Turn();
            Assert.Empty(redraw.Place);
            Assert.Equal(x, fixture.FrameOf(1).X);
        }
    }
}
