using AkuWM.Core.Desk;
using AkuWM.Core.Model;
using Xunit;

namespace AkuWM.Tests;

/// <summary>
/// A mouse button down on a tile lifts it into the always-on-top band, over
/// every floating window, until the button is let go; then the floating
/// windows come back RaiseAfterReleaseMs later (plan 10.35).
/// </summary>
public class PressAndReleaseTests
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
        f.Platform.Calls.Clear();
        f.Platform.Raised.Clear();
        return f;
    }

    [Fact]
    public void A_pressed_tile_goes_into_the_band_and_the_floating_windows_wait()
    {
        DeskFixture f = TileAndFloat();
        f.Foreground(1);
        f.Platform.ButtonsDown = true;

        Assert.True(f.Desk.Press(W(1)));
        Redraw held = f.Turn();
        Assert.Contains((W(1), true), held.Band);
        Assert.Empty(held.Raise);

        f.Wait(Desk.RaiseDelayMs * 3);
        Assert.Empty(f.Turn().Raise);
        Assert.True(f.Desk.RaisePending);
    }

    [Fact]
    public void Released_it_leaves_the_band_and_the_floating_windows_come_back_soon_after()
    {
        DeskFixture f = TileAndFloat();
        f.Foreground(1);
        f.Platform.ButtonsDown = true;
        f.Desk.Press(W(1));
        f.Turn();
        f.Platform.ButtonsDown = false;

        Assert.True(f.Desk.Release());
        Redraw released = f.Turn();
        Assert.Contains((W(1), false), released.Band);
        Assert.Empty(released.Raise);

        f.Wait(Desk.RaiseAfterReleaseMs);
        Redraw back = f.Turn();
        Assert.Contains(W(2), back.Raise);
        Assert.Contains(W(1), back.Tiles);
    }

    [Fact]
    public void A_release_the_script_never_sent_is_taken_from_the_buttons()
    {
        DeskFixture f = TileAndFloat();
        f.Platform.ButtonsDown = true;
        f.Desk.Press(W(1));
        f.Turn();
        f.Platform.ButtonsDown = false;

        f.Turn(); // the pass sees the buttons up and releases by itself
        Assert.False(f.Managed(1)!.Lifted);
        Assert.False(f.Desk.Release());
    }

    [Fact]
    public void Only_a_tile_and_never_under_a_fullscreen_window()
    {
        DeskFixture f = TileAndFloat();
        Assert.False(f.Desk.Press(W(2)));

        f.Open(3, resizable: false, frame: new Rect(0, 0, 3840, 2160));
        f.Turn();
        Assert.Equal(WindowState.Fullscreen, f.Managed(3)!.State);
        Assert.False(f.Desk.Press(W(1)));
    }
}
