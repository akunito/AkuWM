using AkuWM.Core.Model;

namespace AkuWM.Core.Layout;

/// <summary>
/// One workspace's tiling: the tree, and everything that changes it.
/// </summary>
/// <remarks>
/// <para>
/// The operations are the gestures: a window opens, a window closes, focus
/// moves left, this window swaps with the one above it, this edge moves five
/// points to the right. Every one of them leaves a tree that
/// <see cref="Tile.Check"/> accepts, which the tests assert after each call --
/// a tiling tree that drifts does not crash, it puts one window somewhere
/// strange, once, and nobody can reproduce it.
/// </para>
/// <para>
/// Direction is answered geometrically, from the rectangles the tree actually
/// produces, rather than by walking the tree. The two disagree exactly where
/// it matters: the window visually to the right of this one is often not its
/// sibling, and a person means the one they can see.
/// </para>
/// </remarks>
public sealed class TilingTree
{
    /// <summary>How small a tile may be squeezed, as a share of its split.</summary>
    public const double MinimumShare = 0.05;

    public Tile? Root { get; private set; }

    public bool IsEmpty => Root is null;

    public IEnumerable<WindowHandle> Windows => Root?.Leaves().Select(l => l.Window) ?? [];

    public int Count => Root?.Leaves().Count() ?? 0;

    public bool Contains(WindowHandle window) => Root?.Find(window) is not null;

    /// <summary>A copy to try a change on. The drag outline asks it where a drop would land.</summary>
    public TilingTree Clone() => new() { Root = Root?.Clone() };

    /// <summary>
    /// Becomes a saved tree again, less the windows that are gone. What a
    /// monitor's workspace gets back when the monitor returns.
    /// </summary>
    public void Restore(TilingTree saved, Func<WindowHandle, bool> stillThere)
    {
        Root = saved.Root?.Clone();
        foreach (WindowHandle window in Windows.ToList())
        {
            if (!stillThere(window))
            {
                Remove(window);
            }
        }
    }

    public Dictionary<WindowHandle, Rect> Rects(Rect area, Gaps gaps) =>
        TileGeometry.Compute(Root, area, gaps);

    /// <summary>
    /// Puts a window into the tree, next to another one.
    /// </summary>
    /// <param name="nextTo">
    /// Usually the focused window. When it is not in this tree the new window
    /// goes at the end, which is what happens to a window that opens on a
    /// workspace nobody is looking at.
    /// </param>
    /// <param name="direction">Which way to split, when a split is needed.</param>
    /// <param name="before">
    /// On the other side of <paramref name="nextTo"/>. What dropping a window
    /// on the LEFT half of a tile means, as against the right.
    /// </param>
    public void Add(WindowHandle window, WindowHandle nextTo, SplitDirection direction, bool before = false)
    {
        if (window.IsNone || Contains(window))
        {
            return;
        }

        Tile leaf = Tile.Leaf(window);

        if (Root is null)
        {
            Root = leaf;
            return;
        }

        Tile? beside = Root.Find(nextTo);
        if (beside is null)
        {
            // No anchor: join the root if it already splits the right way,
            // otherwise wrap the whole workspace in a new split.
            if (!Root.IsLeaf && Root.Direction == direction)
            {
                if (before)
                {
                    Root.Insert(0, leaf);
                }
                else
                {
                    Root.Add(leaf);
                }
            }
            else
            {
                Root = before
                    ? Tile.Split(direction, leaf, Root)
                    : Tile.Split(direction, Root, leaf);
            }

            return;
        }

        // A sibling when the split already runs this way -- which is what
        // makes three windows on a horizontal workspace three columns rather
        // than a column and a nested pair.
        if (beside.Parent is { } parent && parent.Direction == direction)
        {
            parent.Insert(before ? beside.IndexInParent : beside.IndexInParent + 1, leaf);
            return;
        }

        Tile split = before
            ? Tile.Split(direction, leaf, Tile.Leaf(beside.Window))
            : Tile.Split(direction, Tile.Leaf(beside.Window), leaf);
        if (beside.Parent is { } grandparent)
        {
            grandparent.ReplaceChild(beside, split);
        }
        else
        {
            Root = split;
        }
    }

