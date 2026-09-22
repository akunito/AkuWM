using AkuWM.Core.Desk;
using AkuWM.Core.Model;
using Xunit;

namespace AkuWM.Tests;

/// <summary>
/// Floating windows stay in front of the tiles. The band is the first
/// answer, and Windows Terminal strips HWND_TOPMOST off itself (measured
/// 2026-09-22: the bit is gone 300 ms after SetWindowPos and after every
/// activation), so the band is read back, a refusal is remembered, and the
/// floating windows not held by the band are raised over a tile whenever a
/// tile takes the focus. The person can send one behind the tiles with
/// <c>lower</c> and bring it back with <c>raise</c> or by focusing it.
/// </summary>
public class FloatingOverTilesTests
{
    private static WindowHandle W(long handle) => new(handle);

    /// <summary>One tile, one floating window, both on workspace 11, all settled.</summary>
    private static DeskFixture TileAndFloat(bool bandRefused)
    {
        var f = new DeskFixture();
        f.Open(1);
        f.Open(2, process: "WindowsTerminal", frame: new Rect(1000, 500, 900, 700));
        if (bandRefused)
        {
            f.Platform.RefusesBand.Add(2);
        }

        Assert.True(f.Desk.SetFloating(W(2), true));
        f.Turn();
        f.Turn();
        f.Platform.Calls.Clear();
        f.Platform.Raised.Clear();
        return f;
    }

    [Fact]
    public void A_floating_window_that_keeps_the_band_is_left_alone_when_a_tile_takes_the_focus()
    {
        DeskFixture f = TileAndFloat(bandRefused: false);
        Assert.True(f.Managed(2)!.Banded);

        f.Foreground(1);
        Redraw redraw = f.Turn();

        Assert.Empty(redraw.Raise);
        Assert.DoesNotContain(redraw.Band, b => b.Window == W(2));
    }

    [Fact]
    public void A_band_the_window_does_not_keep_is_remembered_and_not_asked_again()
    {
        DeskFixture f = TileAndFloat(bandRefused: true);
        DeskWindow terminal = f.Managed(2)!;

        Assert.False(terminal.Banded);
        Assert.True(terminal.BandRefused);
        f.Turn();
        f.Turn();
        Assert.DoesNotContain(f.Platform.Calls, c => c.StartsWith("topmost 2 ", StringComparison.Ordinal));
    }

    [Fact]
    public void A_tile_taking_the_focus_raises_the_floating_windows_the_band_does_not_hold()
    {
        DeskFixture f = TileAndFloat(bandRefused: true);

        f.Foreground(1);
        Redraw redraw = f.Turn();

        Assert.Contains(W(2), redraw.Raise);
        Assert.Contains(W(2), f.Platform.Raised);
        // Once per focus change, not on every pass.
        Assert.Empty(f.Turn().Raise);
    }

    [Fact]
    public void The_focused_floating_window_itself_is_not_raised_and_another_workspace_is_not_touched()
    {
        DeskFixture f = TileAndFloat(bandRefused: true);
        f.Desk.FocusWorkspace("12");
        f.Open(3);
        f.Open(4, process: "WindowsTerminal", frame: new Rect(1200, 600, 600, 400));
        f.Platform.RefusesBand.Add(4);
        Assert.True(f.Desk.SetFloating(W(4), true));
        f.Turn();
        f.Turn();
        f.Platform.Raised.Clear();

        f.Foreground(3);
        Redraw redraw = f.Turn();

        Assert.Contains(W(4), redraw.Raise);
        Assert.DoesNotContain(W(2), redraw.Raise);

        f.Foreground(4);
        Assert.Empty(f.Turn().Raise);
    }

    [Fact]
    public void Lower_sends_a_floating_window_behind_the_tiles_once_and_leaves_it_there()
    {
        DeskFixture f = TileAndFloat(bandRefused: false);

        Assert.True(f.Desk.SetLowered(W(2), true));
        Redraw redraw = f.Turn();

        Assert.Contains(W(2), redraw.Lower);
        Assert.Contains(redraw.Band, b => b.Window == W(2) && !b.Topmost);
        Assert.Contains(W(2), f.Platform.Lowered);

        // A tile focused afterwards does not bring it back.
        f.Foreground(1);
        Redraw next = f.Turn();
        Assert.Empty(next.Raise);
        Assert.Empty(next.Lower);
        Assert.DoesNotContain(next.Band, b => b.Window == W(2));
    }

    [Fact]
    public void Raise_brings_a_lowered_window_back_over_the_tiles()
    {
        DeskFixture f = TileAndFloat(bandRefused: true);
        Assert.True(f.Desk.SetLowered(W(2), true));
        f.Turn();
        f.Platform.Raised.Clear();

        Assert.True(f.Desk.SetLowered(W(2), false));
        Redraw redraw = f.Turn();

        Assert.Contains(W(2), redraw.Raise);
        Assert.False(f.Managed(2)!.Lowered);
    }

    [Fact]
    public void A_band_kept_window_that_is_raised_from_lowered_goes_back_into_the_band()
    {
        DeskFixture f = TileAndFloat(bandRefused: false);
        Assert.True(f.Desk.SetLowered(W(2), true));
        f.Turn();

        Assert.True(f.Desk.SetLowered(W(2), false));
        Redraw redraw = f.Turn();

        Assert.Contains(redraw.Band, b => b.Window == W(2) && b.Topmost);
        Assert.True(f.Managed(2)!.Banded);
    }

    [Fact]
    public void Focusing_a_lowered_window_brings_it_forward()
    {
        DeskFixture f = TileAndFloat(bandRefused: false);
        Assert.True(f.Desk.SetLowered(W(2), true));
        f.Turn();

        f.Foreground(2);
        f.Turn();

        Assert.False(f.Managed(2)!.Lowered);
        Assert.True(f.Managed(2)!.Banded);
    }

    [Fact]
    public void Lower_and_raise_refuse_a_tile()
    {
        DeskFixture f = TileAndFloat(bandRefused: false);
        Assert.False(f.Desk.SetLowered(W(1), true));
        Assert.False(f.Desk.SetLowered(W(1), false));
    }

    [Fact]
    public void A_sticky_window_is_raised_over_a_tile_like_a_floating_one()
    {
        var f = new DeskFixture();
        f.Open(1);
        f.Open(2, process: "Telegram", frame: new Rect(2000, 300, 600, 900));
        f.Platform.RefusesBand.Add(2);
        Assert.True(f.Desk.SetSticky(W(2), true));
        f.Turn();
        f.Turn();
        f.Platform.Raised.Clear();

        f.Foreground(1);
        Assert.Contains(W(2), f.Turn().Raise);
    }

    [Fact]
    public void Under_a_game_nothing_is_raised()
    {
        DeskFixture f = TileAndFloat(bandRefused: true);
        f.Open(3, process: "game", frame: new Rect(0, 0, 3840, 2160), resizable: false);
        f.Turn();
        Assert.Equal(WindowState.Fullscreen, f.Managed(3)!.State);
        f.Platform.Raised.Clear();

        f.Foreground(1);
        Assert.Empty(f.Turn().Raise);
    }
}
