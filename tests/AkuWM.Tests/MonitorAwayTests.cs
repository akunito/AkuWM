using AkuWM.Core.Compat;
using AkuWM.Core.Config;
using AkuWM.Core.Desk;
using AkuWM.Core.Layout;
using AkuWM.Core.Model;
using Xunit;

namespace AkuWM.Tests;

/// <summary>
/// A monitor goes to sleep and comes back (Diego, 2026-09-22): what its
/// windows do meanwhile, by configuration, and how exactly they come back.
/// </summary>
public class MonitorAwayTests
{
    private static WindowHandle W(long handle) => new(handle);

    private static DeskFixture Desk(string leaves, string returns)
    {
        AkuWmConfig config = DeskFixture.Configuration();
        config.Layout!.WhenMonitorLeaves = leaves;
        config.Layout.WhenMonitorReturns = returns;
        return new DeskFixture(config);
    }

    /// <summary>Two tiled, one floating and one sticky on the second monitor; one tiled on the first.</summary>
    private static DeskFixture ArrangedOnBoth(string leaves, string returns)
    {
        DeskFixture f = Desk(leaves, returns);
        f.Open(1);
        f.Desk.FocusWorkspace("21");
        f.Open(2, monitor: new MonitorHandle(2), frame: new Rect(3900, 0, 800, 600));
        f.Open(3, monitor: new MonitorHandle(2), frame: new Rect(3900, 700, 800, 600));
        f.Open(4, monitor: new MonitorHandle(2), frame: new Rect(4000, 100, 640, 480), resizable: false);
        f.Open(5, monitor: new MonitorHandle(2), frame: new Rect(4100, 900, 500, 400), resizable: false);
        f.Turn();
        f.Desk.SetSticky(W(5), true);
        f.Desk.Resize(W(2), Direction.Down, 20); // a share the person chose
        f.Turn();
        f.Turn();
        f.Desk.FocusWorkspace("11");
        f.Turn();
        return f;
    }

    private static void SecondGoesAway(DeskFixture f)
    {
        f.Platform.MonitorList.RemoveAll(m => m.Handle.Value == 2);
        f.Desk.SetMonitors(f.Platform.Monitors());
        f.Turn();
        f.Turn();
    }

    private static void SecondComesBack(DeskFixture f)
    {
        f.Platform.MonitorList.Add(FakePlatform.SecondMonitor());
        f.Desk.SetMonitors(f.Platform.Monitors());
        f.Turn();
        f.Turn();
    }

    // ---- move_windows -------------------------------------------------------

    [Fact]
    public void When_the_second_monitor_goes_its_windows_join_the_workspace_on_screen()
    {
        DeskFixture f = ArrangedOnBoth("move_windows", "restore");
        Rect floatingBefore = f.Managed(4)!.FloatingRect!.Value;

        SecondGoesAway(f);

        Assert.Equal("11", f.Managed(2)!.Workspace);
        Assert.Equal("11", f.Managed(3)!.Workspace);
        Assert.Equal("11", f.Managed(4)!.Workspace);
        Assert.Equal(WindowState.Tiling, f.Managed(2)!.State);
        Assert.Equal(WindowState.Floating, f.Managed(4)!.State);
        Assert.Equal("main", f.Managed(5)!.StickyMonitor);
        Assert.Contains("second", f.Desk.OnLoan);

        // Visible, on the first screen, all of them.
        Rect main = FakePlatform.MainMonitor().WorkArea;
        foreach (long h in new long[] { 1, 2, 3, 4, 5 })
        {
            Assert.False(f.IsHidden(h), $"{h} is hidden");
            Assert.True(f.FrameOf(h).FractionInside(main) > 0.99, $"{h} at {f.FrameOf(h)}");
        }

        Assert.NotEqual(floatingBefore, f.Managed(4)!.FloatingRect);
    }

