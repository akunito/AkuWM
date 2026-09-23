using AkuWM.Core.Desk;
using AkuWM.Core.Model;
using AkuWM.Core.Platform;
using Xunit;

namespace AkuWM.Tests;

/// <summary>
/// The bar (the Windows taskbar, Zebar, any app bar) is never measured by
/// AkuWM: the shell subtracts whatever every app bar reserved and hands the
/// rest out as each monitor's work area (<c>rcWork</c>), and that is the whole
/// tiling area. So a bar on another edge, thicker, on one screen only, or
/// auto-hidden is just a different work area -- and a change on the fly is a
/// <c>SetMonitors</c> with new rectangles. Measured on the desk 2026-09-22: an
/// 80 px app bar registered on the left of the main screen moved the visible
/// tile from x=0 to x=80 within 0.7 s, through the WM_SETTINGCHANGE broadcast,
/// with the 4 s poller never needed.
/// </summary>
public class BarAuditTests
{
    private static WindowHandle W(long handle) => new(handle);

    private static readonly Rect MainBounds = FakePlatform.MainMonitor().Bounds;

    /// <summary>The main screen with the bar on the given edge, this thick.</summary>
    private static MonitorSnapshot MainWithBar(string edge, int thickness) =>
        FakePlatform.MainMonitor() with { WorkArea = BarredWorkArea(MainBounds, edge, thickness) };

    private static Rect BarredWorkArea(Rect bounds, string edge, int thickness) => edge switch
    {
        "top" => Rect.FromEdges(bounds.Left, bounds.Top + thickness, bounds.Right, bounds.Bottom),
        "bottom" => Rect.FromEdges(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom - thickness),
        "left" => Rect.FromEdges(bounds.Left + thickness, bounds.Top, bounds.Right, bounds.Bottom),
        "right" => Rect.FromEdges(bounds.Left, bounds.Top, bounds.Right - thickness, bounds.Bottom),
        "none" => bounds,
        _ => throw new ArgumentOutOfRangeException(nameof(edge)),
    };

    private static void MoveTheBar(DeskFixture f, int monitor, string edge, int thickness)
    {
        int at = f.Platform.MonitorList.FindIndex(m => m.Handle.Value == monitor);
        MonitorSnapshot was = f.Platform.MonitorList[at];
        f.Platform.MonitorList[at] = was with { WorkArea = BarredWorkArea(was.Bounds, edge, thickness) };
        f.Screens();
    }

    private static Placement PlacementOf(Redraw redraw, long handle) =>
        Assert.Single(redraw.Place, p => p.Window == W(handle));

    [Theory]
    [InlineData("top", 42)]
    [InlineData("top", 90)]
    [InlineData("bottom", 60)]
    [InlineData("left", 80)]
    [InlineData("right", 120)]
    [InlineData("none", 0)]
    public void A_single_tile_fills_exactly_the_work_area_whatever_edge_the_bar_is_on(string edge, int thickness)
    {
        var f = new DeskFixture(DeskFixture.Configuration(), MainWithBar(edge, thickness), FakePlatform.SecondMonitor());
        f.Open(1);

        Placement placement = PlacementOf(f.Turn(), 1);

        Assert.Equal(BarredWorkArea(MainBounds, edge, thickness), placement.Frame);
    }

    [Theory]
    [InlineData("bottom", 60)]
    [InlineData("left", 80)]
    [InlineData("right", 120)]
    public void Two_tiles_share_the_work_area_and_neither_touches_the_bar(string edge, int thickness)
    {
        var f = new DeskFixture(DeskFixture.Configuration(), MainWithBar(edge, thickness), FakePlatform.SecondMonitor());
        f.Open(1);
        f.Open(2);

        Redraw redraw = f.Turn();
        Rect a = PlacementOf(redraw, 1).Frame;
        Rect b = PlacementOf(redraw, 2).Frame;
        Rect work = BarredWorkArea(MainBounds, edge, thickness);

        Assert.True(work.Contains(a), $"{a} not inside {work}");
        Assert.True(work.Contains(b), $"{b} not inside {work}");
        Assert.Equal(work.Left, Math.Min(a.Left, b.Left));
        Assert.Equal(work.Right, Math.Max(a.Right, b.Right));
        Assert.Equal(work.Top, Math.Min(a.Top, b.Top));
        Assert.Equal(work.Bottom, Math.Max(a.Bottom, b.Bottom));
        Assert.Equal(Rect.Empty, a.Intersect(b));
    }

