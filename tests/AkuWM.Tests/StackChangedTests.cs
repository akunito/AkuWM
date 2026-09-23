using AkuWM.Core.Desk;
using AkuWM.Core.Model;
using Xunit;

namespace AkuWM.Tests;

/// <summary>
/// A tile clicked while it already has the focus comes up over the floating
/// windows natively and no foreground event follows; the z-order event does
/// (EVENT_OBJECT_REORDER), and the floating windows go back over it -- in
/// their own order, by lowering the tiles (live desk 2026-09-23 09:26).
/// </summary>
public class StackChangedTests
{
    private static WindowHandle W(long handle) => new(handle);

    private static DeskFixture TileAndFloat()
    {
        var f = new DeskFixture();
        f.Open(1);
        f.Open(2, resizable: false, frame: new Rect(300, 300, 800, 600));
        f.Turn();
        f.Foreground(1);
        f.Turn();
        f.Wait(Desk.RaiseDelayMs);
        f.Turn();
        f.Platform.Raised.Clear();
        f.Platform.TilesLowered.Clear();
        return f;
    }

    [Fact]
    public void A_reorder_with_a_tile_in_focus_brings_the_floating_windows_back_over_it()
    {
        DeskFixture f = TileAndFloat();
        Assert.Equal(WindowState.Floating, f.Managed(2)!.State);

        f.Desk.StackChanged();
        Assert.Empty(f.Turn().Raise); // like a click: RaiseDelayMs first
        f.Wait(Desk.RaiseDelayMs);

        Redraw redraw = f.Turn();
        Assert.Contains(W(2), redraw.Raise);
        Assert.Contains(W(1), redraw.Tiles);
        Assert.Contains(W(1), f.Platform.TilesLowered);
    }

    [Fact]
    public void A_reorder_while_a_raise_is_on_its_way_does_not_re_arm_it()
    {
        DeskFixture f = TileAndFloat();
        f.Desk.StackChanged();
        f.Wait(100);
        f.Desk.StackChanged();
        f.Wait(Desk.RaiseDelayMs - 100);
        Assert.Contains(W(2), f.Turn().Raise);
        f.Wait(Desk.RaiseDelayMs);
        Assert.Empty(f.Turn().Raise);
    }

    [Fact]
    public void A_reorder_with_a_floating_window_in_focus_does_nothing()
    {
        DeskFixture f = TileAndFloat();
        f.Foreground(2);
        f.Turn();
        f.Desk.StackChanged();
        f.Wait(Desk.RaiseDelayMs);
        Assert.Empty(f.Turn().Raise);
    }
}