    [Fact]
    public void And_comes_back_exactly_as_it_was_whatever_happened_meanwhile()
    {
        DeskFixture f = ArrangedOnBoth("move_windows", "restore");
        Rect floating4 = f.Managed(4)!.FloatingRect!.Value;
        Rect sticky5 = f.Managed(5)!.FloatingRect!.Value;
        Rect tile2 = f.FrameOf(2);
        Rect tile3 = f.FrameOf(3);

        SecondGoesAway(f);

        // Meanwhile the person drags the floating one and resizes a tile.
        f.Move(4, new Rect(200, 200, 640, 480));
        f.Turn();
        f.Desk.Resize(W(2), Direction.Right, 10);
        f.Turn();

        SecondComesBack(f);

        Assert.Equal("21", f.Managed(2)!.Workspace);
        Assert.Equal("21", f.Managed(3)!.Workspace);
        Assert.Equal("21", f.Managed(4)!.Workspace);
        Assert.Equal("11", f.Managed(1)!.Workspace);
        Assert.Equal("second", f.Managed(5)!.StickyMonitor);
        Assert.Empty(f.Desk.OnLoan);

        Assert.Equal(floating4, f.Managed(4)!.FloatingRect);
        Assert.Equal(sticky5, f.Managed(5)!.FloatingRect);
        Assert.Equal(tile2, f.FrameOf(2));
        Assert.Equal(tile3, f.FrameOf(3));
        Assert.Equal(floating4, f.FrameOf(4));
        Assert.True(f.Desk.Workspace("21")!.Displayed);
        Assert.True(f.Desk.Workspace("11")!.Displayed);
    }

    [Fact]
    public void The_dpi_rescale_that_follows_the_return_is_undone_and_the_size_stands()
    {
        DeskFixture f = ArrangedOnBoth("move_windows", "restore");
        f.Wait(5000);
        Rect floating4 = f.Managed(4)!.FloatingRect!.Value;
        Rect sticky5 = f.Managed(5)!.FloatingRect!.Value;
        SecondGoesAway(f);
        SecondComesBack(f);

        // 140 ms later Windows scales both for the 125 % screen (live: 645x481
        // came back as 581x400, 2026-09-22 14:59).
        f.Wait(140);
        f.Platform.ApplicationMoves(W(4), floating4 with { Width = floating4.Width * 5 / 6, Height = floating4.Height * 5 / 6 });
        f.Platform.ApplicationMoves(W(5), sticky5 with { Width = sticky5.Width * 5 / 6, Height = sticky5.Height * 5 / 6 });
        f.Sync();
        f.Turn();
        f.Wait(Desk.SettleMs + 1);
        f.Turn();

        Assert.Equal(floating4, f.FrameOf(4));
        Assert.Equal(sticky5, f.FrameOf(5));
    }

    [Fact]
    public void Keep_leaves_the_windows_where_they_are_now()
    {
        DeskFixture f = ArrangedOnBoth("move_windows", "keep");
        SecondGoesAway(f);
        f.Move(4, new Rect(200, 200, 640, 480));
        f.Turn();

        SecondComesBack(f);

        Assert.Equal("11", f.Managed(2)!.Workspace);
        Assert.Equal("11", f.Managed(4)!.Workspace);
        Assert.Equal(new Rect(200, 200, 640, 480), f.FrameOf(4));
        Assert.True(f.Desk.Workspace("21")!.IsEmpty);
        Assert.Empty(f.Desk.OnLoan);

        // The sticky one follows the screen it belongs to, at the rectangle it has.
        Assert.Equal("second", f.Managed(5)!.StickyMonitor);
        Assert.True(f.FrameOf(5).FractionInside(FakePlatform.SecondMonitor().WorkArea) > 0.99, $"{f.FrameOf(5)}");
    }

    [Fact]
    public void A_window_moved_on_purpose_meanwhile_is_not_taken_back()
    {
        DeskFixture f = ArrangedOnBoth("move_windows", "restore");
        SecondGoesAway(f);
        f.Desk.MoveToWorkspace(W(3), "13");
        f.Turn();

        SecondComesBack(f);

        Assert.Equal("13", f.Managed(3)!.Workspace);
        Assert.Equal("21", f.Managed(2)!.Workspace);
        Assert.DoesNotContain(W(3), f.Desk.Workspace("21")!.Windows);
    }

