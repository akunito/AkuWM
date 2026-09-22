using AkuWM.Core.Model;

namespace AkuWM.Core.Layout;

/// <param name="Inner">Between two tiled windows.</param>
/// <param name="Top">Between the tiling area and the edge of the work area.</param>
public readonly record struct Gaps(int Inner, int Top = 0, int Right = 0, int Bottom = 0, int Left = 0)
{
    public static readonly Gaps None = new(0);

    public static Gaps Uniform(int inner, int outer) => new(inner, outer, outer, outer, outer);

    /// <summary>
    /// The same gaps in the pixels of a scaled monitor.
    /// </summary>
    /// <remarks>
    /// Written as though the screen were at 100 %, so 8 px is 8 px of
    /// apparent space on both this desk's monitors rather than 8 px on one and
    /// 5 px of apparent space on the other.
    /// </remarks>
    public Gaps Scaled(double factor) => new(
        (int)Math.Round(Inner * factor),
        (int)Math.Round(Top * factor),
        (int)Math.Round(Right * factor),
        (int)Math.Round(Bottom * factor),
        (int)Math.Round(Left * factor));
}

/// <summary>
/// Turns a tree and a rectangle into one rectangle per window.
/// </summary>
/// <remarks>
/// <para>
/// A pure function, which is the whole point: the arithmetic that decides
/// where every window on the desk goes is exercised on Linux, against the
/// exact pixel counts of the two monitors this machine has, without a desk
/// anywhere near it.
/// </para>
/// <para>
/// Boundaries are rounded, not sizes. Rounding each window's width
/// independently leaves one-pixel seams and one-pixel overlaps that show up as
/// a flickering line between two windows; rounding the <em>edge</em> each one
/// ends at means the next one starts exactly there, and the last one closes on
/// the edge of the area by construction.
/// </para>
/// </remarks>
public static class TileGeometry
{
    /// <summary>Where every window in the tree goes, inside this area.</summary>
    public static Dictionary<WindowHandle, Rect> Compute(Tile? root, Rect area, Gaps gaps)
    {
        var into = new Dictionary<WindowHandle, Rect>();
        if (root is null)
        {
            return into;
        }

        Rect inner = area.Shrink(gaps.Top, gaps.Right, gaps.Bottom, gaps.Left);
        Place(root, inner.IsEmpty ? area : inner, gaps, into);
        return into;
    }

    private static void Place(Tile tile, Rect rect, Gaps gaps, Dictionary<WindowHandle, Rect> into)
    {
        if (tile.IsLeaf)
        {
            into[tile.Window] = rect;
            return;
        }

        bool horizontal = tile.Direction == SplitDirection.Horizontal;
        int count = tile.Children.Count;
        int span = horizontal ? rect.Width : rect.Height;

        // A tile too narrow for its children AND the gaps between them drops
        // the gaps: the positions still added the gap per child, so the
        // children of a 10 px tile with a 12 px gap sat at 0, 13 and 26 --
        // every one of them outside it. Sixteen windows on the portrait
        // monitor reach this.
        int gap = gaps.Inner;
        if (span - (gap * (count - 1)) < count)
        {
            gap = Math.Max(0, (span - count) / Math.Max(1, count - 1));
        }

        int extent = Math.Max(count, span - (gap * (count - 1)));
        int start = horizontal ? rect.X : rect.Y;

        double accumulated = 0;
        int previousEdge = 0;

        for (int i = 0; i < count; i++)
        {
            Tile child = tile.Children[i];
            accumulated += child.Share;

            // Every child that comes after this one still needs a pixel, so the
            // edge can never pass extent - remaining. Clamping to extent alone
            // let min exceed max once a rounded edge had already reached it,
            // and Math.Clamp throws on that: measured, a two-child 75/25 split
            // in a tile 14 px wide. WmLoop caught the throw and the desk then
            // redrew nothing for the rest of the run.
            int remaining = count - 1 - i;
            int edge = i == count - 1 ? extent : (int)Math.Round(accumulated * extent);
            edge = Math.Clamp(edge, previousEdge + 1, extent - remaining);

            int position = start + previousEdge + (i * gap);
            int size = edge - previousEdge;

            Place(
                child,
                horizontal
                    ? new Rect(position, rect.Y, size, rect.Height)
                    : new Rect(rect.X, position, rect.Width, size),
                gaps,
                into);

            previousEdge = edge;
        }
    }
}
