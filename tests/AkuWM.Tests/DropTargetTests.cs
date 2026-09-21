using System.Collections.Generic;
using AkuWM.Core.Layout;
using AkuWM.Core.Model;
using Xunit;

namespace AkuWM.Tests;

/// <summary>
/// Reading a point on the screen as a place in the layout: the half of the
/// tile under the cursor decides, and the outline shows exactly the rectangle
/// the window is about to take.
/// </summary>
public class DropTargetTests
{
    private static readonly Rect Area = new(0, 0, 2000, 1000);

    /// <summary>Two columns, 1000 wide each.</summary>
    private static Dictionary<WindowHandle, Rect> TwoColumns() => new()
    {
        [new WindowHandle(1)] = new Rect(0, 0, 1000, 1000),
        [new WindowHandle(2)] = new Rect(1000, 0, 1000, 1000),
    };

    [Fact]
    public void The_left_quarter_of_a_tile_goes_to_its_left()
    {
        DropTarget where = DropTargets.Resolve(TwoColumns(), Area, 200, 500, new WindowHandle(9));

        Assert.Equal(new WindowHandle(1), where.NextTo);
        Assert.Equal(SplitDirection.Horizontal, where.Direction);
        Assert.True(where.Before);
        Assert.Equal(new Rect(0, 0, 500, 1000), where.Preview);
    }

    [Fact]
    public void The_right_quarter_goes_to_its_right()
    {
        DropTarget where = DropTargets.Resolve(TwoColumns(), Area, 1800, 500, new WindowHandle(9));

        Assert.Equal(new WindowHandle(2), where.NextTo);
        Assert.False(where.Before);
        Assert.Equal(new Rect(1500, 0, 500, 1000), where.Preview);
    }

    [Fact]
    public void The_top_of_a_tile_splits_it_the_other_way()
    {
        // Dead centre across, near the top: the vertical distance from the
        // middle is the bigger one, so it splits horizontally.
        DropTarget where = DropTargets.Resolve(TwoColumns(), Area, 500, 60, new WindowHandle(9));

        Assert.Equal(new WindowHandle(1), where.NextTo);
        Assert.Equal(SplitDirection.Vertical, where.Direction);
        Assert.True(where.Before);
        Assert.Equal(new Rect(0, 0, 1000, 500), where.Preview);
    }

    [Fact]
    public void And_the_bottom()
    {
        DropTarget where = DropTargets.Resolve(TwoColumns(), Area, 1500, 950, new WindowHandle(9));

        Assert.Equal(new WindowHandle(2), where.NextTo);
        Assert.Equal(SplitDirection.Vertical, where.Direction);
        Assert.False(where.Before);
        Assert.Equal(new Rect(1000, 500, 1000, 500), where.Preview);
    }

    [Fact]
    public void A_corner_still_means_something_definite()
    {
        // 10 % across and 10 % down of the left tile: both say "before", and
        // the one further from the middle decides which axis. They are equal
        // here by construction of the tile, so across wins by the >=.
        DropTarget where = DropTargets.Resolve(TwoColumns(), Area, 100, 100, new WindowHandle(9));

        Assert.True(where.Before);
        Assert.Equal(SplitDirection.Horizontal, where.Direction);
    }

    [Fact]
    public void Its_own_tile_is_not_a_target()
    {
        // Dragging window 1 over itself: there is nothing to land beside, so
        // the whole area is the answer and the drop is a no-op.
        DropTarget where = DropTargets.Resolve(TwoColumns(), Area, 200, 500, new WindowHandle(1));

        Assert.True(where.NextTo.IsNone);
        Assert.Equal(Area, where.Preview);
    }

    [Fact]
    public void An_empty_workspace_takes_the_whole_screen()
    {
        DropTarget where = DropTargets.Resolve(new Dictionary<WindowHandle, Rect>(), Area, 900, 400, new WindowHandle(9));

        Assert.True(where.NextTo.IsNone);
        Assert.Equal(Area, where.Preview);
    }

    [Fact]
    public void A_point_in_the_gap_between_tiles_takes_the_whole_screen()
    {
        var withGaps = new Dictionary<WindowHandle, Rect>
        {
            [new WindowHandle(1)] = new Rect(0, 0, 990, 1000),
            [new WindowHandle(2)] = new Rect(1010, 0, 990, 1000),
        };

        DropTarget where = DropTargets.Resolve(withGaps, Area, 1000, 500, new WindowHandle(9));

        Assert.True(where.NextTo.IsNone);
    }
}