    [Fact]
    public void A_window_closed_meanwhile_is_not_in_the_restored_layout()
    {
        DeskFixture f = ArrangedOnBoth("move_windows", "restore");
        SecondGoesAway(f);
        f.Close(3);
        f.Turn();

        SecondComesBack(f);

        Assert.DoesNotContain(W(3), f.Desk.Workspace("21")!.Windows);
        Assert.Equal("21", f.Managed(2)!.Workspace);
        Assert.Equal(FakePlatform.SecondMonitor().WorkArea, f.FrameOf(2)); // alone in the tree now
    }

    [Fact]
    public void A_fullscreen_window_floats_while_away_and_covers_the_screen_again_when_back()
    {
        DeskFixture f = Desk("move_windows", "restore");
        f.Desk.FocusWorkspace("21");
        f.Open(1, monitor: new MonitorHandle(2), frame: new Rect(3900, 0, 800, 600));
        f.Turn();
        f.Desk.SetFullscreen(W(1), true);
        f.Turn();

        SecondGoesAway(f);
        Assert.Equal(WindowState.Floating, f.Managed(1)!.State);
        Assert.True(f.Desk.Workspace("11")!.Fullscreen.IsNone);

        SecondComesBack(f);
        Assert.Equal(WindowState.Fullscreen, f.Managed(1)!.State);
        Assert.Equal(W(1), f.Desk.Workspace("21")!.Fullscreen);
    }

    // ---- move_workspaces ----------------------------------------------------

    [Fact]
    public void When_the_second_monitor_goes_its_workspaces_move_to_the_first_put_away()
    {
        DeskFixture f = ArrangedOnBoth("move_workspaces", "restore");
        SecondGoesAway(f);

        Assert.Equal("main", f.Desk.Workspace("21")!.MonitorRole);
        Assert.Contains(f.Desk.Workspace("21"), f.Desk.MonitorByRole("main")!.Workspaces);
        Assert.False(f.Desk.Workspace("21")!.Displayed);
        Assert.Equal("21", f.Managed(2)!.Workspace);
        Assert.True(f.IsHidden(2), "put away on the host until asked for");

        // Asked for by the workspace keys, it shows on the first screen.
        f.Desk.WantFocus(f.Desk.FocusWorkspace("21"));
        f.Turn();
        f.Turn();
        Assert.False(f.IsHidden(2));
        Assert.True(f.FrameOf(2).FractionInside(FakePlatform.MainMonitor().WorkArea) > 0.99, $"{f.FrameOf(2)}");
        Assert.True(f.FrameOf(4).FractionInside(FakePlatform.MainMonitor().WorkArea) > 0.99, $"{f.FrameOf(4)}");
    }

    [Fact]
    public void Workspaces_go_back_with_everything_where_it_was()
    {
        DeskFixture f = ArrangedOnBoth("move_workspaces", "restore");
        Rect floating4 = f.Managed(4)!.FloatingRect!.Value;
        SecondGoesAway(f);
        f.Desk.WantFocus(f.Desk.FocusWorkspace("21"));
        f.Turn();
        f.Move(4, new Rect(200, 200, 640, 480));
        f.Turn();

        SecondComesBack(f);

        Assert.Equal("second", f.Desk.Workspace("21")!.MonitorRole);
        Assert.True(f.Desk.Workspace("21")!.Displayed);
        Assert.Equal(floating4, f.Managed(4)!.FloatingRect);
        Assert.Equal(floating4, f.FrameOf(4));
        Assert.Equal("second", f.Managed(5)!.StickyMonitor);
        Assert.NotNull(f.Desk.MonitorByRole("main")!.Displayed);
    }

    [Fact]
    public void Workspaces_go_back_and_keep_brings_the_rectangles_along()
    {
        DeskFixture f = ArrangedOnBoth("move_workspaces", "keep");
        SecondGoesAway(f);
        f.Desk.WantFocus(f.Desk.FocusWorkspace("21"));
        f.Turn();
        f.Move(4, new Rect(200, 200, 640, 480));
        f.Turn();

        SecondComesBack(f);

        Assert.Equal("second", f.Desk.Workspace("21")!.MonitorRole);
        Rect now = f.FrameOf(4);
        Assert.True(now.FractionInside(FakePlatform.SecondMonitor().WorkArea) > 0.99, $"{now}");
        Assert.NotEqual(new Rect(4000, 100, 640, 480), now);
    }

