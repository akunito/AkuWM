using AkuWM.Core.Desk;
using AkuWM.Core.Model;
using Xunit;

namespace AkuWM.Tests;

/// <summary>
/// A monitor moved in the display settings takes its windows with it.
/// </summary>
/// <remarks>
/// Diego, 2026-09-21: "deberían moverse con el monitor, o sea, estar ancladas
/// relativamente a ellos, y no perder la posición dentro del área del
/// monitor".
///
/// A tiled window gets this for free -- the layout is recomputed from the work
/// area. A floating one does not: its rectangle is absolute, so a screen that
/// moves 312 px up leaves every floating window on it 312 px below where it
/// was, which after a big enough move is another screen or nowhere at all.
/// </remarks>
public class MonitorMovedTests
{
    private static MonitorSnapshot Second(Rect bounds, int bar = 35) =>
        FakePlatform.SecondMonitor() with
        {
            Bounds = bounds,
            WorkArea = new Rect(bounds.X, bounds.Y + bar, bounds.Width, bounds.Height - bar),
        };

    /// <summary>
    /// A floating window on the vertical monitor, left exactly where the
    /// person put it -- floating a window centres it, so the rectangle has to
    /// be given afterwards, by a move, which is the path this is about anyway.
    /// </summary>
    private static DeskFixture OnTheSecond(Rect where)
    {
        var made = new DeskFixture();
        made.Open(1, frame: where, monitor: new MonitorHandle(2));
        made.Desk.MoveToWorkspace(DeskFixture.W(1), "21");
        made.Desk.SetFloating(DeskFixture.W(1), true);
        made.Turn();

        made.Move(1, where);
        made.Turn();
        return made;
    }

    [Fact]
    public void A_floating_window_keeps_its_place_on_a_screen_that_moved()
    {
        // The vertical monitor is at 3840,-408 with its work area at -373.
        DeskFixture fixture = OnTheSecond(new Rect(4000, -300, 900, 700));

        // Diego moved it up: 3840,-720, work area at -685. The same screen,
        // the same size, 312 px higher.
        fixture.Desk.SetMonitors([FakePlatform.MainMonitor(), Second(new Rect(3840, -720, 1440, 2560))]);
        fixture.Wait(AkuWM.Core.Desk.Desk.ScreenSettleMs);
        fixture.Turn();
        fixture.Turn();

        Assert.Equal(new Rect(4000, -612, 900, 700), fixture.FrameOf(1));
    }

    [Fact]
    public void And_so_does_one_that_is_minimised()
    {
        DeskFixture fixture = OnTheSecond(new Rect(4000, -300, 900, 700));
        fixture.Platform.SetMinimized(DeskFixture.W(1), true);
        fixture.Sync();
        fixture.Turn();

        fixture.Desk.SetMonitors([FakePlatform.MainMonitor(), Second(new Rect(3840, -720, 1440, 2560))]);
        fixture.Wait(AkuWM.Core.Desk.Desk.ScreenSettleMs);

        // It is on the taskbar: nothing is placed, and the rectangle it will
        // come back to is the one that has to have moved.
        Assert.Equal(new Rect(4000, -612, 900, 700), fixture.Desk.Window(DeskFixture.W(1))!.FloatingRect);
    }

    [Fact]
    public void A_screen_that_also_changed_size_keeps_the_window_in_the_same_relative_spot()
    {
        // A third of the way across, a fifth of the way down.
        DeskFixture fixture = OnTheSecond(new Rect(4320, 132, 400, 300));

        // 1440x2525 of work area becomes 1920x1045: another resolution
        // entirely, at 0,0.
        fixture.Desk.SetMonitors(
            [FakePlatform.MainMonitor(), Second(new Rect(0, -1080, 1920, 1080), bar: 35)]);
        fixture.Wait(AkuWM.Core.Desk.Desk.ScreenSettleMs);
        fixture.Turn();
        fixture.Turn();

        // 480/1440 across and 505/2525 down, in the new area: 0 + 640, and
        // -1045 + 209.
        Rect where = fixture.FrameOf(1);
        Assert.Equal(640, where.X);
        Assert.Equal(-836, where.Y);
    }

    [Fact]
    public void A_window_on_another_screen_is_left_alone()
    {
        var fixture = new DeskFixture();
        fixture.Open(1, frame: new Rect(400, 300, 900, 700));
        fixture.Desk.SetFloating(DeskFixture.W(1), true);
        fixture.Turn();
        fixture.Move(1, new Rect(400, 300, 900, 700));
        fixture.Turn();

        fixture.Desk.SetMonitors([FakePlatform.MainMonitor(), Second(new Rect(3840, -720, 1440, 2560))]);
        fixture.Wait(AkuWM.Core.Desk.Desk.ScreenSettleMs);
        fixture.Turn();
        fixture.Turn();

        Assert.Equal(new Rect(400, 300, 900, 700), fixture.FrameOf(1));
    }

    [Fact]
    public void Tiles_simply_fill_the_screen_where_it_is_now()
    {
        var fixture = new DeskFixture();
        fixture.Open(1, frame: new Rect(4000, -300, 900, 700), monitor: new MonitorHandle(2));
        fixture.Desk.MoveToWorkspace(DeskFixture.W(1), "21");
        fixture.Turn();

        var moved = new Rect(3840, -720, 1440, 2560);
        fixture.Desk.SetMonitors([FakePlatform.MainMonitor(), Second(moved)]);
        fixture.Wait(AkuWM.Core.Desk.Desk.ScreenSettleMs);
        fixture.Turn();
        fixture.Turn();

        Assert.Equal(new Rect(3840, -685, 1440, 2525), fixture.FrameOf(1));
    }
}
