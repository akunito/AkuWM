using AkuWM.Core.Desk;
using AkuWM.Core.Model;
using Xunit;

namespace AkuWM.Tests;

/// <summary>
/// Asking for a window by name (Hyper+letter, focus --container-id) while a
/// fullscreen window covers its workspace lifts it over the game: into the
/// always-on-top band, the game untouched. The game taking the focus again
/// sends it back under (tests/wm toggle 7b; plan 10.13's "nothing lifts an
/// app over a game when it is asked for by name").
/// </summary>
public class OverGameTests
{
    private static WindowHandle W(long handle) => new(handle);

    private static DeskFixture GameAndFloat()
    {
        var f = new DeskFixture();
        f.Open(1, resizable: false, frame: new Rect(400, 400, 800, 600));
        f.Open(2, resizable: false, frame: new Rect(100, 100, 600, 400));
        f.Turn();
        f.Desk.SetFullscreen(W(2), true);
        f.Turn();
        f.Turn();
        Assert.Equal(W(2), f.Managed(1)!.Behind);
        Assert.False(f.Managed(1)!.Banded);
        return f;
    }

    [Fact]
    public void Asked_for_by_name_it_goes_into_the_band_over_the_game()
    {
        DeskFixture f = GameAndFloat();

        Assert.True(f.Desk.ShowOverGame(W(1)));
        Redraw redraw = f.Turn();

        Assert.Contains((W(1), true), redraw.Band);
        Assert.DoesNotContain(redraw.Behind, b => b.Item1 == W(1));
        Assert.DoesNotContain(redraw.Band, b => b.Window == W(2));
        Assert.True(f.Managed(1)!.OverGame);
    }

    [Fact]
    public void The_game_taking_the_focus_again_sends_it_back_under()
    {
        DeskFixture f = GameAndFloat();
        f.Desk.ShowOverGame(W(1));
        f.Turn();
        f.Wait(10_000);

        Assert.True(f.Foreground(2));
        Redraw redraw = f.Turn();

        Assert.False(f.Managed(1)!.OverGame);
        Assert.Contains((W(1), false), redraw.Band);
        Assert.Contains((W(1), W(2)), redraw.Behind);
    }

    [Fact]
    public void A_window_that_merely_opens_stays_under()
    {
        DeskFixture f = GameAndFloat();
        Assert.False(f.Managed(1)!.OverGame);
        Assert.Empty(f.Turn().Band);
    }

    [Fact]
    public void A_tile_asked_for_by_name_goes_over_the_game_too()
    {
        var f = new DeskFixture();
        f.Open(1);
        f.Open(2, resizable: false, frame: new Rect(100, 100, 600, 400));
        f.Turn();
        f.Desk.SetFullscreen(W(2), true);
        f.Turn();
        f.Turn();
        Assert.Equal(WindowState.Tiling, f.Managed(1)!.State);
        Assert.Equal(W(2), f.Managed(1)!.Behind);

        Assert.True(f.Desk.ShowOverGame(W(1)));
        Redraw redraw = f.Turn();

        Assert.Contains((W(1), true), redraw.Band);
        Assert.DoesNotContain(redraw.Behind, b => b.Item1 == W(1));
    }

    [Fact]
    public void Nothing_to_lift_over_without_a_game()
    {
        var f = new DeskFixture();
        f.Open(1, resizable: false, frame: new Rect(400, 400, 800, 600));
        f.Turn();
        Assert.False(f.Desk.ShowOverGame(W(1)));
    }
}
