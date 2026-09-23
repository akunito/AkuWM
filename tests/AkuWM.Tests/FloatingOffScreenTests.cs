using AkuWM.Core.Desk;
using AkuWM.Core.Model;
using Xunit;

namespace AkuWM.Tests;

/// <summary>
/// A floating window dragged mostly off the screen stays where it was put as
/// long as a grabbable piece of it is on the work area; with less than that,
/// or none (a screen that is gone), it is pulled back on (plan 10.35).
/// </summary>
public class FloatingOffScreenTests
{
    private static WindowHandle W(long handle) => new(handle);

    private static DeskFixture Floating()
    {
        var f = new DeskFixture();
        f.Open(1, resizable: false, frame: new Rect(1000, 500, 900, 700));
        f.Turn();
        f.Turn();
        f.Wait(10_000);
        return f;
    }

    [Fact]
    public void Mostly_off_the_screen_with_a_grabbable_strip_left_it_stays()
    {
        DeskFixture f = Floating();
        var parked = new Rect(1000, 2160 - 40, 900, 700); // 40 px above the bottom: a grab (the right edge has another screen)
        f.Move(1, parked);
        f.Wait(Desk.SettleMs + 1);
        f.Turn();
        f.Turn();
        Assert.Equal(parked, f.FrameOf(1));
    }

    [Fact]
    public void With_less_than_a_grab_left_it_comes_back_on()
    {
        DeskFixture f = Floating();
        f.Move(1, new Rect(1000, 2160 - 20, 900, 700)); // 20 px above the bottom: less than a grab
        f.Wait(Desk.SettleMs + 1);
        f.Turn();
        f.Turn();
        Rect where = f.FrameOf(1);
        Assert.True(where.Bottom <= 2160, $"{where}");
    }

    [Fact]
    public void Off_the_top_with_a_strip_left_it_stays_too()
    {
        DeskFixture f = Floating();
        var parked = new Rect(1000, 42 + 30 - 700, 900, 700); // 30 px below the work area's top
        f.Move(1, parked);
        f.Wait(Desk.SettleMs + 1);
        f.Turn();
        f.Turn();
        Assert.Equal(parked, f.FrameOf(1));
    }
}
