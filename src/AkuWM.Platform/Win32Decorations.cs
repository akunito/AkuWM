using AkuWM.Core.Logging;
using AkuWM.Core.Model;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.UI.WindowsAndMessaging;

namespace AkuWM.Platform;

/// <summary>
/// The border colour and the corner shape, which the compositor draws.
/// </summary>
/// <remarks>
/// <para>
/// Both are Windows 11 (build 22000) attributes: on anything older
/// <c>DwmSetWindowAttribute</c> returns <c>E_INVALIDARG</c> and nothing is
/// drawn. That is not an error, and saying so once is enough -- a window
/// manager that logs a failure per window per focus change is one nobody can
/// read the log of.
/// </para>
/// <para>
/// A window with no DWM frame of its own -- an application that draws its own
/// chrome -- takes the call and shows nothing. There is no way to tell the two
/// apart from here.
/// </para>
/// </remarks>
internal static class Win32Decorations
{
    // Not in the DWMWINDOWATTRIBUTE the metadata generates for this Windows
    // version, so the numbers are here, from the MS documentation.
    private const DWMWINDOWATTRIBUTE CornerPreference = (DWMWINDOWATTRIBUTE)33;
    private const DWMWINDOWATTRIBUTE BorderColor = (DWMWINDOWATTRIBUTE)34;

    private static bool _unsupported;

    /// <summary>
    /// The window styles AkuWM found, by window, so they can be put back.
    /// </summary>
    /// <remarks>
    /// The one thing here that is not cosmetic. A window whose caption has
    /// been stripped cannot be moved or closed with the mouse, so leaving one
    /// that way after AkuWM stops is not a wrong-looking window, it is a
    /// stuck one. Held in memory only: this survives AkuWM changing its mind,
    /// not AkuWM dying -- and a style, unlike a cloak, is something the
    /// application itself can undo by asking for its own frame again.
    /// </remarks>
    private static readonly Dictionary<long, int> Captions = [];

    /// <summary>What AkuWM last sent each window, so only the fields that differ go again.</summary>
    /// <remarks>
    /// Every focus change decorates two windows, and each one re-sent the
    /// corner preference it already had: four DWM calls where two do. Held in
    /// memory only, like the captions; a fresh daemon sends everything once.
    /// </remarks>
    private static readonly Dictionary<long, Decoration> Sent = [];

    internal static bool Apply(HWND hwnd, Decoration decoration, bool force = false)
    {
        // Forced: the last one sent is exactly what the application painted
        // over, so "already sent" is the wrong answer. The re-assert after a
        // focus change was being filtered out here for a whole afternoon.
        if (force)
        {
            Sent.Remove((long)hwnd.Value);
        }

        Sent.TryGetValue((long)hwnd.Value, out Decoration previous);
        bool known = Sent.ContainsKey((long)hwnd.Value);

        bool did = (!known || previous.TitleBar != decoration.TitleBar) && Caption(hwnd, decoration.TitleBar);
        did |= (!known || previous.Opacity != decoration.Opacity) && Opacity(hwnd, decoration.Opacity);

        if (_unsupported)
        {
            return did;
        }

        unsafe
        {
            HRESULT colour = default;
            HRESULT shape = default;
            bool sentColour = false;
            bool sentShape = false;

            if (!known || previous.Border != decoration.Border)
            {
                uint border = decoration.Border;
                colour = PInvoke.DwmSetWindowAttribute(hwnd, BorderColor, &border, sizeof(uint));
                sentColour = true;
            }

            if (!known || previous.Corners != decoration.Corners)
            {
                uint corners = decoration.Corners switch
                {
                    Corners.Square => 1u,      // DWMWCP_DONOTROUND
                    Corners.Round => 2u,       // DWMWCP_ROUND
                    Corners.RoundSmall => 3u,  // DWMWCP_ROUNDSMALL
                    _ => 0u,                   // DWMWCP_DEFAULT
                };

                shape = PInvoke.DwmSetWindowAttribute(hwnd, CornerPreference, &corners, sizeof(uint));
                sentShape = true;
            }

            if (!sentColour && !sentShape)
            {
                return true; // nothing differed
            }

            if ((sentColour && colour.Succeeded) || (sentShape && shape.Succeeded))
            {
                Sent[(long)hwnd.Value] = decoration;
                return true;
            }

            // Which call, and why: UIPI refuses an elevated window from a
            // build without uiAccess, and there was nothing in the log.
            Log.Debug(() => $"decorating {hwnd.Value:x} refused: border 0x{colour.Value:x8}, corners 0x{shape.Value:x8}");

            if (did)
            {
                return true;
            }

            // E_INVALIDARG from BOTH is the old-Windows answer. A single
            // window refusing is ordinary; everything refusing is the build.
            if (sentColour && sentShape
                && colour.Value == unchecked((int)0x80070057) && shape.Value == unchecked((int)0x80070057))
            {
                _unsupported = true;
                Log.Info("this Windows build has no window border or corner attributes; AkuWM will not ask again");
            }

            return false;
        }
    }