    [Fact]
    public void The_bar_moved_to_the_bottom_on_the_fly_re_lays_the_tiles_out_at_once()
    {
        var f = new DeskFixture();
        f.Open(1);
        f.Open(2);
        f.Turn();
        Assert.Equal(FakePlatform.MainMonitor().WorkArea.Top, f.FrameOf(1).Top);

        MoveTheBar(f, 1, "bottom", 60);
        Redraw redraw = f.Turn();

        Rect work = BarredWorkArea(MainBounds, "bottom", 60);
        Rect a = PlacementOf(redraw, 1).Frame;
        Rect b = PlacementOf(redraw, 2).Frame;
        Assert.Equal(work.Top, a.Top);
        Assert.Equal(work.Top, b.Top);
        Assert.Equal(work.Bottom, a.Bottom);
        Assert.Equal(work.Bottom, b.Bottom);
        Assert.Equal(work, Rect.FromEdges(Math.Min(a.Left, b.Left), a.Top, Math.Max(a.Right, b.Right), a.Bottom));
        // Applied: the fake platform now reports the new frames, and the next
        // turn has nothing left to do.
        Assert.Equal(a, f.FrameOf(1));
        Assert.Empty(f.Turn().Place);
    }

    [Fact]
    public void A_bar_that_grew_shortens_the_tiles_and_one_that_hid_lets_them_take_the_whole_screen()
    {
        var f = new DeskFixture();
        f.Open(1);
        f.Turn();

        MoveTheBar(f, 1, "top", 90);
        Assert.Equal(BarredWorkArea(MainBounds, "top", 90), PlacementOf(f.Turn(), 1).Frame);

        MoveTheBar(f, 1, "none", 0);
        Assert.Equal(MainBounds, PlacementOf(f.Turn(), 1).Frame);

        MoveTheBar(f, 1, "top", 42);
        Assert.Equal(FakePlatform.MainMonitor().WorkArea, PlacementOf(f.Turn(), 1).Frame);
    }

    [Fact]
    public void A_bar_on_one_screen_leaves_the_other_screen_alone()
    {
        var f = new DeskFixture();
        f.Open(1);
        f.Desk.FocusWorkspace("21");
        f.Open(2, monitor: new MonitorHandle(2), frame: new Rect(3900, 0, 800, 600));
        f.Turn();
        Rect second = f.FrameOf(2);
        Assert.Equal(FakePlatform.SecondMonitor().WorkArea, second);

        MoveTheBar(f, 1, "left", 80);
        Redraw redraw = f.Turn();

        Assert.Equal(BarredWorkArea(MainBounds, "left", 80), PlacementOf(redraw, 1).Frame);
        Assert.DoesNotContain(redraw.Place, p => p.Window == W(2));
        Assert.Equal(second, f.FrameOf(2));
    }

    [Fact]
    public void The_second_screen_can_have_its_own_bar_at_the_bottom()
    {
        var f = new DeskFixture();
        f.Desk.FocusWorkspace("21");
        f.Open(2, monitor: new MonitorHandle(2), frame: new Rect(3900, 0, 800, 600));
        f.Turn();

        MoveTheBar(f, 2, "bottom", 60);
        Redraw redraw = f.Turn();

        Rect bounds = FakePlatform.SecondMonitor().Bounds;
        Assert.Equal(BarredWorkArea(bounds, "bottom", 60), PlacementOf(redraw, 2).Frame);
    }

