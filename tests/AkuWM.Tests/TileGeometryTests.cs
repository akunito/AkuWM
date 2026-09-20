using AkuWM.Core.Layout;
using AkuWM.Core.Model;
using Xunit;

namespace AkuWM.Tests;

/// <summary>
/// The arithmetic that decides where every window on the desk goes, checked
/// against the pixel counts of the two monitors this machine actually has.
/// </summary>
public class TileGeometryTests
{
    /// <summary>The main monitor's work area: 4K, taskbar at the bottom.</summary>
    private static readonly Rect Main = new(0, 42, 3840, 2118);

    /// <summary>The portrait one.</summary>
    private static readonly Rect Second = new(3840, -373, 1440, 2525);

    private static Tile Leaf(long handle) => Tile.Leaf(new WindowHandle(handle));

    private static Rect Of(Dictionary<WindowHandle, Rect> rects, long handle) => rects[new WindowHandle(handle)];

    [Fact]
    public void One_window_gets_the_whole_area()
    {
        Dictionary<WindowHandle, Rect> rects = TileGeometry.Compute(Leaf(1), Main, Gaps.None);

        Assert.Equal(Main, Of(rects, 1));
    }

    [Fact]
    public void Two_side_by_side_split_the_width_exactly()
    {
        Tile root = Tile.Split(SplitDirection.Horizontal, Leaf(1), Leaf(2));

        Dictionary<WindowHandle, Rect> rects = TileGeometry.Compute(root, Main, Gaps.None);

        Assert.Equal(new Rect(0, 42, 1920, 2118), Of(rects, 1));
        Assert.Equal(new Rect(1920, 42, 1920, 2118), Of(rects, 2));
    }

    [Fact]
    public void Three_columns_leave_no_seam_and_no_overlap()
    {
        // 3840 does not divide by three into the same number three times once
        // gaps are in play, and a one-pixel seam between two windows is
        // visible as a flickering line.
        Tile root = Tile.Split(SplitDirection.Horizontal, Leaf(1), Leaf(2), Leaf(3));

        Dictionary<WindowHandle, Rect> rects = TileGeometry.Compute(root, Main, new Gaps(12));

        Assert.Equal(Of(rects, 1).Right + 12, Of(rects, 2).Left);
        Assert.Equal(Of(rects, 2).Right + 12, Of(rects, 3).Left);
        Assert.Equal(Main.Right, Of(rects, 3).Right);
        Assert.Equal(Main.Left, Of(rects, 1).Left);
    }

    [Fact]
    public void The_vertical_monitor_stacks()
    {
        Tile root = Tile.Split(SplitDirection.Vertical, Leaf(1), Leaf(2));

        Dictionary<WindowHandle, Rect> rects = TileGeometry.Compute(root, Second, Gaps.None);

        Assert.Equal(Second.Width, Of(rects, 1).Width);
        Assert.Equal(Of(rects, 1).Bottom, Of(rects, 2).Top);
        Assert.Equal(Second.Bottom, Of(rects, 2).Bottom);
    }

    [Fact]
    public void Shares_are_respected()
    {
        Tile root = Tile.Split(SplitDirection.Horizontal, Leaf(1), Leaf(2));
        root.Children[0].Share = 0.7;
        root.Children[1].Share = 0.3;
        root.Normalise();

        Dictionary<WindowHandle, Rect> rects = TileGeometry.Compute(root, Main, Gaps.None);

        Assert.Equal(2688, Of(rects, 1).Width);
        Assert.Equal(1152, Of(rects, 2).Width);
    }

    [Fact]
    public void Outer_gaps_come_off_the_area_once()
    {
        Tile root = Tile.Split(SplitDirection.Horizontal, Leaf(1), Leaf(2));

        Dictionary<WindowHandle, Rect> rects = TileGeometry.Compute(root, Main, Gaps.Uniform(0, 20));

        // Twenty pixels of air at the edge of the screen, and none between the
        // two windows because the inner gap is zero.
        Assert.Equal(20, Of(rects, 1).Left);
        Assert.Equal(62, Of(rects, 1).Top);
        Assert.Equal(3820, Of(rects, 2).Right);
        Assert.Equal(Of(rects, 1).Right, Of(rects, 2).Left);
    }

