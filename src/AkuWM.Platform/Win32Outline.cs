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
/// Four thin layered strips per target -- top, bottom, left, right -- each a
/// popup of AkuWM's: WS_EX_TOOLWINDOW (no taskbar button), WS_EX_NOACTIVATE
/// (never takes the focus), WS_EX_TRANSPARENT plus HTTRANSPARENT (every click
/// goes through), WS_EX_LAYERED with per-pixel alpha so the corners are
/// anti-aliased (a window region is not: "se ve pixelado", 2026-09-22). The
/// strips only cover the band, so a 2529x1410 window costs ~190 KB of pixels
/// to redraw, not 14 MB; a move that keeps the size just moves them.
/// </para>
/// <para>
/// Everything happens on the thread that pumps the message window: windows
/// belong to the thread that creates them, and the wm thread is a plain work
/// queue.
/// </para>
/// </remarks>
public static class Win32Outline
{
    private const string ClassName = "AkuWM.Outline";
    private const uint WmNcHitTest = 0x0084;
    private const int HtTransparent = -1;

    private sealed class Overlay
    {
        public readonly HWND[] Strips = new HWND[4];
        public Rect Frame;
        public uint Colour;
        public int Corner;
        public int Width;
        public uint Dpi;
        public bool Topmost;
    }

    private static readonly ConcurrentDictionary<long, Overlay> Overlays = new();
    private static readonly WNDPROC Procedure = OnMessage;
    private static ushort _class;

