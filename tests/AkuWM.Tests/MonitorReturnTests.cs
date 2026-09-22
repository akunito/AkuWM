using AkuWM.Core.Config;
using AkuWM.Core.Desk;
using AkuWM.Core.Model;
using Xunit;

namespace AkuWM.Tests;

/// <summary>
/// What Windows itself does around a monitor that is disabled and enabled
/// again (Settings > Display, 2026-09-22, "Minimize windows when a monitor
/// is disconnected" and "Remember window locations" both on, as they are by
/// default): every window of that screen is MINIMISED when it goes, restored
/// at its remembered rectangle when it is back -- a maximised one at the
/// monitor's full bounds, because the bar has not reserved its strip yet --
/// then minimised again by the second phase of the display change. The
/// person then clicks its taskbar button and gets it back un-maximised, a
/// little smaller than the screen. Traces of Brave, 15:37-15:38.
/// </summary>
public class MonitorReturnTests
{
    private static WindowHandle W(long handle) => new(handle);

    private static readonly Rect SecondBounds = FakePlatform.SecondMonitor().Bounds;
    private static readonly Rect SecondWork = FakePlatform.SecondMonitor().WorkArea;

    private static DeskFixture Desk(string leaves = "leave")
    {
        AkuWmConfig config = DeskFixture.Configuration();
        config.Layout!.WhenMonitorLeaves = leaves;
        config.Layout.WhenMonitorReturns = "restore";
        var f = new DeskFixture(config);
        f.Desk.FocusWorkspace("21");
        return f;
    }

    /// <summary>Windows parks the window: minimised, at the far corner.</summary>
    private static void WindowsMinimises(DeskFixture f, long handle)
    {
        f.Platform.SetMinimized(W(handle), true);
        f.Move(handle, new Rect(-32000, -32000, 237, 39));
    }

    private static void SecondGoes(DeskFixture f)
    {
        f.Platform.MonitorList.RemoveAll(m => m.Handle.Value == 2);
        f.Desk.SetMonitors(f.Platform.Monitors());
        f.Turn();
        f.Turn();
    }

    private static void SecondReturns(DeskFixture f)
    {
        f.Platform.MonitorList.Add(FakePlatform.SecondMonitor());
        f.Desk.SetMonitors(f.Platform.Monitors());
        f.Turn();
        f.Turn();
    }

    [Fact]
    public void A_maximised_window_that_windows_parks_and_hands_back_smaller_is_a_tile_again_not_a_game()
    {
        DeskFixture f = Desk();
        f.Open(1, process: "brave", monitor: new MonitorHandle(2), frame: new Rect(3900, 0, 800, 600));
        f.Turn();
        // The person maximises it: fullscreen by design, unmarked (it stops at the bar).
        f.Platform.SetMaximized(W(1), true);
        f.Move(1, SecondWork);
        f.Turn();
        Assert.Equal(WindowState.Fullscreen, f.Managed(1)!.State);

        WindowsMinimises(f, 1);
        SecondGoes(f);
        Assert.Equal(WindowState.Minimized, f.Managed(1)!.State);

        // Back: restored maximised over the whole bounds (no bar yet), then
        // parked again by the second phase.
        f.Platform.MonitorList.Add(FakePlatform.SecondMonitor());
        f.Desk.SetMonitors(f.Platform.Monitors());
        f.Platform.SetMaximized(W(1), true);
        f.Move(1, SecondBounds);
        f.Turn();
        WindowsMinimises(f, 1);
        f.Turn();

        // The person clicks its taskbar button: un-maximised, 16 x 44 short.
        f.Platform.SetMinimized(W(1), false);
        f.Move(1, new Rect(SecondBounds.X + 8, SecondBounds.Y + 9, SecondBounds.Width - 16, SecondBounds.Height - 44));
        Redraw redraw = f.Turn();
        // A window that has just moved is left to settle before it is placed.
        f.Wait(AkuWM.Core.Desk.Desk.SettleMs + 1);
        f.Turn();
        f.Turn();

        DeskWindow brave = f.Managed(1)!;
        Assert.Equal(WindowState.Tiling, brave.State);
        Assert.True(f.Desk.Workspace("21")!.Fullscreen.IsNone, "nothing covers the workspace");
        Assert.DoesNotContain(redraw.TaskbarMark, m => m.Item1 == W(1) && m.Item2);
        Assert.False(brave.Marked);
        Assert.Equal(SecondWork, f.FrameOf(1));
        // And it can be floated and moved like any other window.
        Assert.True(f.Desk.SetFloating(W(1), true));
        f.Turn();
        f.Move(1, new Rect(3900, 100, 900, 700));
        f.Wait(AkuWM.Core.Desk.Desk.SettleMs + 1);
        f.Turn();
        f.Turn();
        Assert.Equal(new Rect(3900, 100, 900, 700), f.FrameOf(1));
    }

