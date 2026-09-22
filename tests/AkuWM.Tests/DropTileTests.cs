using System.Linq;
using AkuWM.Core.Desk;
using AkuWM.Core.Layout;
using AkuWM.Core.Model;
using Xunit;

namespace AkuWM.Tests;

/// <summary>
/// Dropping a tiled window where the pointer is, on its own screen or another.
/// </summary>
public class DropTileTests
{
    private static DeskFixture TwoTiles()
    {
        var fixture = new DeskFixture();
        fixture.Open(1);
        fixture.Open(2);
        fixture.Turn();
        fixture.Turn();
        return fixture;
    }

    [Fact]
    public void The_left_half_of_a_tile_puts_the_window_on_its_left()
    {
        DeskFixture fixture = TwoTiles();

        // Two columns of the main monitor's 3840 wide work area. Window 3
        // dropped a quarter into the FIRST one.
        fixture.Open(3);
        fixture.Turn();

        Rect first = fixture.FrameOf(1);
        fixture.Desk.DropTile(DeskFixture.W(3), first.X + (first.Width / 4), first.Y + (first.Height / 2));
        fixture.Turn();
        fixture.Turn();

        // It is now the leftmost of the three.
        Assert.Equal(DeskFixture.W(3), Leftmost(fixture));
    }

    [Fact]
    public void And_the_right_half_on_its_right()
    {
        DeskFixture fixture = TwoTiles();
        fixture.Open(3);
        fixture.Turn();

        Rect first = fixture.FrameOf(1);
        fixture.Desk.DropTile(DeskFixture.W(3), first.Right - (first.Width / 4), first.Y + (first.Height / 2));
        fixture.Turn();
        fixture.Turn();

        Assert.NotEqual(DeskFixture.W(3), Leftmost(fixture));
        Assert.Equal(DeskFixture.W(1), Leftmost(fixture));
    }

    [Fact]
    public void A_drop_on_the_other_monitor_moves_it_there()
    {
        DeskFixture fixture = TwoTiles();

        // The second monitor's work area, middle of it.
        Rect second = fixture.Desk.Monitors.Single(m => m.Role == "second").TilingArea;
        fixture.Desk.DropTile(
            DeskFixture.W(2), second.X + (second.Width / 2), second.Y + (second.Height / 2));
        fixture.Turn();
        fixture.Turn();

        Assert.Equal("21", fixture.Managed(2)!.Workspace);
        Assert.Equal(second, fixture.FrameOf(2));
    }

    [Fact]
    public void The_gap_it_leaves_closes()
    {
        DeskFixture fixture = TwoTiles();
        Rect whole = fixture.Desk.Monitors.Single(m => m.Role == "main").TilingArea;

        Rect second = fixture.Desk.Monitors.Single(m => m.Role == "second").TilingArea;
        fixture.Desk.DropTile(DeskFixture.W(2), second.X + 10, second.Y + 10);
        fixture.Turn();
        fixture.Turn();

        // The one left behind fills the monitor by itself.
        Assert.Equal(whole, fixture.FrameOf(1));
    }

    [Fact]
    public void A_floating_window_is_refused()
    {
        DeskFixture fixture = TwoTiles();
        fixture.Desk.SetFloating(DeskFixture.W(2), true);
        fixture.Turn();

        Rect first = fixture.FrameOf(1);

        Assert.False(fixture.Desk.DropTile(DeskFixture.W(2), first.X + 20, first.Y + 20));
        Assert.Equal(WindowState.Floating, fixture.Managed(2)!.State);
    }

    [Fact]
    public void Dropping_a_window_on_its_own_tile_changes_nothing()
    {
        DeskFixture fixture = TwoTiles();
        Rect was = fixture.FrameOf(1);

        Assert.False(fixture.Desk.DropTile(
            DeskFixture.W(1), was.X + (was.Width / 4), was.Y + (was.Height / 2)));

        fixture.Turn();
        Assert.Equal(was, fixture.FrameOf(1));
    }

    [Fact]
    public void The_preview_is_the_rectangle_it_would_take()
    {
        DeskFixture fixture = TwoTiles();
        fixture.Open(3);
        fixture.Turn();

        Rect first = fixture.FrameOf(1);
        DropTarget? preview = fixture.Desk.DropPreview(
            DeskFixture.W(3), first.X + (first.Width / 4), first.Y + (first.Height / 2));

        Assert.NotNull(preview);
        Assert.Equal(DeskFixture.W(1), preview!.Value.NextTo);
        Assert.True(preview.Value.Before);

        // Beside a tile in a row of two, the window joins the row: a third of
        // it, and that is what the outline shows. It used to show half of the
        // tile, which is not a rectangle any window was going to get.
        fixture.Desk.DropTile(DeskFixture.W(3), first.X + (first.Width / 4), first.Y + (first.Height / 2));
        fixture.Turn();
        fixture.Turn();
        Assert.Equal(fixture.FrameOf(3), preview.Value.Preview);
    }

    [Fact]
    public void There_is_no_preview_where_the_drop_would_be_refused()
    {
        DeskFixture fixture = TwoTiles();
        Rect own = fixture.FrameOf(1);

        // Over its own tile: the outline must show nothing rather than promise
        // the whole work area.
        Assert.Null(fixture.Desk.DropPreview(DeskFixture.W(1), own.X + 40, own.Y + 40));
    }

    /// <summary>The handle of the tile furthest left on the main monitor.</summary>
    private static WindowHandle Leftmost(DeskFixture fixture)
    {
        DeskMonitor main = fixture.Desk.Monitors.Single(m => m.Role == "main");

        return main.Displayed!.Tiling
            .Rects(main.TilingArea, new Gaps(0, 0, 0, 0, 0))
            .OrderBy(t => t.Value.X)
            .First()
            .Key;
    }
}
