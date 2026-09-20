using AkuWM.Core.Logging;
using AkuWM.Core.Model;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace AkuWM.Platform;

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
    /// <summary>
    /// Moves and sizes several windows so they land together.
    /// </summary>
    /// <remarks>
    /// <strong>No <c>SWP_ASYNCWINDOWPOS</c> here.</strong> Outside a batch it
    /// is the flag that keeps a window manager from blocking behind an
    /// application that is not answering, and <c>SetWindowPos</c> takes it
    /// happily. <c>DeferWindowPos</c> refuses the whole batch for it, with
    /// ERROR_INVALID_PARAMETER on the first window -- measured on this desk,
    /// after a workspace switch that moved nothing and said nothing. The
    /// failure is silent in the worst way: the batch handle comes back null,
    /// every move already added to it is lost with it, and the windows simply
    /// stay where they were.
    /// </remarks>
    public static int Place(IReadOnlyList<Placement> placements, bool activate = false)
    {
        if (placements.Count == 0)
        {
            return 0;
        }

        SET_WINDOW_POS_FLAGS flags =
            SET_WINDOW_POS_FLAGS.SWP_NOZORDER
            | SET_WINDOW_POS_FLAGS.SWP_NOOWNERZORDER;

        if (!activate)
        {
            flags |= SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE;
        }

        HDWP batch = PInvoke.BeginDeferWindowPos(placements.Count);
        if (batch == default)
        {
            Log.Warn($"the window manager refused to start a batch of {placements.Count}; placing them one by one");
            return PlaceOneByOne(placements, flags);
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
                // The batch is gone, and with it every move already added to
                // it. Whatever has been asked for so far has not happened.
                int why = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                Log.Warn(
                    $"a batch of {placements.Count} was dropped after {placed} "
                    + $"(error {why}) on {placement.Window} -> {target}; placing them one by one");
                return PlaceOneByOne(placements, flags);
            }

            placed++;
        }

        if (PInvoke.EndDeferWindowPos(batch))
        {
            return placed;
        }

        Log.Warn($"a batch of {placements.Count} was refused at the end; placing them one by one");
        return PlaceOneByOne(placements, flags);
    }

    /// <summary>
    /// The fallback when a batch will not go through.
    /// </summary>
    /// <remarks>
    /// A dropped batch takes every move in it with it, silently: the windows
    /// simply do not move, and nothing says why. Measured on this desk, that
    /// is exactly what happened to the first three windows AkuWM ever tried to
    /// arrange. One call each is slower and visibly reflows, which is a great
    /// deal better than a window manager that does nothing at all.
    /// </remarks>
    private static int PlaceOneByOne(IReadOnlyList<Placement> placements, SET_WINDOW_POS_FLAGS flags)
    {
        int placed = 0;

        foreach (Placement placement in placements)
        {
            var hwnd = new HWND((IntPtr)placement.Window.Value);
            Rect target = WithBorder(hwnd, placement.Frame);

            // Asynchronous here, where it is allowed: a window whose
            // application has stopped answering must not stop the desk.
            if (PInvoke.SetWindowPos(
                hwnd,
                HWND.Null,
                target.X,
                target.Y,
                target.Width,
                target.Height,
                flags | SET_WINDOW_POS_FLAGS.SWP_ASYNCWINDOWPOS))
            {
                placed++;
            }
            else
            {
                Log.Debug(() => $"{placement.Window} refused {target}");
            }
        }

        return placed;
    }

    /// <summary>
    /// One window, one call, no batch.
    /// </summary>
    /// <remarks>
    /// This one does take <c>SWP_ASYNCWINDOWPOS</c>: outside a batch it is
    /// accepted, and it is what stops the window manager blocking behind an
    /// application that has stopped answering.
    /// </remarks>
    public static bool PlaceDirectly(WindowHandle window, Rect frame) =>
        PlaceOneByOne(
            [new Placement(window, frame)],
            SET_WINDOW_POS_FLAGS.SWP_NOZORDER
            | SET_WINDOW_POS_FLAGS.SWP_NOOWNERZORDER
            | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE
            | SET_WINDOW_POS_FLAGS.SWP_ASYNCWINDOWPOS) == 1;

    /// <summary>One window, when there is only one.</summary>
    public static bool Place(WindowHandle window, Rect frame, bool activate = false) =>
        Place([new Placement(window, frame)], activate) == 1;

    /// <summary>
    /// Puts a window into the always-on-top band, or takes it out.
    /// </summary>
    /// <remarks>
    /// Position and size are left alone; this is only the z-order band, which
    /// is how a scratchpad or a pill stays over a fullscreen game.
    /// </remarks>
    public static bool SetTopmost(WindowHandle window, bool topmost) =>
        PInvoke.SetWindowPos(
            new HWND((IntPtr)window.Value),
            topmost ? HWND.HWND_TOPMOST : HWND.HWND_NOTOPMOST,
            0, 0, 0, 0,
            SET_WINDOW_POS_FLAGS.SWP_NOMOVE
            | SET_WINDOW_POS_FLAGS.SWP_NOSIZE
            | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);

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
