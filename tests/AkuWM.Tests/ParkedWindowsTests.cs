using AkuWM.Core.Config;
using AkuWM.Core.Desk;
using AkuWM.Core.Model;
using Xunit;

namespace AkuWM.Tests;

/// <summary>
/// The monitors went to sleep and came back (2026-09-22 17:41 → 18:06): the
/// main screen vanished, the vertical one became primary, Windows added a
/// 1024x768 "Default_Monitor", parked every window of the lost screen in the
/// taskbar, and on the way back showed the vertical screen LANDSCAPE for a
/// second and the main screen with no EDID for another. Diego found every
/// window gone from its workspace. Three things, each pinned here.
/// </summary>
public class ParkedWindowsTests
{
    private static WindowHandle W(long handle) => new(handle);

    private static readonly MonitorSnapshot Ghost = FakePlatform.MainMonitor(handle: 9) with
    {
        DeviceName = @"\\.\DISPLAY9",
        FriendlyName = "Default_Monitor",
        HardwareId = "Default_Monitor",
        Bounds = new Rect(1440, 0, 1024, 768),
        WorkArea = new Rect(1440, 28, 1024, 740),
        Dpi = 96,
        IsPrimary = false,
    };

    /// <summary>Windows parks a window: minimised, at the far corner.</summary>
    private static void WindowsParks(DeskFixture f, long handle)
    {
        f.Platform.SetMinimized(W(handle), true);
        f.Move(handle, new Rect(-32000, -32000, 237, 39));
    }

    [Fact]
    public void A_role_that_names_its_screen_never_lands_on_a_ghost()
    {
        List<MonitorConfig> configured =
        [
            new MonitorConfig { Id = "main", Match = new MonitorMatch { Edid = "SAM7233" } },
            new MonitorConfig { Id = "second", Match = new MonitorMatch { Edid = "NSL2711" } },
        ];

        Dictionary<MonitorHandle, string> roles = MonitorRoles.Resolve(
            configured, [FakePlatform.SecondMonitor(handle: 2) with { IsPrimary = true }, Ghost]);

        Assert.Equal("second", Assert.Single(roles).Value);
        Assert.False(roles.ContainsKey(Ghost.Handle));
    }

    [Fact]
    public void A_role_without_an_identity_still_falls_back_to_what_is_there()
    {
        List<MonitorConfig> configured =
        [
            new MonitorConfig { Id = "main", Match = new MonitorMatch { Edid = "SAM7233" } },
            new MonitorConfig { Id = "second" },
        ];

        Dictionary<MonitorHandle, string> roles = MonitorRoles.Resolve(configured, [Ghost]);
        Assert.Equal("second", roles[Ghost.Handle]);
    }

    [Fact]
    public void The_main_screens_windows_are_not_laid_out_on_the_ghost_while_it_is_away()
    {
        // Diego's policy: nothing moves when a screen goes. With move_windows
        // they would be lent to the vertical screen, which is also not the ghost.
        AkuWmConfig config = DeskFixture.Configuration();
        config.Layout!.WhenMonitorLeaves = "leave";
        var f = new DeskFixture(config);
        f.Open(1);
        f.Open(2);
        f.Turn();
        Rect a = f.FrameOf(1);

        f.Platform.MonitorList.RemoveAll(m => m.Handle.Value == 1);
        f.Platform.MonitorList[0] = f.Platform.MonitorList[0] with { IsPrimary = true };
        f.Platform.MonitorList.Add(Ghost);
        f.Screens();
        Redraw redraw = f.Turn();

        Assert.DoesNotContain(redraw.Place, p => p.Window == W(1));
        Assert.DoesNotContain(redraw.Place, p => p.Window == W(2));
        Assert.Equal(a, f.FrameOf(1));
        Assert.DoesNotContain(f.Desk.Monitors, m => m.Role == "main");
    }

