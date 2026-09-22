using AkuWM.Core.Desk;
using AkuWM.Core.Model;
using Xunit;

namespace AkuWM.Tests;

/// <summary>
/// A window created WS_MAXIMIZE | WS_VISIBLE shows itself un-maximised
/// first (100,100 1280x720 at 84 ms), is read and tiled, maximises itself at
/// 93 ms, and takes AkuWM's tile rectangle at 153 ms while still zoomed:
/// IsZoomed true at 1290x2127 in the tile, and SW_MAXIMIZE on a zoomed
/// window re-places nothing (measured on the desk 2026-09-22 23:16;
/// tests/fullscreen 8-startmax steps 1-3 for two runs). The model notices a
/// maximised window that does not cover its work area and asks Windows to
/// maximise it again, down and up.
/// </summary>
public class SquashedMaximizeTests
{
    private static readonly Rect Tile = new(1284, 42, 1272, 2118);
    private static readonly Rect Maximised = new(-11, 31, 3862, 2140);

    private static DeskFixture Squashed()
    {
        var f = new DeskFixture();
        f.Open(1);
        f.Turn();
        f.Open(2, frame: new Rect(100, 100, 1280, 720));
        f.Turn();
        Assert.Equal(WindowState.Tiling, f.Managed(2)!.State);

        // Maximised itself, and then took the tile's rectangle.
        int at = f.Platform.WindowList.FindIndex(w => w.Handle.Value == 2);
        f.Platform.WindowList[at] = f.Platform.WindowList[at] with { IsMaximized = true, FrameBounds = Tile, WindowRect = Tile };
        f.Sync();
        Assert.Equal(WindowState.Fullscreen, f.Managed(2)!.State);
        return f;
    }

    [Fact]
    public void A_maximised_window_that_does_not_cover_its_work_area_is_maximised_again()
    {
        DeskFixture f = Squashed();
        f.Platform.Calls.Clear();

        Redraw redraw = f.Turn();
        Assert.Equal([DeskFixture.W(2)], redraw.Remaximize);
        Assert.DoesNotContain(redraw.Place, p => p.Window == DeskFixture.W(2));
        Assert.Equal(["maximize 2 False", "maximize 2 True"], f.Platform.Calls.Where(c => c.StartsWith("maximize")).ToArray());
    }

    [Fact]
    public void Asked_once_with_patience()
    {
        DeskFixture f = Squashed();
        f.Turn();
        Assert.Empty(f.Turn().Remaximize);

        f.Wait(Desk.PlacementPatienceMs);
        Assert.Equal([DeskFixture.W(2)], f.Turn().Remaximize);
    }

    [Fact]
    public void A_maximised_window_over_its_work_area_is_left_alone()
    {
        DeskFixture f = Squashed();
        f.Turn();

        int at = f.Platform.WindowList.FindIndex(w => w.Handle.Value == 2);
        f.Platform.WindowList[at] = f.Platform.WindowList[at] with { FrameBounds = Maximised, WindowRect = Maximised };
        f.Sync();
        f.Wait(Desk.PlacementPatienceMs);

        Redraw redraw = f.Turn();
        Assert.Empty(redraw.Remaximize);
        Assert.DoesNotContain(redraw.Place, p => p.Window == DeskFixture.W(2));
        Assert.Equal(WindowState.Fullscreen, f.Managed(2)!.State);
    }

    [Fact]
    public void The_un_maximised_rectangle_on_the_way_down_is_not_the_window_leaving_fullscreen()
    {
        DeskFixture f = Squashed();
        f.Turn();

        // Windows reports the restore before the maximise.
        int at = f.Platform.WindowList.FindIndex(w => w.Handle.Value == 2);
        var normal = new Rect(100, 100, 1280, 720);
        f.Platform.WindowList[at] = f.Platform.WindowList[at] with { IsMaximized = false, FrameBounds = normal, WindowRect = normal };
        f.Sync();
        Assert.Equal(WindowState.Fullscreen, f.Managed(2)!.State);

        // And nothing is placed over it while the maximise is on its way.
        Redraw redraw = f.Turn();
        Assert.DoesNotContain(redraw.Place, p => p.Window == DeskFixture.W(2));
        Assert.Empty(redraw.Unmaximize);

        f.Platform.WindowList[at] = f.Platform.WindowList[at] with { IsMaximized = true, FrameBounds = Maximised, WindowRect = Maximised };
        f.Sync();
        Assert.Equal(WindowState.Fullscreen, f.Managed(2)!.State);
        Assert.Empty(f.Turn().Remaximize);
    }
}
