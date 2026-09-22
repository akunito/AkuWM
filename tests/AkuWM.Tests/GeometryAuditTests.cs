using AkuWM.Core.Desk;
using AkuWM.Core.Layout;
using AkuWM.Core.Model;
using Xunit;

namespace AkuWM.Tests;

/// <summary>The geometry audit of 2026-09-22: what the numbers said.</summary>
public class GeometryAuditTests
{
    private static WindowHandle W(long handle) => new(handle);

    private static DeskFixture ThreeColumns()
    {
        var fixture = new DeskFixture();
        fixture.Open(1);
        fixture.Open(2);
        fixture.Open(3);
        fixture.Turn();
        fixture.Turn();
        return fixture;
    }

    // ---- the drag outline -------------------------------------------------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_outline_is_exactly_where_the_drop_lands(bool leftHalf)
    {
        DeskFixture fixture = ThreeColumns();
        Rect third = fixture.FrameOf(3);
        int x = leftHalf ? third.X + (third.Width / 4) : third.X + (third.Width * 3 / 4);
        int y = third.Y + (third.Height / 2);

        DropTarget? preview = fixture.Desk.DropPreview(W(1), x, y);
        Assert.NotNull(preview);

        Assert.True(fixture.Desk.DropTile(W(1), x, y));
        fixture.Turn();
        fixture.Turn();

        // Beside a tile whose parent already splits that way, the window joins
        // the ROW: a third of it, not half of the tile. The outline said half
        // of the tile, 1284 px away from where the window went.
        Assert.Equal(fixture.FrameOf(1), preview!.Value.Preview);
    }

    [Fact]
    public void The_outline_on_another_screen_is_where_the_drop_lands_there()
    {
        DeskFixture fixture = ThreeColumns();
        fixture.Desk.FocusWorkspace("21");
        fixture.Open(4, monitor: new MonitorHandle(2));
        fixture.Turn();
        fixture.Turn();
        Rect other = fixture.FrameOf(4);
        (int x, int y) = (other.X + (other.Width / 2), other.Y + (other.Height / 4));

        DropTarget? preview = fixture.Desk.DropPreview(W(1), x, y);
        Assert.NotNull(preview);

        Assert.True(fixture.Desk.DropTile(W(1), x, y));
        fixture.Turn();
        fixture.Turn();

        Assert.Equal(fixture.FrameOf(1), preview!.Value.Preview);
    }

    // ---- resizing past the minimum share ------------------------------------

    [Fact]
    public void Asking_for_wider_never_makes_a_window_narrower()
    {
        var tree = new TilingTree();
        tree.Add(W(1), WindowHandle.None, SplitDirection.Horizontal);
        tree.Add(W(2), W(1), SplitDirection.Horizontal);
        for (int i = 0; i < 9; i++)
        {
            tree.Resize(W(1), Direction.Right, 0.05);
        }

        // A third window takes a third of each: window 2 is now under the
        // minimum share, and there is nothing to take from it.
        tree.Add(W(3), W(2), SplitDirection.Horizontal);
        Rect area = new(0, 0, 3840, 2118);
        int was = tree.Rects(area, new Gaps(12))[W(1)].Width;

        tree.Resize(W(1), Direction.Right, 0.05);

        Assert.True(tree.Rects(area, new Gaps(12))[W(1)].Width >= was);
    }

    // ---- a window too big for the screen it is dropped on -----------------

    [Fact]
    public void A_window_too_big_for_the_screen_to_the_right_still_lands_on_it()
    {
        var fixture = new DeskFixture();
        fixture.Open(1, frame: new Rect(400, 300, 2020, 2591));
        fixture.Desk.SetSticky(W(1), true);
        fixture.Turn();

        // Its centre is on the vertical monitor and so is the hand, but it
        // does not fit there, and its top-left corner -- the old tie-breaker
        // -- is still on the main one. It went home every time.
        fixture.Move(1, new Rect(3550, -300, 2020, 2591), hand: (4560, 800));
        fixture.Turn();
        fixture.Turn();

        Assert.Equal("second", fixture.Managed(1)!.StickyMonitor);
        Assert.True(fixture.FrameOf(1).FractionInside(FakePlatform.SecondMonitor().WorkArea) > 0.99, $"{fixture.FrameOf(1)}");
    }

    // ---- the second after a crossing ---------------------------------------

    [Fact]
    public void A_drag_that_goes_on_after_a_crossing_is_the_persons()
    {
        var fixture = new DeskFixture();
        fixture.Wait(5000); // so that "crossed at 0" is not "never crossed"
        fixture.Open(1, resizable: false);
        fixture.Turn();

        fixture.Move(1, new Rect(4000, 100, 800, 600));
        fixture.Turn();
        fixture.Turn();
        Rect landed = fixture.FrameOf(1);

        // 200 ms later, the hand is still moving it: same size, new place.
        fixture.Wait(200);
        fixture.Move(1, landed with { X = landed.X + 150, Y = landed.Y + 100 });
        fixture.Turn();

        Assert.Equal(landed.X + 150, fixture.FrameOf(1).X);
        Assert.Equal(landed.Y + 100, fixture.FrameOf(1).Y);
    }

