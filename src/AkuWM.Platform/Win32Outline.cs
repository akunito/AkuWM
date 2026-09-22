using System.Collections.Concurrent;
using AkuWM.Core.Logging;
using AkuWM.Core.Model;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.WindowsAndMessaging;

namespace AkuWM.Platform;

/// <summary>
/// AkuWM's own border around a window it may not decorate (see <see cref="Outline"/>).
/// </summary>
/// <remarks>
/// <para>
/// One popup window per target: WS_EX_TOOLWINDOW (no taskbar button),
/// WS_EX_NOACTIVATE (never takes the focus), WS_EX_TRANSPARENT plus
/// HTTRANSPARENT (every click goes through), shaped by a region that is the
/// frame band only -- so there is no bitmap to draw and no interior at all.
/// The background brush paints it; nothing else is drawn. It sits directly
/// above its target in the z-order and follows its rectangle.
/// </para>
/// <para>
/// Everything happens on the thread that pumps the message window: windows
/// belong to the thread that creates them, and the wm thread is a plain work
/// queue that would never deliver a WM_ERASEBKGND.
/// </para>
/// </remarks>
public static class Win32Outline
{
    private const string ClassName = "AkuWM.Outline";
    private const uint WmNcHitTest = 0x0084;
    private const uint WmEraseBkgnd = 0x0014;
    private const uint WmPaint = 0x000F;
    private const int HtTransparent = -1;

    private sealed class Overlay
    {
        public HWND Window;
        public Rect Outer;
        public uint Colour;
        public HBRUSH Brush;
    }

    private static readonly ConcurrentDictionary<long, Overlay> Overlays = new();
    private static readonly WNDPROC Procedure = OnMessage;
    private static ushort _class;

    /// <summary>Draws, moves or removes the outline of one target. Safe from any thread.</summary>
    public static void Set(WindowHandle target, Rect? frame, uint colour, bool topmost)
    {
        Win32MessageWindow? host = Win32MessageWindow.Current;
        if (host is null)
        {
            Log.Warn($"no outline for {target}: there is no message window to draw it from");
            return;
        }

        long key = target.Value;
        Log.Debug(() => $"  outline {target} -> {(frame is null ? "off" : frame.ToString())} colour {colour:x6} topmost={topmost}");
        if (frame is not { } wanted)
        {
            host.Post(() => Remove(key));
            return;
        }

        if (!host.Post(() => Draw(key, wanted, colour, topmost)))
        {
            Log.Warn($"no outline for {target}: the message thread did not take the work");
        }
    }

    private static unsafe bool Register()
    {
        if (_class != 0)
        {
            return true;
        }

        fixed (char* name = ClassName)
        {
            var description = new WNDCLASSEXW
            {
                cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<WNDCLASSEXW>(),
                lpfnWndProc = Procedure,
                hInstance = (HINSTANCE)PInvoke.GetModuleHandle((string?)null).DangerousGetHandle(),
                lpszClassName = name,
            };

            _class = PInvoke.RegisterClassEx(description);
        }

        if (_class == 0)
        {
            Log.Warn("the outline window class could not be registered; elevated windows get no border");
        }

        return _class != 0;
    }