    // ---- leave --------------------------------------------------------------

    [Fact]
    public void Leave_only_stops_hiding_them()
    {
        DeskFixture f = ArrangedOnBoth("leave", "restore");
        SecondGoesAway(f);

        Assert.Equal("21", f.Managed(2)!.Workspace);
        Assert.False(f.IsHidden(2));
        Assert.Empty(f.Desk.OnLoan);

        SecondComesBack(f);
        Assert.Equal("21", f.Managed(2)!.Workspace);
        Assert.True(f.Desk.Workspace("21")!.Displayed);
    }

    // ---- edges --------------------------------------------------------------

    [Fact]
    public void Every_screen_gone_lends_nothing_and_nothing_is_lost_when_they_return()
    {
        DeskFixture f = ArrangedOnBoth("move_windows", "restore");
        Rect floating4 = f.Managed(4)!.FloatingRect!.Value;

        f.Platform.MonitorList.Clear();
        f.Desk.SetMonitors(f.Platform.Monitors());
        f.Turn();
        Assert.Empty(f.Desk.OnLoan);

        f.Platform.MonitorList.Add(FakePlatform.MainMonitor());
        f.Platform.MonitorList.Add(FakePlatform.SecondMonitor());
        f.Desk.SetMonitors(f.Platform.Monitors());
        f.Turn();
        f.Turn();

        Assert.Equal("21", f.Managed(2)!.Workspace);
        Assert.Equal(floating4, f.Managed(4)!.FloatingRect);
    }

    [Fact]
    public void The_policy_is_read_at_the_moment_it_is_needed()
    {
        DeskFixture f = ArrangedOnBoth("move_windows", "restore");

        AkuWmConfig config = DeskFixture.Configuration();
        config.Layout!.WhenMonitorLeaves = "leave";
        f.Desk.Reload(config);
        f.Turn();

        SecondGoesAway(f);
        Assert.Empty(f.Desk.OnLoan);
        Assert.Equal("21", f.Managed(2)!.Workspace);
    }

    [Fact]
    public void The_host_going_away_too_lends_the_borrowed_windows_on_and_the_first_return_still_takes_its_own()
    {
        AkuWmConfig config = DeskFixture.Configuration();
        config.Layout!.WhenMonitorLeaves = "move_windows";
        config.Monitors!.Add(new MonitorConfig { Id = "third", Match = new MonitorMatch { Edid = "BNQ7F32" } });
        config.Workspaces!.Add(new WorkspaceConfig { Name = "31", Monitor = "third" });
        var third = new MonitorSnapshot
        {
            Handle = new MonitorHandle(3),
            DeviceName = @"\\.\DISPLAY3",
            FriendlyName = "ZOWIE",
            HardwareId = "BNQ7F32",
            Bounds = new Rect(-1920, -706, 1920, 1080),
            WorkArea = new Rect(-1920, -706, 1920, 1080),
            Dpi = 96,
            IsPrimary = false,
        };
        var f = new DeskFixture(config, FakePlatform.MainMonitor(), FakePlatform.SecondMonitor(), third);
        f.Open(1);
        f.Desk.FocusWorkspace("21");
        f.Open(2, monitor: new MonitorHandle(2), frame: new Rect(3900, 0, 800, 600));
        f.Turn();
        f.Desk.FocusWorkspace("11");
        f.Turn();

        // The second goes: its window is lent to main. Then main goes: the
        // borrowed window is lent on to the third with main's own.
        f.Platform.MonitorList.RemoveAll(m => m.Handle.Value == 2);
        f.Desk.SetMonitors(f.Platform.Monitors());
        f.Turn();
        Assert.Equal("11", f.Managed(2)!.Workspace);

        f.Platform.MonitorList.RemoveAll(m => m.Handle.Value == 1);
        f.Desk.SetMonitors(f.Platform.Monitors());
        f.Turn();
        Assert.Equal("31", f.Managed(2)!.Workspace);
        Assert.Equal("31", f.Managed(1)!.Workspace);

        // The second is back first: its window is on the third now, not where
        // the loan put it, so it stays -- the person may be using it there.
        f.Platform.MonitorList.Insert(1, FakePlatform.SecondMonitor());
        f.Desk.SetMonitors(f.Platform.Monitors());
        f.Turn();
        Assert.Equal("31", f.Managed(2)!.Workspace);

        // Main is back: its own window returns, and the borrowed one with it
        // (it was lent to main's workspace 11 at the time).
        f.Platform.MonitorList.Insert(0, FakePlatform.MainMonitor());
        f.Desk.SetMonitors(f.Platform.Monitors());
        f.Turn();
        Assert.Equal("11", f.Managed(1)!.Workspace);
        Assert.Equal("11", f.Managed(2)!.Workspace);
    }
}

