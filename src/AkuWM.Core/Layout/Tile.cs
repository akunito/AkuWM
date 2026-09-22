using AkuWM.Core.Model;

namespace AkuWM.Core.Layout;

/// <summary>Which way a split divides its children.</summary>
public enum SplitDirection
{
    /// <summary>Side by side, left to right.</summary>
    Horizontal,

    /// <summary>Stacked, top to bottom.</summary>
    Vertical,
}

/// <summary>A direction on the screen, as a chord names it.</summary>
public enum Direction
{
    Left,
    Right,
    Up,
    Down,
}

public static class Directions
{
    public static SplitDirection Axis(this Direction direction) =>
        direction is Direction.Left or Direction.Right ? SplitDirection.Horizontal : SplitDirection.Vertical;

    /// <summary>Towards the origin (left, up) rather than away from it.</summary>
    public static bool IsBackwards(this Direction direction) =>
        direction is Direction.Left or Direction.Up;

    public static Direction Opposite(this Direction direction) => direction switch
    {
        Direction.Left => Direction.Right,
        Direction.Right => Direction.Left,
        Direction.Up => Direction.Down,
        _ => Direction.Up,
    };
}

/// <summary>
/// One node of a workspace's tree: either a window, or a split holding other
/// nodes.
/// </summary>
/// <remarks>
/// <para>
/// sway's shape, and the reason for it is that a tiling desk is a sequence of
/// decisions rather than a grid: "put this one next to that one, and that pair
/// above the third". A tree records those decisions, so closing the third
/// window gives the pair the space back and nothing else moves.
/// </para>
/// <para>
/// A leaf holds a window and no children. A split holds two or more children
/// and no window. Nothing else is a valid tile, and <see cref="Check"/> says
/// so out loud, because the operations are where a window manager either keeps
/// its tree honest or starts placing windows in rectangles that do not exist.
/// </para>
/// <para>
/// <see cref="Share"/> is the fraction of the parent's axis this child takes.
/// The children of a split always sum to one; every operation that changes the
/// membership renormalises, so a share is never read as an absolute size.
/// </para>
/// </remarks>
public sealed class Tile
{
    private readonly List<Tile> _children = [];

    private Tile(WindowHandle window) => Window = window;

    private Tile(SplitDirection direction, IEnumerable<Tile> children)
    {
        Direction = direction;
        foreach (Tile child in children)
        {
            child.Parent = this;
            _children.Add(child);
        }

        Normalise();
    }

    public static Tile Leaf(WindowHandle window) => new(window);

    public static Tile Split(SplitDirection direction, params Tile[] children) => new(direction, children);

    /// <summary>The window, for a leaf. <see cref="WindowHandle.None"/> for a split.</summary>
    public WindowHandle Window { get; } = WindowHandle.None;

    public SplitDirection Direction { get; set; }

    public IReadOnlyList<Tile> Children => _children;

    public Tile? Parent { get; private set; }

    public double Share { get; set; } = 1.0;

    public bool IsLeaf => _children.Count == 0;

    public Tile Root => Parent?.Root ?? this;

    /// <summary>Every leaf under this node, left to right, top to bottom.</summary>
    public IEnumerable<Tile> Leaves()
    {
        if (IsLeaf)
        {
            yield return this;
            yield break;
        }

        foreach (Tile child in _children)
        {
            foreach (Tile leaf in child.Leaves())
            {
                yield return leaf;
            }
        }
    }

    public IEnumerable<Tile> Descendants()
    {
        yield return this;
        foreach (Tile child in _children)
        {
            foreach (Tile node in child.Descendants())
            {
                yield return node;
            }
        }
    }