    [Fact]
    public void A_resize_in_the_beat_after_a_crossing_is_windows_and_is_undone()
    {
        var fixture = new DeskFixture();
        fixture.Wait(5000);
        fixture.Open(1, resizable: false);
        fixture.Turn();

        fixture.Move(1, new Rect(4000, 100, 800, 600));
        fixture.Turn();
        fixture.Turn();
        Rect landed = fixture.FrameOf(1);

        // 140 ms later Windows rescales it for the new DPI, behind the back
        // of everybody.
        fixture.Wait(140);
        fixture.Platform.ApplicationMoves(W(1), landed with { Width = landed.Width * 5 / 6, Height = landed.Height * 5 / 6 });
        fixture.Sync();
        fixture.Turn();

        // Once the window has rested: a window still changing is left alone.
        fixture.Wait(Desk.SettleMs + 1);
        fixture.Turn();

        Assert.Equal(landed.Width, fixture.FrameOf(1).Width);
        Assert.Equal(landed.Height, fixture.FrameOf(1).Height);
    }

    // ---- a tile with no room for gaps ---------------------------------------

    [Fact]
    public void Children_stay_inside_a_tile_too_small_for_the_gaps()
    {
        Tile root = Tile.Split(SplitDirection.Horizontal, Tile.Leaf(W(1)), Tile.Leaf(W(2)), Tile.Leaf(W(3)));
        Rect area = new(0, 0, 10, 10);

        Dictionary<WindowHandle, Rect> rects = TileGeometry.Compute(root, area, new Gaps(12));

        Assert.All(rects.Values, r => Assert.True(area.Contains(r), $"{r} is outside {area}"));
    }

    // ---- the invisible border across a change of scale ----------------------

    [Fact]
    public void A_border_read_on_one_scale_is_not_applied_on_another()
    {
        var fixture = new DeskFixture();
        fixture.Open(1, monitor: new MonitorHandle(2), frame: new Rect(4000, 100, 800, 600));
        fixture.Turn();

        fixture.Desk.MoveToWorkspace(W(1), "11");
        Redraw redraw = fixture.Desk.Compute();

        Placement placement = Assert.Single(redraw.Place, p => p.Window == W(1));
        Assert.Null(placement.Border);
    }

    [Fact]
    public void A_maximised_windows_border_is_not_trusted()
    {
        var fixture = new DeskFixture();
        fixture.Platform.WindowList.Add(FakePlatform.Window(1, "zen", frame: new Rect(0, 42, 3840, 2118), maximized: true));
        fixture.Sync();
        fixture.Open(2);

        Redraw redraw = fixture.Desk.Compute();

        Assert.All(redraw.Place.Where(p => p.Window == W(1)), p => Assert.Null(p.Border));
    }
}

/// <summary>
/// Reported from the desk 2026-09-22: dragging a floating window right after
/// moving it to another monitor flickered for a second or so, the first time.
/// </summary>
public class DragAfterCrossingTests
{
    private static WindowHandle W(long handle) => new(handle);

    [Fact]
    public void A_drag_that_starts_right_after_a_move_to_another_monitor_is_never_fought()
    {
        var fixture = new DeskFixture();
        fixture.Wait(5000);
        fixture.Open(1, resizable: false);
        fixture.Turn();

        // Hyper+Shift+Right: the model moves it, the redraw places it.
        Assert.True(fixture.Desk.MoveToWorkspace(W(1), "21"));
        fixture.Turn();
        Rect placed = fixture.FrameOf(1);
        Assert.True(placed.X >= 3840, $"{placed} is not on the second monitor");

        // Windows rescales it for 125 % a beat later, behind everybody's back.
        fixture.Wait(140);
        fixture.Platform.ApplicationMoves(W(1), placed with { Width = placed.Width * 5 / 6, Height = placed.Height * 5 / 6 });
        fixture.Sync();
        fixture.Turn();

        // And the hand takes it, at the drag script's rate. Every tick the
        // window is where the hand put it; a placement now is AkuWM putting it
        // back, which the script then undoes on its next tick: that is the
        // flicker. The crossing guard threw away every move for a second.
        Rect at = fixture.FrameOf(1);
        int fought = 0;
        for (int tick = 1; tick <= 60; tick++)
        {
            fixture.Wait(8);
            at = at with { X = at.X + 6, Y = at.Y + 2 };
            fixture.Move(1, at);
            Redraw redraw = fixture.Turn();
            if (redraw.Place.Any(p => p.Window == W(1)))
            {
                fought++;
            }
        }

        Assert.Equal(0, fought);
        Assert.Equal(at, fixture.FrameOf(1));
    }
}