/// <summary>The chord for a screen that is dark but, to Windows, present.</summary>
public class FetchWindowsTests
{
    private static WindowHandle W(long handle) => new(handle);

    private static DeskFixture Arranged()
    {
        AkuWmConfig config = DeskFixture.Configuration();
        config.Layout!.WhenMonitorLeaves = "leave";
        var f = new DeskFixture(config);
        f.Open(1);
        f.Desk.FocusWorkspace("21");
        f.Open(2, monitor: new MonitorHandle(2), frame: new Rect(3900, 0, 800, 600));
        f.Open(3, monitor: new MonitorHandle(2), frame: new Rect(4000, 100, 640, 480), resizable: false);
        f.Turn();
        f.Desk.FocusWorkspace("11");
        f.Turn();
        f.Foreground(1);
        return f;
    }

    [Fact]
    public void Nothing_moves_by_itself_with_the_default_and_the_chord_brings_the_other_screens_windows_here()
    {
        DeskFixture f = Arranged();
        Rect floating3 = f.Managed(3)!.FloatingRect!.Value;

        // The vertical monitor drops out and is listed again two seconds later.
        f.Platform.MonitorList.RemoveAll(m => m.Handle.Value == 2);
        f.Desk.SetMonitors(f.Platform.Monitors());
        f.Turn();
        Assert.Equal("21", f.Managed(2)!.Workspace);
        f.Platform.MonitorList.Add(FakePlatform.SecondMonitor());
        f.Desk.SetMonitors(f.Platform.Monitors());
        f.Turn();
        Assert.Equal("21", f.Managed(2)!.Workspace);
        Assert.Empty(f.Desk.OnLoan);

        // The person, looking at a dark screen, presses the chord.
        var executor = new GlazeExecutor(f.Desk, new FakeDeskPlatform());
        ExecResult fetched = executor.Command("fetch-windows");
        f.Turn();
        f.Turn();

        Assert.True(fetched.Success);
        Assert.True(fetched.Data!["fetched"]!.GetValue<bool>());
        Assert.Equal("11", f.Managed(2)!.Workspace);
        Assert.Equal("11", f.Managed(3)!.Workspace);
        Assert.True(f.FrameOf(3).FractionInside(FakePlatform.MainMonitor().WorkArea) > 0.99, $"{f.FrameOf(3)}");
        Assert.Contains("second", f.Desk.OnLoan);

        // Pressed again: everything back where it was.
        ExecResult returned = executor.Command("fetch-windows");
        f.Turn();
        f.Turn();

        Assert.False(returned.Data!["fetched"]!.GetValue<bool>());
        Assert.Equal("21", f.Managed(2)!.Workspace);
        Assert.Equal(floating3, f.Managed(3)!.FloatingRect);
        Assert.Empty(f.Desk.OnLoan);
    }

    [Fact]
    public void Fetched_windows_also_go_back_when_the_screen_is_replugged()
    {
        DeskFixture f = Arranged();
        var executor = new GlazeExecutor(f.Desk, new FakeDeskPlatform());
        executor.Command("fetch-windows");
        f.Turn();
        Assert.Equal("11", f.Managed(2)!.Workspace);

        f.Platform.MonitorList.RemoveAll(m => m.Handle.Value == 2);
        f.Desk.SetMonitors(f.Platform.Monitors());
        f.Turn();
        f.Platform.MonitorList.Add(FakePlatform.SecondMonitor());
        f.Desk.SetMonitors(f.Platform.Monitors());
        f.Turn();

        Assert.Equal("21", f.Managed(2)!.Workspace);
        Assert.Empty(f.Desk.OnLoan);
    }
}