    /// <summary>
    /// The leaf holding a window, anywhere under this node.
    /// </summary>
    /// <remarks>
    /// Walked, not enumerated. `Leaves()` is a recursive iterator, so every
    /// leaf is yielded up through one state machine per level, and the
    /// predicate allocated a closure and a delegate per call. That was
    /// affordable when only the tree's own mutators called this; the query
    /// path calls it once per window while building the JSON, which made
    /// serialising one workspace quadratic in its windows -- and the bar asks
    /// for that on every event, per widget.
    /// </remarks>
    public Tile? Find(WindowHandle window)
    {
        if (IsLeaf)
        {
            return Window == window ? this : null;
        }

        for (int i = 0; i < _children.Count; i++)
        {
            if (_children[i].Find(window) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    public int IndexInParent => Parent?._children.IndexOf(this) ?? -1;

    /// <summary>A deep copy, for asking "what if" without touching the layout.</summary>
    public Tile Clone()
    {
        if (IsLeaf)
        {
            return new Tile(Window) { Share = Share };
        }

        var copy = new Tile(Direction, _children.Select(c => c.Clone())) { Share = Share };
        return copy;
    }

    // ---- changing the tree ------------------------------------------------
    // Every one of these keeps two things true: a split always has at least
    // two children, and the shares of a split always sum to one. A tree that
    // drifts from either places windows in rectangles that do not exist.

    /// <summary>
    /// Adds a child, giving it an equal share and taking that share from the
    /// others in proportion.
    /// </summary>
    /// <remarks>
    /// A third window on a row of two is a third of the row, not half of it --
    /// which is what happens if the newcomer simply arrives with a share of
    /// one and everything is renormalised. Taking it from the others in
    /// proportion also means a pair someone has deliberately resized to 70/30
    /// stays at that ratio between themselves.
    /// </remarks>
    internal void Insert(int at, Tile child)
    {
        child.Parent = this;

        int after = _children.Count + 1;
        foreach (Tile existing in _children)
        {
            existing.Share *= (after - 1.0) / after;
        }

        child.Share = 1.0 / after;
        _children.Insert(Math.Clamp(at, 0, _children.Count), child);
        Normalise();
    }

    internal void Add(Tile child) => Insert(_children.Count, child);

    internal void RemoveChild(Tile child)
    {
        if (_children.Remove(child))
        {
            child.Parent = null;
            Normalise();
        }
    }

    internal void ReplaceChild(Tile existing, Tile replacement)
    {
        int at = _children.IndexOf(existing);
        if (at < 0)
        {
            return;
        }

        replacement.Share = existing.Share;
        replacement.Parent = this;
        existing.Parent = null;
        _children[at] = replacement;
    }

    internal void SwapChildren(int a, int b)
    {
        (_children[a], _children[b]) = (_children[b], _children[a]);
        (_children[a].Share, _children[b].Share) = (_children[b].Share, _children[a].Share);
    }

    /// <summary>Makes the children's shares sum to one, keeping their ratios.</summary>
    internal void Normalise()
    {
        if (_children.Count == 0)
        {
            return;
        }

        double total = _children.Sum(c => c.Share);
        if (total <= 0)
        {
            foreach (Tile child in _children)
            {
                child.Share = 1.0 / _children.Count;
            }

            return;
        }

        foreach (Tile child in _children)
        {
            child.Share /= total;
        }
    }

    /// <summary>
    /// What is wrong with this tree, or an empty list.
    /// </summary>
    /// <remarks>
    /// Called by the tests after every operation. A window manager whose tree
    /// is subtly wrong does not crash; it puts one window somewhere strange,
    /// once, on a Tuesday.
    /// </remarks>
    public IReadOnlyList<string> Check()
    {
        var wrong = new List<string>();
        var seen = new HashSet<long>();

        foreach (Tile node in Descendants())
        {
            if (node.IsLeaf)
            {
                if (node.Window.IsNone)
                {
                    wrong.Add("a leaf with no window");
                }
                else if (!seen.Add(node.Window.Value))
                {
                    wrong.Add($"{node.Window} is in the tree twice");
                }

                continue;
            }

            if (!node.Window.IsNone)
            {
                wrong.Add($"a split that also holds {node.Window}");
            }

            if (node._children.Count < 2)
            {
                wrong.Add($"a split with {node._children.Count} child(ren); it should have collapsed");
            }

            double sum = node._children.Sum(c => c.Share);
            if (Math.Abs(sum - 1.0) > 0.0001)
            {
                wrong.Add($"a split whose shares sum to {sum:F4}");
            }

            foreach (Tile child in node._children)
            {
                if (!ReferenceEquals(child.Parent, node))
                {
                    wrong.Add("a child whose parent does not point back at it");
                }
            }
        }

        return wrong;
    }

    public override string ToString() =>
        IsLeaf
            ? $"{Window}"
            : $"{(Direction == SplitDirection.Horizontal ? "H" : "V")}[{string.Join(" ", _children)}]";
}