    [Fact]
    public void A_floating_window_keeps_its_place_as_a_fraction_of_the_work_area_when_the_bar_moves()
    {
        var f = new DeskFixture();
        f.Open(1, frame: new Rect(1000, 500, 900, 700));
        Assert.True(f.Desk.SetFloating(W(1), true));
        f.Turn();
        Rect before = f.Managed(1)!.FloatingRect!.Value;
        Rect was = FakePlatform.MainMonitor().WorkArea;

        MoveTheBar(f, 1, "left", 80);
        f.Turn();
        Rect now = BarredWorkArea(MainBounds, "left", 80);
        Rect after = f.Managed(1)!.FloatingRect!.Value;

        // Same fraction across, same size; and the bar is not under it.
        Assert.Equal(before.Width, after.Width);
        Assert.Equal(before.Height, after.Height);
        Assert.Equal(now.X + (int)Math.Round((before.X - was.X) * (double)now.Width / was.Width), after.X);
        Assert.True(after.Left >= now.Left, $"{after} is under the bar {now}");
        Assert.True(now.Contains(after), $"{after} left the work area {now}");
    }

    [Fact]
    public void A_floating_window_is_not_moved_when_only_the_bars_thickness_changes_where_it_is_not()
    {
        var f = new DeskFixture();
        f.Open(1, frame: new Rect(1000, 500, 900, 700));
        Assert.True(f.Desk.SetFloating(W(1), true));
        f.Turn();
        Rect before = f.Managed(1)!.FloatingRect!.Value;

        // The bar goes from the top to the bottom, same thickness: origin
        // moves up by 42 and the height is the same, so the window moves up
        // with the work area and nothing else.
        MoveTheBar(f, 1, "bottom", 42);
        f.Turn();
        Rect after = f.Managed(1)!.FloatingRect!.Value;

        Assert.Equal(before.X, after.X);
        Assert.Equal(before.Y - 42, after.Y);
        Assert.Equal(before.Width, after.Width);
    }

    [Fact]
    public void A_system_dpi_window_beside_a_side_bar_starts_at_the_work_area_not_inside_the_bar()
    {
        var f = new DeskFixture(DeskFixture.Configuration(), MainWithBar("left", 80), FakePlatform.SecondMonitor());
        f.Platform.WindowList.Add(FakePlatform.Window(1, "notepad++", perMonitorDpi: false));
        f.Sync();

        Placement placement = PlacementOf(f.Turn(), 1);
        Rect work = BarredWorkArea(MainBounds, "left", 80);

        // Its 9 px invisible border may hang into the bar (the bar is on the
        // screen, the border is invisible); the frame itself may not.
        Assert.True(placement.Frame.Left >= work.Left, $"{placement.Frame} starts inside the bar");
        Assert.True(placement.Frame.Right <= work.Right, $"{placement.Frame}");
        Assert.True(placement.Frame.Bottom + 9 <= MainBounds.Bottom, $"{placement.Frame}");
    }

    [Fact]
    public void Hidden_workspaces_are_laid_out_against_the_new_bar_when_they_are_shown()
    {
        var f = new DeskFixture();
        f.Open(1);
        f.Turn();
        f.Desk.FocusWorkspace("12");
        f.Open(2);
        f.Turn();
        Assert.True(f.IsHidden(1));

        MoveTheBar(f, 1, "bottom", 60);
        Redraw redraw = f.Turn();
        Rect work = BarredWorkArea(MainBounds, "bottom", 60);
        Assert.Equal(work, PlacementOf(redraw, 2).Frame);
        // The hidden one is not touched while hidden ...
        Assert.DoesNotContain(redraw.Place, p => p.Window == W(1));

        // ... and gets the new area the moment its workspace is back.
        f.Desk.FocusWorkspace("11");
        Assert.Equal(work, PlacementOf(f.Turn(), 1).Frame);
    }

    [Fact]
    public void The_poller_sees_a_bar_that_grew_on_either_screen()
    {
        long now = 0;
        var watch = new ScreenWatch(() => now);
        MonitorSnapshot main = FakePlatform.MainMonitor();
        MonitorSnapshot second = FakePlatform.SecondMonitor();
        watch.Prime([main, second]);
        now += ScreenWatch.EveryMs;

        Assert.True(watch.Changed(() => [main, second with { WorkArea = BarredWorkArea(second.Bounds, "bottom", 60) }]));
        now += ScreenWatch.EveryMs;
        Assert.True(watch.Changed(() => [main with { WorkArea = BarredWorkArea(main.Bounds, "top", 90) }, second]));
        now += ScreenWatch.EveryMs;
        Assert.False(watch.Changed(() => [main with { WorkArea = BarredWorkArea(main.Bounds, "top", 90) }, second]));
    }
}