    private static unsafe void Draw(long key, Rect frame, uint colour, bool topmost)
    {
        var target = new HWND((IntPtr)key);
        if (!PInvoke.IsWindow(target) || !Register())
        {
            return;
        }

        // 2 px at 100 %, scaled with the target's screen.
        uint dpi = PInvoke.GetDpiForWindow(target);
        int thickness = Math.Max(2, (int)Math.Round(2 * (dpi == 0 ? 96 : dpi) / 96.0));
        Rect outer = frame.Inflate(thickness);

        if (!Overlays.TryGetValue(key, out Overlay? overlay))
        {
            HWND created = PInvoke.CreateWindowEx(
                WINDOW_EX_STYLE.WS_EX_TOOLWINDOW | WINDOW_EX_STYLE.WS_EX_NOACTIVATE | WINDOW_EX_STYLE.WS_EX_TRANSPARENT,
                ClassName,
                string.Empty,
                WINDOW_STYLE.WS_POPUP,
                outer.X, outer.Y, outer.Width, outer.Height,
                HWND.Null, null, null, null);

            if (created.IsNull)
            {
                Log.Warn($"no outline window for {target}: CreateWindowEx failed");
                return;
            }

            overlay = new Overlay { Window = created, Outer = default, Colour = 0 };
            Overlays[key] = overlay;
            Log.Debug($"  outline window {created} created for {target}");
        }

        if (overlay.Colour != colour || overlay.Brush.IsNull)
        {
            if (!overlay.Brush.IsNull)
            {
                PInvoke.DeleteObject(overlay.Brush);
            }

            overlay.Brush = PInvoke.CreateSolidBrush(new COLORREF(colour));
            overlay.Colour = colour;
            PInvoke.InvalidateRect(overlay.Window, (RECT*)null, true);
        }

        if (overlay.Outer != outer)
        {
            // The band as a region: the outer rectangle minus the frame.
            HRGN band = PInvoke.CreateRectRgn(0, 0, outer.Width, outer.Height);
            HRGN hole = PInvoke.CreateRectRgn(thickness, thickness, outer.Width - thickness, outer.Height - thickness);
            PInvoke.CombineRgn(band, band, hole, RGN_COMBINE_MODE.RGN_DIFF);
            PInvoke.DeleteObject(hole);
            PInvoke.SetWindowRgn(overlay.Window, band, true); // the system owns the region from here
            overlay.Outer = outer;
        }

        // Directly above the target: after the window that is above it. When
        // the target is the very top of its band, HWND_TOP keeps the band and
        // HWND_TOPMOST joins the other one.
        HWND above = PInvoke.GetWindow(target, GET_WINDOW_CMD.GW_HWNDPREV);
        HWND insertAfter = !above.IsNull && above != overlay.Window ? above : topmost ? HWND.HWND_TOPMOST : HWND.HWND_TOP;

        const SET_WINDOW_POS_FLAGS flags =
            SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_SHOWWINDOW | SET_WINDOW_POS_FLAGS.SWP_NOOWNERZORDER;
        bool placed = PInvoke.SetWindowPos(overlay.Window, insertAfter, outer.X, outer.Y, outer.Width, outer.Height, flags);

        // Relative to an elevated window is refused (UIPI: the window above
        // Purple was Purple's own popup, and SetWindowPos said no). The top
        // of the band then: over anything that overlaps the target, which is
        // the price of a border on a window AkuWM may not touch.
        if (!placed)
        {
            placed = PInvoke.SetWindowPos(overlay.Window, topmost ? HWND.HWND_TOPMOST : HWND.HWND_TOP, outer.X, outer.Y, outer.Width, outer.Height, flags);
        }
        Log.Debug(() => $"  outline window {overlay.Window} at {outer} after {insertAfter.Value:x} -> {placed}, alive={PInvoke.IsWindow(overlay.Window)}");
    }

    private static void Remove(long key)
    {
        if (!Overlays.TryRemove(key, out Overlay? overlay))
        {
            return;
        }

        Log.Debug(() => $"  outline window {overlay.Window} destroyed");
        PInvoke.DestroyWindow(overlay.Window);
        if (!overlay.Brush.IsNull)
        {
            PInvoke.DeleteObject(overlay.Brush);
        }
    }

    /// <summary>Takes every outline down; on the message thread, before its window goes.</summary>
    public static void DisposeAll()
    {
        foreach (long key in Overlays.Keys.ToArray())
        {
            Remove(key);
        }
    }

    private static unsafe LRESULT OnMessage(HWND window, uint message, WPARAM wParam, LPARAM lParam)
    {
        switch (message)
        {
            case WmNcHitTest:
                return (LRESULT)HtTransparent;

            case WmEraseBkgnd:
                foreach (Overlay overlay in Overlays.Values)
                {
                    if (overlay.Window == window && !overlay.Brush.IsNull)
                    {
                        RECT client;
                        PInvoke.GetClientRect(window, &client);
                        PInvoke.FillRect(new HDC((IntPtr)(nint)wParam.Value), &client, overlay.Brush);
                        return (LRESULT)1;
                    }
                }

                break;

            case WmPaint:
                PAINTSTRUCT paint;
                PInvoke.BeginPaint(window, &paint);
                PInvoke.EndPaint(window, &paint);
                return (LRESULT)0;
        }

        return PInvoke.DefWindowProc(window, message, wParam, lParam);
    }
}
