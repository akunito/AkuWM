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

        SET_WINDOW_POS_FLAGS flags = Flags(activate);

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
            Rect target = WithBorder(hwnd, placement);

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
    /// <summary>
    /// The same placements, one call each, asynchronously.
    /// </summary>
    /// <remarks>
    /// Public so the bench can put it against the batch on the real desk. The
    /// batch's whole claim is that the windows land together; the measured
    /// cost of it is that EndDeferWindowPos waits for every application in
    /// turn, and on this desk that is where the slow redraws are -- 6 or 7
    /// windows placed, nothing else to do, 700 ms (p99 of 1683 redraws:
    /// 626 ms, median 1.13 ms). Posting N requests takes microseconds and
    /// lets the applications answer at the same time as each other.
    /// </remarks>
    public static int PlaceEachAsync(IReadOnlyList<Placement> placements, bool activate = false) =>
        PlaceOneByOne(placements, Flags(activate));

    private static SET_WINDOW_POS_FLAGS Flags(bool activate)
    {
        SET_WINDOW_POS_FLAGS flags =
            SET_WINDOW_POS_FLAGS.SWP_NOZORDER
            | SET_WINDOW_POS_FLAGS.SWP_NOOWNERZORDER;

        return activate ? flags : flags | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE;
    }

    private static int PlaceOneByOne(IReadOnlyList<Placement> placements, SET_WINDOW_POS_FLAGS flags)
    {
        int placed = 0;

        foreach (Placement placement in placements)
        {
            var hwnd = new HWND((IntPtr)placement.Window.Value);
            Rect target = WithBorder(hwnd, placement);

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
    public static bool SetTopmost(WindowHandle window, bool topmost)
    {
        var hwnd = new HWND((IntPtr)window.Value);
        PInvoke.SetWindowPos(
            hwnd,
            topmost ? HWND.HWND_TOPMOST : HWND.HWND_NOTOPMOST,
            0, 0, 0, 0,
            SET_WINDOW_POS_FLAGS.SWP_NOMOVE
            | SET_WINDOW_POS_FLAGS.SWP_NOSIZE
            | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);

        // Read back, not trusted: the return value says the request went
        // through, and an application handling WM_WINDOWPOSCHANGING can put
        // the bit back the way it likes it (Windows Terminal does).
        return IsTopmost(window) == topmost;
    }

    public static bool IsTopmost(WindowHandle window) =>
        ((int)PInvoke.GetWindowLongPtr(new HWND((IntPtr)window.Value), WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE)
         & (int)WINDOW_EX_STYLE.WS_EX_TOPMOST) != 0;

    public static bool Raise(WindowHandle window) =>
        PInvoke.SetWindowPos(
            new HWND((IntPtr)window.Value),
            HWND.HWND_TOP,
            0, 0, 0, 0,
            SET_WINDOW_POS_FLAGS.SWP_NOMOVE
            | SET_WINDOW_POS_FLAGS.SWP_NOSIZE
            | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);

    public static bool Lower(WindowHandle window) =>
        PInvoke.SetWindowPos(
            new HWND((IntPtr)window.Value),
            HWND.HWND_BOTTOM,
            0, 0, 0, 0,
            SET_WINDOW_POS_FLAGS.SWP_NOMOVE
            | SET_WINDOW_POS_FLAGS.SWP_NOSIZE
            | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);

    /// <summary>
    /// Puts a window directly behind another. The call lands on the window
    /// being moved, never on the one it goes behind, so a game's swapchain is
    /// not touched by it.
    /// </summary>
    public static bool PlaceBehind(WindowHandle window, WindowHandle behind) =>
        PInvoke.SetWindowPos(
            new HWND((IntPtr)window.Value),
            new HWND((IntPtr)behind.Value),
            0, 0, 0, 0,
            SET_WINDOW_POS_FLAGS.SWP_NOMOVE
            | SET_WINDOW_POS_FLAGS.SWP_NOSIZE
            | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE
            | SET_WINDOW_POS_FLAGS.SWP_NOOWNERZORDER);

    /// <summary>
    /// Turns a visible-frame rectangle into the outer rectangle
    /// <c>SetWindowPos</c> expects.
    /// </summary>
    private static Rect WithBorder(HWND hwnd, Placement placement)
    {
        // What the model already knows. It read both rectangles when the
        // window last changed; asking Windows again costs a GetWindowRect and
        // a DWM round trip, per window, inside the batch.
        if (placement.Border is { } known)
        {
            return Grown(placement.Frame, known.Top, known.Right, known.Bottom, known.Left);
        }

        if (!PInvoke.GetWindowRect(hwnd, out RECT outer))
        {
            return placement.Frame;
        }

        Rect? visible = Win32Windows.FrameBoundsOf(hwnd);
        if (visible is not { } bounds || bounds.IsEmpty)
        {
            return placement.Frame;
        }

        return Grown(
            placement.Frame,
            bounds.Top - outer.top,
            outer.right - bounds.Right,
            outer.bottom - bounds.Bottom,
            bounds.Left - outer.left);
    }

    private static Rect Grown(Rect frame, int top, int right, int bottom, int left) => new(
        frame.X - left,
        frame.Y - top,
        frame.Width + left + right,
        frame.Height + top + bottom);
}
