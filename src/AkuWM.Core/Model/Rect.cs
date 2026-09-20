namespace AkuWM.Core.Model;

/// <summary>A rectangle in virtual-screen pixels, top-left origin.</summary>
/// <remarks>
/// Windows hands out rectangles as left/top/right/bottom; AkuWM works in
/// position and size, because that is what a layout divides and what
/// <c>SetWindowPos</c> wants back. The conversion happens once, at the edge.
/// </remarks>
public readonly record struct Rect(int X, int Y, int Width, int Height)
{
    public static readonly Rect Empty = new(0, 0, 0, 0);

    public int Left => X;

    public int Top => Y;

    public int Right => X + Width;

    public int Bottom => Y + Height;

    public int Area => Width * Height;

    public bool IsEmpty => Width <= 0 || Height <= 0;

    public static Rect FromEdges(int left, int top, int right, int bottom) =>
        new(left, top, right - left, bottom - top);

    public Rect Inflate(int by) => new(X - by, Y - by, Width + (2 * by), Height + (2 * by));

    /// <summary>Shrinks by a different amount on each side: top, right, bottom, left.</summary>
    public Rect Shrink(int top, int right, int bottom, int left) =>
        new(X + left, Y + top, Width - left - right, Height - top - bottom);

    public bool Contains(int x, int y) => x >= Left && x < Right && y >= Top && y < Bottom;

    public bool Contains(Rect other) =>
        other.Left >= Left && other.Top >= Top && other.Right <= Right && other.Bottom <= Bottom;

    public Rect Intersect(Rect other)
    {
        int left = Math.Max(Left, other.Left);
        int top = Math.Max(Top, other.Top);
        int right = Math.Min(Right, other.Right);
        int bottom = Math.Min(Bottom, other.Bottom);
        return right <= left || bottom <= top ? Empty : FromEdges(left, top, right, bottom);
    }

    /// <summary>How much of this rectangle lies inside another, 0 to 1.</summary>
    public double FractionInside(Rect other) =>
        Area <= 0 ? 0 : (double)Intersect(other).Area / Area;

    public override string ToString() => $"{X},{Y} {Width}x{Height}";
}