    /// <summary>Takes a window out, and collapses the split it leaves behind.</summary>
    public bool Remove(WindowHandle window)
    {
        Tile? leaf = Root?.Find(window);
        if (leaf is null)
        {
            return false;
        }

        if (leaf.Parent is not { } parent)
        {
            Root = null;
            return true;
        }

        parent.RemoveChild(leaf);
        Collapse(parent);
        return true;
    }

    /// <summary>
    /// A split with one child left is not a split: its child takes its place,
    /// and its share, so the space goes back to the windows that are still
    /// there.
    /// </summary>
    private void Collapse(Tile split)
    {
        while (split.Children.Count == 1)
        {
            Tile only = split.Children[0];
            split.RemoveChild(only);

            if (split.Parent is { } parent)
            {
                parent.ReplaceChild(split, only);
                split = parent;
            }
            else
            {
                only.Share = 1.0;
                Root = only;
                return;
            }
        }
    }

    /// <summary>
    /// The window a person means when they say "the one to the left".
    /// </summary>
    /// <remarks>
    /// Geometric, and it requires the two to actually face each other: a
    /// candidate must overlap the source across the direction of travel. Only
    /// when nothing does is the nearest by centre used, which is what makes
    /// "left" from a tall window next to two short ones land on the one beside
    /// it rather than the one diagonally away.
    /// </remarks>
    public WindowHandle Neighbour(WindowHandle from, Direction direction, Rect area, Gaps gaps)
    {
        Dictionary<WindowHandle, Rect> rects = Rects(area, gaps);
        if (!rects.TryGetValue(from, out Rect source))
        {
            return WindowHandle.None;
        }

        var facing = new List<(WindowHandle Window, int Distance)>();
        var behind = new List<(WindowHandle Window, int Distance)>();

        foreach ((WindowHandle window, Rect rect) in rects)
        {
            if (window == from)
            {
                continue;
            }

            int distance = Ahead(source, rect, direction);
            if (distance < 0)
            {
                continue;
            }

            if (Overlaps(source, rect, direction))
            {
                facing.Add((window, distance));
            }
            else
            {
                behind.Add((window, distance));
            }
        }

        List<(WindowHandle Window, int Distance)> candidates = facing.Count > 0 ? facing : behind;

        return candidates.Count == 0
            ? WindowHandle.None
            : candidates.OrderBy(c => c.Distance).ThenBy(c => c.Window.Value).First().Window;
    }

    /// <summary>How far ahead in this direction, or -1 when it is not ahead at all.</summary>
    private static int Ahead(Rect source, Rect other, Direction direction)
    {
        int gap = direction switch
        {
            Direction.Left => source.Left - other.Right,
            Direction.Right => other.Left - source.Right,
            Direction.Up => source.Top - other.Bottom,
            _ => other.Top - source.Bottom,
        };

        // Touching counts as ahead; a gap of zero is two windows side by side
        // with no inner gap configured.
        return gap >= 0 ? gap : -1;
    }

    private static bool Overlaps(Rect source, Rect other, Direction direction) =>
        direction.Axis() == SplitDirection.Horizontal
            ? other.Bottom > source.Top && other.Top < source.Bottom
            : other.Right > source.Left && other.Left < source.Right;

