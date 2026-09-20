using AkuWM.Core.Model;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace AkuWM.Platform;

/// <param name="Window">Which window.</param>
/// <param name="Frame">Where its visible frame should end up.</param>
public readonly record struct Placement(WindowHandle Window, Rect Frame);

/// <summary>
/// Moving windows, in batches.
/// </summary>
/// <remarks>
/// <para>
/// A workspace switch moves a dozen windows at once. Done one
/// <c>SetWindowPos</c> at a time, each one is a separate trip through the
/// window manager and the screen visibly reflows; done through a deferred
/// batch, they land together.
/// </para>
/// <para>
/// The rectangle handed in is the <em>visible frame</em>, so the invisible
/// resize border is added back here -- placing a window at the rectangle the
/// layout computed, without that correction, leaves it nine pixels off on
/// three sides at 150 %.
/// </para>
/// </remarks>
public static class Win32Position
{
    public static int Place(IReadOnlyList<Placement> placements, bool activate = false)
    {
        if (placements.Count == 0)
        {
            return 0;
        }

        SET_WINDOW_POS_FLAGS flags =
            SET_WINDOW_POS_FLAGS.SWP_NOZORDER
            | SET_WINDOW_POS_FLAGS.SWP_NOOWNERZORDER
            | SET_WINDOW_POS_FLAGS.SWP_ASYNCWINDOWPOS;

        if (!activate)
        {
            flags |= SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE;
        }

        HDWP batch = PInvoke.BeginDeferWindowPos(placements.Count);
        if (batch == default)
        {
            return 0;
        }

        int placed = 0;
        foreach (Placement placement in placements)
        {
            var hwnd = new HWND((IntPtr)placement.Window.Value);
            Rect target = WithBorder(hwnd, placement.Frame);

            batch = PInvoke.DeferWindowPos(
                batch, hwnd, HWND.Null, target.X, target.Y, target.Width, target.Height, flags);

            if (batch == default)
            {
                return placed;
            }

            placed++;
        }

        return PInvoke.EndDeferWindowPos(batch) ? placed : 0;
    }

    /// <summary>One window, when there is only one.</summary>
    public static bool Place(WindowHandle window, Rect frame, bool activate = false) =>
        Place([new Placement(window, frame)], activate) == 1;

    /// <summary>
    /// Turns a visible-frame rectangle into the outer rectangle
    /// <c>SetWindowPos</c> expects.
    /// </summary>
    private static Rect WithBorder(HWND hwnd, Rect frame)
    {
        if (!PInvoke.GetWindowRect(hwnd, out RECT outer))
        {
            return frame;
        }

        Rect? visible = Win32Windows.FrameBoundsOf(hwnd);
        if (visible is not { } bounds || bounds.IsEmpty)
        {
            return frame;
        }

        int left = bounds.Left - outer.left;
        int top = bounds.Top - outer.top;
        int right = outer.right - bounds.Right;
        int bottom = outer.bottom - bounds.Bottom;

        return new Rect(
            frame.X - left,
            frame.Y - top,
            frame.Width + left + right,
            frame.Height + top + bottom);
    }
}