    /// <summary>
    /// Strips or restores the caption, which is where the buttons live.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>WS_THICKFRAME</c> is deliberately left alone: it is the resize
    /// border, a floating window needs it, and <c>IsResizable</c> is read from
    /// it elsewhere.
    /// </para>
    /// <para>
    /// The non-client area has to be recalculated afterwards or the window
    /// keeps drawing a bar that is no longer in its style, so the
    /// <c>SWP_FRAMECHANGED</c> is not optional. It also means the frame
    /// bounds AkuWM read a moment ago are stale, and the next redraw will
    /// place it again -- which is the right answer and costs one extra move.
    /// </para>
    /// </remarks>
    private static bool Caption(HWND hwnd, bool wanted)
    {
        const int GwlStyle = -16;
        const int WsCaption = 0x00C00000;

        long key = hwnd.Value;
        int style = (int)PInvoke.GetWindowLongPtr(hwnd, (WINDOW_LONG_PTR_INDEX)GwlStyle);

        if (style == 0)
        {
            return false;
        }

        bool has = (style & WsCaption) == WsCaption;

        if (wanted)
        {
            // Only put back what was taken: a window that never had a caption
            // must not be given one.
            if (!Captions.Remove(key, out int original) || has)
            {
                return false;
            }

            // Only the caption bits from the saved style, on the style the
            // window has NOW. Writing the saved dword back carried its
            // WS_MINIMIZE / WS_MAXIMIZE bits from a moment ago: a maximised
            // fliptest minimised and restored came back iconic at
            // -10,-10 3860x2180 -- IsIconic true, IsZoomed false, for good --
            // because the restore's decoration wrote the style saved while
            // it was minimised (measured 2026-09-22 23:25; tests/fullscreen
            // 8-startmax steps 2-5 "state=minimized" since 22:19).
            style |= original & WsCaption;
        }
        else
        {
            if (!has)
            {
                return false;
            }

            // A window that draws its own title bar has nothing to strip: its
            // client area already fills the frame (WinUI 3 Notepad, Windows
            // Terminal, a browser with its tabs in the title bar -- measured
            // 2026-09-30: non-client height 8-9 px with WS_CAPTION set, against
            // ~80 for Notepad++). Taking WS_CAPTION from a WinUI window left
            // it painting the classic caption -- title, minimise, maximise,
            // close -- over its own tab strip after the next re-layout
            // (Diego, Notepad, 12:35). The style is what its title bar code
            // is built on; the bar it draws is the one `hide` would remove.
            if (DrawsItsOwnCaption(hwnd))
            {
                return false;
            }

            // A window with a classic menu bar keeps its caption. Notepad++
            // in dark mode paints the menu bar dark from its non-client
            // handlers; without WS_CAPTION the bar came out white
            // (240,240,240), and DrawMenuBar, RedrawWindow(RDW_FRAME) and
            // WM_NCACTIVATE did not bring it back -- only the style did
            // (measured 2026-09-30 13:08). A white menu bar is worse than a
            // title bar.
            if (!PInvoke.GetMenu(hwnd).IsNull)
            {
                return false;
            }

            Captions[key] = style;
            style &= ~WsCaption;
        }

        PInvoke.SetWindowLongPtr(hwnd, (WINDOW_LONG_PTR_INDEX)GwlStyle, style);

        // Read back: UIPI refuses the style change on an elevated window and
        // SetWindowLongPtr does not say so. Counted as done, an elevated
        // console had "its caption stripped", the decoration passed as
        // applied, and the outline that stands in for the refused border
        // was never drawn (Administrator: Windows PowerShell, 2026-09-22).
        if ((int)PInvoke.GetWindowLongPtr(hwnd, (WINDOW_LONG_PTR_INDEX)GwlStyle) != style)
        {
            if (!wanted)
            {
                Captions.Remove(key);
            }

            return false;
        }

        PInvoke.SetWindowPos(
            hwnd, HWND.Null, 0, 0, 0, 0,
            SET_WINDOW_POS_FLAGS.SWP_NOMOVE | SET_WINDOW_POS_FLAGS.SWP_NOSIZE
            | SET_WINDOW_POS_FLAGS.SWP_NOZORDER | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE
            | SET_WINDOW_POS_FLAGS.SWP_FRAMECHANGED);

        return true;
    }