    /// <summary>
    /// Moves a window one place in a direction.
    /// </summary>
    /// <remarks>
    /// Two windows that are siblings swap, which is what a person expects from
    /// a pair side by side. Anything else is a move: the window leaves where
    /// it was, the split it leaves collapses, and it lands beside the window
    /// it was aimed at, on the correct side of it.
    /// </remarks>
    public bool Move(WindowHandle window, Direction direction, Rect area, Gaps gaps)
    {
        Tile? leaf = Root?.Find(window);
        WindowHandle target = Neighbour(window, direction, area, gaps);
        if (leaf is null || target.IsNone)
        {
            return false;
        }

        Tile neighbour = Root!.Find(target)!;

        if (ReferenceEquals(leaf.Parent, neighbour.Parent) && leaf.Parent is { } shared)
        {
            shared.SwapChildren(leaf.IndexInParent, neighbour.IndexInParent);
            return true;
        }

        if (!Remove(window))
        {
            return false;
        }

        // The tree changed underneath: the neighbour may have a different
        // parent now, because the split the window left behind collapsed.
        neighbour = Root?.Find(target) ?? throw new InvalidOperationException(
            $"the window {target} that {window} was moved towards is no longer in the tree");

        Tile moved = Tile.Leaf(window);
        SplitDirection axis = direction.Axis();

        if (neighbour.Parent is { } parent && parent.Direction == axis)
        {
            // Landing beside it: in front of it when moving towards the
            // origin, behind it when moving away.
            parent.Insert(neighbour.IndexInParent + (direction.IsBackwards() ? 0 : 1), moved);
            return true;
        }

        Tile split = direction.IsBackwards()
            ? Tile.Split(axis, moved, Tile.Leaf(target))
            : Tile.Split(axis, Tile.Leaf(target), moved);

        if (neighbour.Parent is { } grandparent)
        {
            grandparent.ReplaceChild(neighbour, split);
        }
        else
        {
            Root = split;
        }

        return true;
    }

    /// <summary>
    /// Moves one edge of a window, taking the space from whatever is on the
    /// other side of that edge.
    /// </summary>
    /// <remarks>
    /// The edge may not belong to this window's own split -- the right edge of
    /// a window in a nested column is the right edge of the column -- so the
    /// search walks up until it finds a split that has something on that side
    /// to take from.
    /// </remarks>
    /// <param name="by">
    /// A fraction of the split, e.g. 0.05 for five points. Negative moves the
    /// edge the other way, which is what <c>resize --width -5%</c> means.
    /// </param>
    public bool Resize(WindowHandle window, Direction direction, double by)
    {
        Tile? node = Root?.Find(window);
        SplitDirection axis = direction.Axis();
        int towards = direction.IsBackwards() ? -1 : 1;

        while (node?.Parent is { } parent)
        {
            int at = node.IndexInParent;
            int neighbourAt = at + towards;

            // The other side when there is no neighbour the way we are growing:
            // making the LAST window in a row wider moves its other edge, and
            // asking for it used to do nothing at all, because the only
            // neighbour considered was the one that does not exist. The share
            // still moves the same way -- into this tile, out of whichever
            // neighbour there is. Found by tests/wm 2026-09-21, in a case that
            // swapped two windows and then resized the one now on the end.
            if (parent.Direction == axis && (neighbourAt < 0 || neighbourAt >= parent.Children.Count))
            {
                neighbourAt = at - towards;
            }

            if (parent.Direction == axis && neighbourAt >= 0 && neighbourAt < parent.Children.Count)
            {
                Tile other = parent.Children[neighbourAt];

                // Neither side may be squeezed out of existence, whichever way
                // the edge is travelling -- and never past zero: a neighbour
                // already under the minimum (a third window inserted into a
                // 95/5 pair takes a third of each) turned "wider" into
                // narrower, because the room to give was negative and was
                // given anyway.
                double give = by > 0
                    ? Math.Clamp(by, 0, Math.Max(0, other.Share - MinimumShare))
                    : Math.Clamp(by, Math.Min(0, MinimumShare - node.Share), 0);

                if (Math.Abs(give) < 1e-9)
                {
                    return false;
                }

                node.Share += give;
                other.Share -= give;
                parent.Normalise();
                return true;
            }

            node = parent;
        }

        return false;
    }

    /// <summary>Turns the split this window is in through ninety degrees.</summary>
    public bool ToggleDirection(WindowHandle window)
    {
        if (Root?.Find(window)?.Parent is not { } parent)
        {
            return false;
        }

        parent.Direction = parent.Direction == SplitDirection.Horizontal
            ? SplitDirection.Vertical
            : SplitDirection.Horizontal;

        return true;
    }

    /// <summary>The windows in tree order, which is what a switcher walks.</summary>
    public IReadOnlyList<WindowHandle> InOrder() => [.. Windows];

    public override string ToString() => Root?.ToString() ?? "(empty)";
}
