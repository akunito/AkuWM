using AkuWM.Core.Desk;
using AkuWM.Core.Model;
using Xunit;

namespace AkuWM.Tests;

/// <summary>
/// A clicked tile comes up over the floating windows natively and stays
/// there for as long as the button is held; the floating windows come back
/// RaiseAfterReleaseMs after it is let go. The wait is armed once per click:
/// the two or three foreground events an application sends while it
/// activates do not restart it (plan 10.35).
/// </summary>
public class RaiseWaitsForTheButtonsTests
{
    private static WindowHandle W(long handle) => new(handle);

    private static DeskFixture TileAndFloat()
    {
        var f = new DeskFixture();
        f.Open(1);
        f.Open(2, process: "WindowsTerminal", frame: new Rect(1000, 500, 900, 700));
        f.Platform.RefusesBand.Add(2);
        Assert.True(f.Desk.SetFloating(W(2), true));
        f.Turn();
        f.Turn();
        f.Platform.Raised.Clear();
        return f;
    }

    [Fact]
    public void While_the_button_is_held_the_tile_stays_in_front()
    {
        DeskFixture f = TileAndFloat();
        f.Platform.ButtonsDown = true;
        f.Foreground(1);
        f.Wait(Desk.RaiseDelayMs * 3);

        Redraw held = f.Turn();
        Assert.Empty(held.Raise);
        Assert.True(f.Desk.RaisePending);

        f.Platform.ButtonsDown = false;
        Assert.Empty(f.Turn().Raise); // just released: not yet
        f.Wait(Desk.RaiseAfterReleaseMs);
        Assert.Contains(W(2), f.Turn().Raise);
        Assert.False(f.Desk.RaisePending);
    }

    [Fact]
    public void A_second_foreground_event_of_the_same_click_does_not_restart_the_wait()
    {
        DeskFixture f = TileAndFloat();
        f.Open(3);
        f.Turn();
        f.Platform.Raised.Clear();

        f.Foreground(1);
        f.Wait(Desk.RaiseAfterReleaseMs - 10);
        f.Foreground(3); // the application activating again, same workspace
        f.Turn();
        f.Wait(10);

        Assert.Contains(W(2), f.Turn().Raise);
    }

    [Fact]
    public void With_no_button_at_all_the_raise_comes_soon_after_the_focus()
    {
        DeskFixture f = TileAndFloat();
        f.Foreground(1);
        f.Wait(Desk.RaiseAfterReleaseMs);
        Assert.Contains(W(2), f.Turn().Raise);
    }
}