    [Fact]
    public void A_nested_split_divides_only_its_own_half()
    {
        // The shape a third window makes when it opens on top of the second:
        // H[ 1  V[ 2  3 ] ]
        Tile root = Tile.Split(
            SplitDirection.Horizontal,
            Leaf(1),
            Tile.Split(SplitDirection.Vertical, Leaf(2), Leaf(3)));

        Dictionary<WindowHandle, Rect> rects = TileGeometry.Compute(root, Main, Gaps.None);

        Assert.Equal(1920, Of(rects, 1).Width);
        Assert.Equal(2118, Of(rects, 1).Height);
        Assert.Equal(1920, Of(rects, 2).Width);
        Assert.Equal(1059, Of(rects, 2).Height);
        Assert.Equal(Of(rects, 2).Bottom, Of(rects, 3).Top);
    }

    [Fact]
    public void Gaps_scale_with_the_monitor()
    {
        // 8 px written in the configuration is 12 px on the 150 % monitor, so
        // the air between two windows looks the same on both screens.
        Assert.Equal(12, Gaps.Uniform(8, 0).Scaled(1.5).Inner);
        Assert.Equal(10, Gaps.Uniform(8, 0).Scaled(1.25).Inner);
    }

    [Fact]
    public void A_tree_never_places_a_window_outside_its_area()
    {
        Tile root = Tile.Split(
            SplitDirection.Horizontal,
            Leaf(1),
            Tile.Split(SplitDirection.Vertical, Leaf(2), Leaf(3), Leaf(4)),
            Leaf(5));

        Dictionary<WindowHandle, Rect> rects = TileGeometry.Compute(root, Main, new Gaps(12, 8, 8, 8, 8));

        foreach ((WindowHandle window, Rect rect) in rects)
        {
            Assert.True(Main.Contains(rect), $"{window} at {rect} is outside {Main}");
            Assert.True(rect.Width > 0 && rect.Height > 0, $"{window} has no size: {rect}");
        }
    }

    [Theory]
    [InlineData(8, 0.75)]
    [InlineData(14, 0.75)]
    [InlineData(22, 0.95)]
    [InlineData(1, 0.5)]
    [InlineData(3, 0.99)]
    public void A_tile_narrower_than_its_gap_still_produces_rectangles(int width, double share)
    {
        // Measured: a nest sixteen deep on the portrait monitor already makes a
        // 1 px tile. Math.Clamp with min > max threw here, WmLoop caught it,
        // and the desk then redrew nothing for the rest of the run: whatever
        // was cloaked stayed cloaked and workspace switching became a no-op.
        Tile root = Tile.Split(SplitDirection.Horizontal, Leaf(1), Leaf(2));
        root.Children[0].Share = share;
        root.Children[1].Share = 1 - share;
        root.Normalise();

        Dictionary<WindowHandle, Rect> rects =
            TileGeometry.Compute(root, new Rect(0, 0, width, 100), new Gaps(12));

        Assert.Equal(2, rects.Count);
        Assert.All(rects.Values, r => Assert.True(r.Width > 0 && r.Height > 0, $"{r}"));
    }

    [Fact]
    public void No_share_and_no_area_ever_throws()
    {
        var random = new Random(20260920);

        for (int i = 0; i < 2000; i++)
        {
            int children = random.Next(2, 6);
            var leaves = new Tile[children];
            for (int c = 0; c < children; c++)
            {
                leaves[c] = Leaf(c + 1);
            }

            Tile root = Tile.Split(
                random.Next(2) == 0 ? SplitDirection.Horizontal : SplitDirection.Vertical, leaves);

            for (int c = 0; c < children; c++)
            {
                root.Children[c].Share = random.NextDouble() + 0.001;
            }

            root.Normalise();

            var area = new Rect(0, 0, random.Next(1, 200), random.Next(1, 200));
            Dictionary<WindowHandle, Rect> rects = TileGeometry.Compute(root, area, new Gaps(random.Next(0, 24)));

            Assert.Equal(children, rects.Count);
            Assert.All(rects.Values, r => Assert.True(r.Width > 0 && r.Height > 0, $"{area} -> {r}"));
        }
    }

    [Fact]
    public void An_area_too_small_for_the_windows_still_gives_each_one_something()
    {
        // Not a real desk, but a display change can hand the layout a
        // rectangle far smaller than it had, and a window with a negative
        // width is a SetWindowPos that fails.
        Tile root = Tile.Split(SplitDirection.Horizontal, Leaf(1), Leaf(2), Leaf(3));

        Dictionary<WindowHandle, Rect> rects = TileGeometry.Compute(root, new Rect(0, 0, 10, 10), new Gaps(12));

        Assert.All(rects.Values, r => Assert.True(r.Width > 0 && r.Height > 0, $"{r}"));
    }
}