    /// <summary>Draws, moves or removes the outline of one target. Safe from any thread.</summary>
    public static void Set(WindowHandle target, Rect? frame, uint colour, bool topmost, int corner, int width)
    {
        Win32MessageWindow? host = Win32MessageWindow.Current;
        if (host is null)
        {
            Log.Warn($"no outline for {target}: there is no message window to draw it from");
            return;
        }

        long key = target.Value;
        Log.Debug(() => $"  outline {target} -> {(frame is null ? "off" : frame.ToString())} colour {colour:x6} corner {corner} width {width} topmost={topmost}");
        if (frame is not { } wanted)
        {
            host.Post(() => Remove(key));
            return;
        }

        if (!host.Post(() => Draw(key, wanted, colour, topmost, corner, width)))
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

    private static unsafe void Draw(long key, Rect frame, uint colour, bool topmost, int corner, int width)
    {
        var target = new HWND((IntPtr)key);
        if (!PInvoke.IsWindow(target) || !Register())
        {
            return;
        }

        uint dpi = PInvoke.GetDpiForWindow(target);
        if (dpi == 0)
        {
            dpi = 96;
        }

        int t = Math.Max(1, (int)Math.Round(width * dpi / 96.0));
        int r = (int)Math.Round(corner * dpi / 96.0);
        Rect outer = frame.Inflate(t);
        // The top and bottom strips take the whole corner arc with them.
        int band = Math.Max(t, r + t);
        Rect[] strips = Strips(outer, t, band);

        bool created = false;
        if (!Overlays.TryGetValue(key, out Overlay? overlay))
        {
            overlay = new Overlay();
            for (int i = 0; i < 4; i++)
            {
                overlay.Strips[i] = PInvoke.CreateWindowEx(
                    WINDOW_EX_STYLE.WS_EX_TOOLWINDOW | WINDOW_EX_STYLE.WS_EX_NOACTIVATE
                    | WINDOW_EX_STYLE.WS_EX_TRANSPARENT | WINDOW_EX_STYLE.WS_EX_LAYERED,
                    ClassName,
                    string.Empty,
                    WINDOW_STYLE.WS_POPUP,
                    strips[i].X, strips[i].Y, Math.Max(1, strips[i].Width), Math.Max(1, strips[i].Height),
                    HWND.Null, null, null, null);

                if (overlay.Strips[i].IsNull)
                {
                    Log.Warn($"no outline window for {target}: CreateWindowEx failed");
                    for (int j = 0; j < i; j++)
                    {
                        PInvoke.DestroyWindow(overlay.Strips[j]);
                    }

                    return;
                }
            }

            Overlays[key] = overlay;
            created = true;
            Log.Debug($"  outline windows created for {target}");
        }

        bool sameShape = !created
            && overlay.Frame.Width == frame.Width && overlay.Frame.Height == frame.Height
            && overlay.Colour == colour && overlay.Corner == corner && overlay.Width == width && overlay.Dpi == dpi;

        if (!sameShape)
        {
            for (int i = 0; i < 4; i++)
            {
                Render(overlay.Strips[i], strips[i], outer, t, r, colour);
            }
        }
        else if (overlay.Frame != frame)
        {
            for (int i = 0; i < 4; i++)
            {
                PInvoke.SetWindowPos(
                    overlay.Strips[i], HWND.Null, strips[i].X, strips[i].Y, 0, 0,
                    SET_WINDOW_POS_FLAGS.SWP_NOSIZE | SET_WINDOW_POS_FLAGS.SWP_NOZORDER | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);
            }
        }

        overlay.Frame = frame;
        overlay.Colour = colour;
        overlay.Corner = corner;
        overlay.Width = width;
        overlay.Dpi = dpi;
        overlay.Topmost = topmost;

        // Directly above the target: after the window that is above it.
        // Relative to an elevated window is refused (UIPI: the window above
        // Purple was Purple's own popup), then the top of the band, which
        // is over anything that overlaps the target -- the price of a border
        // on a window AkuWM may not touch.
        HWND above = PInvoke.GetWindow(target, GET_WINDOW_CMD.GW_HWNDPREV);
        bool ours = false;
        for (int i = 0; i < 4 && !ours; i++)
        {
            ours = above == overlay.Strips[i];
        }

        HWND insertAfter = !above.IsNull && !ours ? above : topmost ? HWND.HWND_TOPMOST : HWND.HWND_TOP;
        const SET_WINDOW_POS_FLAGS zOnly =
            SET_WINDOW_POS_FLAGS.SWP_NOMOVE | SET_WINDOW_POS_FLAGS.SWP_NOSIZE
            | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_SHOWWINDOW | SET_WINDOW_POS_FLAGS.SWP_NOOWNERZORDER;

        for (int i = 0; i < 4; i++)
        {
            if (!PInvoke.SetWindowPos(overlay.Strips[i], insertAfter, 0, 0, 0, 0, zOnly))
            {
                PInvoke.SetWindowPos(overlay.Strips[i], topmost ? HWND.HWND_TOPMOST : HWND.HWND_TOP, 0, 0, 0, 0, zOnly);
            }
        }
    }

    /// <summary>Top, bottom, left, right, in outer-screen coordinates.</summary>
    private static Rect[] Strips(Rect outer, int t, int band)
    {
        int middle = Math.Max(0, outer.Height - (2 * band));
        return
        [
            new Rect(outer.X, outer.Y, outer.Width, band),
            new Rect(outer.X, outer.Bottom - band, outer.Width, band),
            new Rect(outer.X, outer.Y + band, t, middle),
            new Rect(outer.Right - t, outer.Y + band, t, middle),
        ];
    }

    /// <summary>
    /// Paints one strip: the band between the outer rounded rectangle and the
    /// frame's own, anti-aliased by signed distance, premultiplied BGRA.
    /// </summary>
    private static unsafe void Render(HWND strip, Rect where, Rect outer, int t, int r, uint colour)
    {
        int w = Math.Max(1, where.Width);
        int h = Math.Max(1, where.Height);

        var info = new BITMAPINFO();
        info.bmiHeader.biSize = (uint)sizeof(BITMAPINFOHEADER);
        info.bmiHeader.biWidth = w;
        info.bmiHeader.biHeight = -h; // top-down
        info.bmiHeader.biPlanes = 1;
        info.bmiHeader.biBitCount = 32;
        info.bmiHeader.biCompression = (uint)BI_COMPRESSION.BI_RGB;

        HDC screen = PInvoke.GetDC(HWND.Null);
        HDC memory = PInvoke.CreateCompatibleDC(screen);
        void* bits;
        using DeleteObjectSafeHandle bitmap = PInvoke.CreateDIBSection(memory, &info, DIB_USAGE.DIB_RGB_COLORS, out bits, null, 0);
        if (bitmap.IsInvalid)
        {
            PInvoke.DeleteDC(memory);
            PInvoke.ReleaseDC(HWND.Null, screen);
            return;
        }

        HGDIOBJ previous = PInvoke.SelectObject(memory, new HGDIOBJ(bitmap.DangerousGetHandle()));

        byte red = (byte)(colour & 0xFF);
        byte green = (byte)((colour >> 8) & 0xFF);
        byte blue = (byte)((colour >> 16) & 0xFF);
        float outerR = r > 0 ? r + t : 0;
        float innerR = r;
        // Half sizes and centres of the two rounded rectangles, in outer coordinates.
        float ocx = outer.Width / 2f, ocy = outer.Height / 2f;
        float ohx = ocx - outerR, ohy = ocy - outerR;
        float ihx = (outer.Width - (2 * t)) / 2f - innerR, ihy = (outer.Height - (2 * t)) / 2f - innerR;

        uint* pixel = (uint*)bits;
        int offsetX = where.X - outer.X;
        int offsetY = where.Y - outer.Y;
        for (int y = 0; y < h; y++)
        {
            float py = offsetY + y + 0.5f - ocy;
            for (int x = 0; x < w; x++)
            {
                float px = offsetX + x + 0.5f - ocx;
                float a = Coverage(px, py, ohx, ohy, outerR) - Coverage(px, py, ihx, ihy, innerR);
                if (a <= 0)
                {
                    pixel[y * w + x] = 0;
                    continue;
                }

                if (a > 1)
                {
                    a = 1;
                }

                uint alpha = (uint)(a * 255 + 0.5f);
                uint b = (uint)(blue * a + 0.5f), g = (uint)(green * a + 0.5f), rr = (uint)(red * a + 0.5f);
                pixel[y * w + x] = (alpha << 24) | (rr << 16) | (g << 8) | b;
            }
        }

        var destination = new System.Drawing.Point(where.X, where.Y);
        var size = new SIZE { cx = w, cy = h };
        var source = new System.Drawing.Point(0, 0);
        var blend = new BLENDFUNCTION { BlendOp = 0, BlendFlags = 0, SourceConstantAlpha = 255, AlphaFormat = 1 }; // AC_SRC_OVER, AC_SRC_ALPHA
        PInvoke.UpdateLayeredWindow(strip, default, destination, size, memory, source, new COLORREF(0), blend, UPDATE_LAYERED_WINDOW_FLAGS.ULW_ALPHA);

        PInvoke.SelectObject(memory, previous);
        PInvoke.DeleteDC(memory);
        PInvoke.ReleaseDC(HWND.Null, screen);
    }

    /// <summary>How much of a pixel at (px, py) from the centre lies inside a rounded rectangle of half size (hx, hy) plus radius.</summary>
    private static float Coverage(float px, float py, float hx, float hy, float radius)
    {
        float qx = Math.Abs(px) - hx;
        float qy = Math.Abs(py) - hy;
        float ox = Math.Max(qx, 0), oy = Math.Max(qy, 0);
        float distance = MathF.Sqrt((ox * ox) + (oy * oy)) + Math.Min(Math.Max(qx, qy), 0) - radius;
        float coverage = 0.5f - distance;
        return coverage <= 0 ? 0 : coverage >= 1 ? 1 : coverage;
    }

    private static void Remove(long key)
    {
        if (!Overlays.TryRemove(key, out Overlay? overlay))
        {
            return;
        }

        Log.Debug(() => $"  outline windows destroyed for 0x{key:x}");
        for (int i = 0; i < 4; i++)
        {
            if (!overlay.Strips[i].IsNull)
            {
                PInvoke.DestroyWindow(overlay.Strips[i]);
            }
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

    private static LRESULT OnMessage(HWND window, uint message, WPARAM wParam, LPARAM lParam) =>
        message == WmNcHitTest ? (LRESULT)HtTransparent : PInvoke.DefWindowProc(window, message, wParam, lParam);
}
