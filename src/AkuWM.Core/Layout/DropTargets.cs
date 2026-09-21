using AkuWM.Core.Model;

namespace AkuWM.Core.Layout;

/// <summary>Where a window dropped at a point would go in a layout.</summary>
/// <param name="NextTo">The tile it lands beside. None when the workspace is empty.</param>
/// <param name="Direction">Which way that tile is split to make room.</param>
/// <param name="Before">On the near side of it rather than the far side.</param>
/// <param name="Preview">The rectangle the window would occupy: what the drag outline draws.</param>
public readonly record struct DropTarget(
    WindowHandle NextTo,
    SplitDirection Direction,
    bool Before,
    Rect Preview);

/// <summary>
/// Reading a point on the screen as a place in the layout.
/// </summary>
/// <remarks>
/// Diego's choice, 2026-09-21: the half of the tile under the cursor decides.
/// Left half and the window goes to its left, right half to its right, and the
/// same up and down -- whichever of the two the pointer is further from the
/// middle in wins, so a drop near a corner still means something definite.
/// It is what sway and i3 do, and it is the only rule that lets the outline
/// show the exact rectangle the window is about to occupy.
/// </remarks>
public static class DropTargets
{
    public static DropTarget Resolve(
        IReadOnlyDictionary<WindowHandle, Rect> tiles, Rect area, int x, int y, WindowHandle dragged)
    {
        foreach ((WindowHandle handle, Rect rect) in tiles)
        {
            if (handle == dragged || rect.Width <= 0 || rect.Height <= 0 || !rect.Contains(x, y))
            {
                continue;
            }

            double across = ((x - rect.X) / (double)rect.Width) - 0.5;
            double down = ((y - rect.Y) / (double)rect.Height) - 0.5;

            if (Math.Abs(across) >= Math.Abs(down))
            {
                bool left = across < 0;
                int half = rect.Width / 2;

                return new DropTarget(
                    handle,
                    SplitDirection.Horizontal,
                    left,
                    left
                        ? rect with { Width = half }
                        : rect with { X = rect.X + half, Width = rect.Width - half });
            }

            bool up = down < 0;
            int halfDown = rect.Height / 2;

            return new DropTarget(
                handle,
                SplitDirection.Vertical,
                up,
                up
                    ? rect with { Height = halfDown }
                    : rect with { Y = rect.Y + halfDown, Height = rect.Height - halfDown });
        }

        // On no tile at all: an empty workspace, the gaps between tiles, or the
        // one tile there is being the window we are dragging. It takes the
        // whole work area, which is also what the outline should show.
        return new DropTarget(WindowHandle.None, SplitDirection.Horizontal, false, area);
    }
}