    [Fact]
    public void A_window_parked_by_windows_comes_back_where_it_was_once_the_screens_settle()
    {
        var f = new DeskFixture();
        f.Open(1);
        f.Open(2);
        f.Turn();
        Rect a = f.FrameOf(1);
        Rect b = f.FrameOf(2);

        // The main screen re-configures (same handle, new shape for a
        // moment) and Windows parks its windows a second later.
        MonitorSnapshot main = f.Platform.MonitorList[0];
        f.Platform.MonitorList[0] = main with { Bounds = new Rect(0, 0, 2560, 1440), WorkArea = new Rect(0, 42, 2560, 1398) };
        f.Desk.SetMonitors(f.Platform.Monitors());
        f.Wait(1000);
        WindowsParks(f, 1);
        WindowsParks(f, 2);
        Assert.True(f.Managed(1)!.Parked);
        Assert.Equal(WindowState.Minimized, f.Managed(1)!.State);
        Assert.Empty(f.Turn().Restore);

        f.Platform.MonitorList[0] = main;
        f.Desk.SetMonitors(f.Platform.Monitors());
        f.Wait(AkuWM.Core.Desk.Desk.MonitorSettleMs);
        Redraw redraw = f.Turn();

        Assert.Contains(W(1), redraw.Restore);
        Assert.Contains(W(2), redraw.Restore);
        f.Turn();
        f.Turn();
        Assert.Equal(WindowState.Tiling, f.Managed(1)!.State);
        Assert.Equal("11", f.Managed(1)!.Workspace);
        Assert.Equal(a, f.FrameOf(1));
        Assert.Equal(b, f.FrameOf(2));
    }

    [Fact]
    public void A_window_the_person_minimised_stays_minimised()
    {
        var f = new DeskFixture();
        f.Open(1);
        f.Turn();
        f.Wait(AkuWM.Core.Desk.Desk.ParkWindowMs + 1);

        f.Platform.SetMinimized(W(1), true);
        f.Move(1, new Rect(-32000, -32000, 237, 39));
        Assert.False(f.Managed(1)!.Parked);
        f.Wait(AkuWM.Core.Desk.Desk.MonitorSettleMs);
        Assert.Empty(f.Turn().Restore);
        Assert.Equal(WindowState.Minimized, f.Managed(1)!.State);
    }

    [Fact]
    public void A_parked_window_of_a_screen_that_is_away_waits_for_it_and_comes_back_on_it()
    {
        AkuWmConfig config = DeskFixture.Configuration();
        config.Layout!.WhenMonitorLeaves = "leave";
        var f = new DeskFixture(config);
        f.Open(1);
        f.Desk.FocusWorkspace("21");
        f.Open(2, monitor: new MonitorHandle(2), frame: new Rect(3900, 0, 800, 600));
        f.Turn();
        Rect onSecond = f.FrameOf(2);

        f.Platform.MonitorList.RemoveAll(m => m.Handle.Value == 2);
        f.Desk.SetMonitors(f.Platform.Monitors());
        f.Wait(500);
        WindowsParks(f, 2);
        f.Wait(AkuWM.Core.Desk.Desk.MonitorSettleMs);
        Assert.Empty(f.Turn().Restore);
        Assert.True(f.Managed(2)!.Parked);

        f.Platform.MonitorList.Add(FakePlatform.SecondMonitor());
        f.Screens();
        Redraw redraw = f.Turn();
        Assert.Contains(W(2), redraw.Restore);
        f.Turn();
        f.Turn();
        Assert.Equal("21", f.Managed(2)!.Workspace);
        Assert.Equal(onSecond, f.FrameOf(2));
    }

    [Fact]
    public void A_screen_that_changed_shape_waits_longer_than_a_bar_that_moved()
    {
        var f = new DeskFixture();
        f.Open(1);
        f.Turn();

        MonitorSnapshot main = f.Platform.MonitorList[0];
        f.Platform.MonitorList[0] = main with { Bounds = new Rect(0, 0, 2560, 1440), WorkArea = new Rect(0, 42, 2560, 1398) };
        f.Desk.SetMonitors(f.Platform.Monitors());
        f.Wait(AkuWM.Core.Desk.Desk.ScreenSettleMs);
        Assert.Empty(f.Turn().Place);
        Assert.True(f.Desk.Unsettled);

        f.Wait(AkuWM.Core.Desk.Desk.MonitorSettleMs - AkuWM.Core.Desk.Desk.ScreenSettleMs);
        Assert.Single(f.Turn().Place, p => p.Window == W(1));
    }
}