    [Fact]
    public void A_game_parked_and_handed_back_whole_is_still_fullscreen()
    {
        DeskFixture f = Desk();
        f.Open(1, process: "game", monitor: new MonitorHandle(2), frame: new Rect(3900, 0, 800, 600), resizable: false);
        f.Turn();
        f.Move(1, SecondBounds);
        f.Turn();
        Assert.Equal(WindowState.Fullscreen, f.Managed(1)!.State);

        WindowsMinimises(f, 1);
        f.Turn();
        f.Platform.SetMinimized(W(1), false);
        f.Move(1, SecondBounds);
        f.Turn();
        f.Turn();

        Assert.Equal(WindowState.Fullscreen, f.Managed(1)!.State);
        Assert.Equal(W(1), f.Desk.Workspace("21")!.Fullscreen);
        Assert.True(f.Managed(1)!.Marked);
    }

    [Fact]
    public void With_leave_a_parked_window_restored_from_the_taskbar_while_its_screen_is_away_shows_on_the_screen_that_is_here()
    {
        DeskFixture f = Desk("leave");
        f.Open(1, monitor: new MonitorHandle(2), frame: new Rect(3900, 0, 800, 600));
        f.Open(2, monitor: new MonitorHandle(2), frame: new Rect(3900, 700, 800, 600));
        f.Desk.FocusWorkspace("11");
        f.Open(3);
        f.Turn();

        WindowsMinimises(f, 1);
        WindowsMinimises(f, 2);
        SecondGoes(f);
        Assert.True(f.Desk.OnLoan.Count == 0, "leave: nothing lent");

        // The taskbar click: Windows restores it on the primary screen.
        f.Platform.SetMinimized(W(1), false);
        f.Move(1, new Rect(100, 100, 800, 600));
        f.Turn();
        f.Turn();

        Assert.False(f.IsHidden(1), "the click has to show something");
        Assert.True(f.FrameOf(1).FractionInside(FakePlatform.MainMonitor().WorkArea) > 0.99, $"{f.FrameOf(1)}");
        Assert.Equal(WindowState.Tiling, f.Managed(1)!.State);
        // The other parked one stays parked: one click, one window.
        Assert.Equal(WindowState.Minimized, f.Managed(2)!.State);

        // The screen is back: the window goes home (restore).
        SecondReturns(f);
        Assert.Equal("21", f.Managed(1)!.Workspace);
        Assert.True(f.FrameOf(1).FractionInside(SecondWork) > 0.99, $"{f.FrameOf(1)}");
    }

    [Fact]
    public void Fetch_windows_brings_the_parked_windows_of_the_away_screen_back_from_the_taskbar()
    {
        DeskFixture f = Desk("leave");
        f.Open(1, monitor: new MonitorHandle(2), frame: new Rect(3900, 0, 800, 600));
        f.Open(2, monitor: new MonitorHandle(2), frame: new Rect(3900, 700, 800, 600));
        f.Desk.FocusWorkspace("11");
        f.Open(3);
        f.Turn();

        WindowsMinimises(f, 1);
        WindowsMinimises(f, 2);
        SecondGoes(f);

        Assert.True(f.Desk.ToggleFetch());
        Assert.True(f.Managed(1)!.State == WindowState.Tiling, $"state {f.Managed(1)!.State} ws {f.Managed(1)!.Workspace} managed {f.Managed(1)!.Managed}");
        Redraw redraw = f.Turn();
        Assert.Contains(W(1), redraw.Restore);
        Assert.Contains(W(2), redraw.Restore);
        f.Turn();
        f.Turn();

        Assert.False(f.IsHidden(1));
        Assert.False(f.IsHidden(2));
        Assert.True(f.FrameOf(1).FractionInside(FakePlatform.MainMonitor().WorkArea) > 0.99, $"{f.FrameOf(1)}");
        Assert.True(f.FrameOf(2).FractionInside(FakePlatform.MainMonitor().WorkArea) > 0.99, $"{f.FrameOf(2)}");
        Assert.Equal(WindowState.Tiling, f.Managed(1)!.State);
    }
}