    /// <summary>
    /// How solid the window is.
    /// </summary>
    /// <remarks>
    /// <c>WS_EX_LAYERED</c> has to go on before the alpha means anything, and
    /// it is left on once set: taking it off again repaints the window, and a
    /// window that flickers every time the focus moves is worse than one
    /// carrying a style bit it is not using.
    /// </remarks>
    /// <summary>The windows AkuWM itself made layered: the only ones whose alpha it may set (see <see cref="Layering"/>).</summary>
    private static readonly HashSet<long> LayeredByUs = [];

    private static bool Opacity(HWND hwnd, double opacity)
    {
        const int GwlExStyle = -20;
        const int WsExLayered = 0x00080000;

        int ex = (int)PInvoke.GetWindowLongPtr(hwnd, (WINDOW_LONG_PTR_INDEX)GwlExStyle);

        if (ex == 0)
        {
            return false;
        }

        long key = hwnd.Value;
        bool layered = (ex & WsExLayered) != 0;

        // A window layered by its own doing is painted with
        // UpdateLayeredWindow; one SetLayeredWindowAttributes on it and it
        // never repaints again (NordVPN's dialog, 2026-09-22). Never touched.
        if (!Layering.ShouldSetAlpha(layered, LayeredByUs.Contains(key), opacity))
        {
            return false;
        }

        if (!layered)
        {
            PInvoke.SetWindowLongPtr(hwnd, (WINDOW_LONG_PTR_INDEX)GwlExStyle, ex | WsExLayered);
            LayeredByUs.Add(key);
        }

        byte alpha = (byte)Math.Clamp((int)Math.Round(opacity * 255), 1, 255);

        return PInvoke.SetLayeredWindowAttributes(
            hwnd, default, alpha, LAYERED_WINDOW_ATTRIBUTES_FLAGS.LWA_ALPHA);
    }

    /// <summary>How much frame a window may have above its client area and still count as drawing its own caption.</summary>
    private const int OwnCaptionNonClientPx = 16;

    private static bool DrawsItsOwnCaption(HWND hwnd)
    {
        if (!PInvoke.GetWindowRect(hwnd, out RECT window) || !PInvoke.GetClientRect(hwnd, out RECT client))
        {
            return false;
        }

        int frame = window.bottom - window.top;
        int inside = client.bottom - client.top;
        return frame > 0 && inside > 0 && frame - inside <= OwnCaptionNonClientPx;
    }
}
