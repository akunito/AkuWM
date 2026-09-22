using System.Runtime.InteropServices;
using System.Text;
using AkuWM.Core.Model;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.Security;
using Windows.Win32.System.Threading;
using Windows.Win32.UI.WindowsAndMessaging;

namespace AkuWM.Platform;

/// <summary>
/// Reads windows: which ones exist, and what each one is.
/// </summary>
/// <remarks>
/// <para>
/// The filter is Microsoft's own description of what belongs in the Alt+Tab
/// list -- visible, its own root owner, not a tool window unless it asks to
/// be listed, not cloaked by the shell for another virtual desktop -- because
/// that is the set a person thinks of as "my windows", which is the set a
/// window manager should arrange. AkuWM's own rules then take things out of
/// it (<c>ignore</c>) or change what happens to them.
/// </para>
/// <para>
/// Two measured facts shape this file. <c>GetWindowRect</c> includes the
/// invisible resize border, so the visible rectangle comes from
/// <c>DWMWA_EXTENDED_FRAME_BOUNDS</c> and the difference is kept as the border
/// delta. And a window cloaked by the shell is still "visible" to
/// <c>IsWindowVisible</c>, so the cloak flag is read for every window rather
/// than inferred.
/// </para>
/// </remarks>
public static class Win32Windows
{
    /// <summary>Every window a manager has business with, front of the z-order first.</summary>
    public static List<WindowSnapshot> Enumerate()
    {
        var handles = new List<HWND>(128);

        // EnumWindows walks top-level windows in z-order, front to back, which
        // is the order the restacking logic needs and the order the IPC
        // reports.
        PInvoke.EnumWindows(
            (hwnd, _) =>
            {
                handles.Add(hwnd);
                return true;
            },
            IntPtr.Zero);

        var windows = new List<WindowSnapshot>(handles.Count);
        foreach (HWND hwnd in handles)
        {
            if (!IsCandidate(hwnd))
            {
                continue;
            }

            WindowSnapshot? snapshot = Read(hwnd);
            if (snapshot is not null)
            {
                windows.Add(snapshot);
            }
        }

        return windows;
    }

    public static WindowSnapshot? Read(WindowHandle handle) => Read(new HWND((IntPtr)handle.Value));

    /// <summary>Could this handle be a window AkuWM would manage? Three cheap calls.</summary>
    public static bool CouldBeManaged(WindowHandle handle) =>
        IsCandidate(new HWND((IntPtr)handle.Value));

    /// <summary>One call: does that handle still name a window?</summary>
    public static bool IsWindow(WindowHandle handle) =>
        PInvoke.IsWindow(new HWND((IntPtr)handle.Value));

    /// <summary>
    /// Could this window ever be managed? Cheap checks only: the expensive
    /// ones (process name, DWM attributes) are paid once the answer is yes.
    /// </summary>
    internal static bool IsCandidate(HWND hwnd)
    {
        if (!PInvoke.IsWindow(hwnd) || !PInvoke.IsWindowVisible(hwnd))
        {
            return false;
        }

        // A window that is owned by another one (a dialog, a tooltip, a
        // palette) is the owner's business, not the manager's -- unless the
        // owner is itself, which is what a real top-level window looks like.
        if (PInvoke.GetAncestor(hwnd, GET_ANCESTOR_FLAGS.GA_ROOTOWNER) != hwnd)
        {
            return false;
        }

        var style = (WINDOW_STYLE)(uint)PInvoke.GetWindowLongPtr(hwnd, WINDOW_LONG_PTR_INDEX.GWL_STYLE);
        var exStyle = (WINDOW_EX_STYLE)(uint)PInvoke.GetWindowLongPtr(hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);

        if (style.HasFlag(WINDOW_STYLE.WS_CHILD))
        {
            return false;
        }

        // A tool window stays out of the list unless it explicitly asked to be
        // in it; that is how the shell decides too.
        if (exStyle.HasFlag(WINDOW_EX_STYLE.WS_EX_TOOLWINDOW) && !exStyle.HasFlag(WINDOW_EX_STYLE.WS_EX_APPWINDOW))
        {
            return false;
        }

        // Anything untitled is scaffolding: the invisible helper windows every
        // toolkit keeps around, and the shell's own.
        if (PInvoke.GetWindowTextLength(hwnd) == 0)
        {
            return false;
        }

        return true;
    }

    internal static WindowSnapshot? Read(HWND hwnd)
    {
        if (!PInvoke.IsWindow(hwnd))
        {
            return null;
        }

        uint processId = 0;
        unsafe
        {
            PInvoke.GetWindowThreadProcessId(hwnd, &processId);
        }

        if (!PInvoke.GetWindowRect(hwnd, out RECT rect))
        {
            return null;
        }

        var style = (WINDOW_STYLE)(uint)PInvoke.GetWindowLongPtr(hwnd, WINDOW_LONG_PTR_INDEX.GWL_STYLE);
        var exStyle = (WINDOW_EX_STYLE)(uint)PInvoke.GetWindowLongPtr(hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);

        Rect windowRect = Rect.FromEdges(rect.left, rect.top, rect.right, rect.bottom);
        (string process, bool elevated) = ProcessFacts(processId);

        return new WindowSnapshot
        {
            Handle = new WindowHandle(hwnd.Value),
            ProcessId = processId,
            ProcessName = process,
            ClassName = ClassOf(hwnd),
            Title = TitleOf(hwnd),
            WindowRect = windowRect,
            FrameBounds = FrameBoundsOf(hwnd) ?? windowRect,
            Monitor = new MonitorHandle(
                PInvoke.MonitorFromWindow(hwnd, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST).Value),
            IsVisible = PInvoke.IsWindowVisible(hwnd),
            IsMinimized = PInvoke.IsIconic(hwnd),
            IsMaximized = PInvoke.IsZoomed(hwnd),
            Cloak = CloakOf(hwnd),
            IsTopmost = exStyle.HasFlag(WINDOW_EX_STYLE.WS_EX_TOPMOST),
            IsResizable = style.HasFlag(WINDOW_STYLE.WS_THICKFRAME),
            IsToolWindow = exStyle.HasFlag(WINDOW_EX_STYLE.WS_EX_TOOLWINDOW),
            IsElevated = elevated,
            PerMonitorDpi = PInvoke.GetAwarenessFromDpiAwarenessContext(PInvoke.GetWindowDpiAwarenessContext(hwnd))
                            == Windows.Win32.UI.HiDpi.DPI_AWARENESS.DPI_AWARENESS_PER_MONITOR_AWARE,
        };
    }

    internal static string TitleOf(HWND hwnd)
    {
        int length = PInvoke.GetWindowTextLength(hwnd);
        if (length <= 0)
        {
            return string.Empty;
        }

        Span<char> buffer = length < 256 ? stackalloc char[length + 1] : new char[length + 1];
        unsafe
        {
            fixed (char* p = buffer)
            {
                int written = PInvoke.GetWindowText(hwnd, p, buffer.Length);
                return written <= 0 ? string.Empty : new string(buffer[..written]);
            }
        }
    }

    internal static string ClassOf(HWND hwnd)
    {
        Span<char> buffer = stackalloc char[256];
        unsafe
        {
            fixed (char* p = buffer)
            {
                int written = PInvoke.GetClassName(hwnd, p, buffer.Length);
                return written <= 0 ? string.Empty : new string(buffer[..written]);
            }
        }
    }

    /// <summary>
    /// The rectangle the window actually occupies on screen.
    /// </summary>
    /// <remarks>
    /// <c>GetWindowRect</c> includes the resize border, which is invisible and
    /// 9 px wide at 150 % on this desk: laying windows out with it leaves a
    /// visible gap on one side and none on the other.
    /// </remarks>
    internal static Rect? FrameBoundsOf(HWND hwnd)
    {
        unsafe
        {
            RECT bounds;
            HRESULT hr = PInvoke.DwmGetWindowAttribute(
                hwnd, DWMWINDOWATTRIBUTE.DWMWA_EXTENDED_FRAME_BOUNDS, &bounds, (uint)sizeof(RECT));
            return hr.Succeeded ? Rect.FromEdges(bounds.left, bounds.top, bounds.right, bounds.bottom) : null;
        }
    }

    /// <summary>
    /// Why the compositor is not drawing this window, if it is not.
    /// </summary>
    /// <remarks>
    /// This is the flag AkuWM reads back after every hide: "hidden" means the
    /// cloak is on according to DWM, not that a cloak call was made. A window
    /// that refuses the cloak shows up here and is reported by <c>doctor</c>
    /// instead of quietly staying half-hidden.
    /// </remarks>
    internal static CloakKind CloakOf(HWND hwnd)
    {
        unsafe
        {
            uint cloaked = 0;
            HRESULT hr = PInvoke.DwmGetWindowAttribute(
                hwnd, DWMWINDOWATTRIBUTE.DWMWA_CLOAKED, &cloaked, sizeof(uint));
            return hr.Succeeded ? (CloakKind)cloaked : CloakKind.None;
        }
    }

    // Concurrent because there are two readers: the wm thread on every event,
    // and the pipe listener answering `query windows` or `doctor` from a second
    // WindowsPlatform. Two threads writing a plain Dictionary corrupts its
    // bucket chain, and a lookup that never terminates on the wm thread wedges
    // the loop until the watchdog kills the daemon.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<uint, (string Name, bool Elevated)>
        ProcessCache = new();

    /// <summary>
    /// The process's name without <c>.exe</c>, and whether AkuWM is allowed to
    /// touch its windows.
    /// </summary>
    /// <remarks>
    /// Elevation is answered the way it matters rather than the way it is
    /// defined: if this process cannot even open the other one's token, it
    /// certainly cannot position its windows, so it is treated as elevated.
    /// Games are the case that matters, and they are elevated.
    /// </remarks>
    public static (string Name, bool Elevated) ProcessFacts(uint processId)
    {
        if (processId == 0)
        {
            return (string.Empty, false);
        }

        // Process ids are reused, but only after the process is gone, and a
        // window's process outlives the window. The cache is dropped whole on
        // every full enumeration.
        if (ProcessCache.TryGetValue(processId, out (string Name, bool Elevated) cached))
        {
            return cached;
        }

        (string, bool) facts = ReadProcessFacts(processId);
        ProcessCache[processId] = facts;
        return facts;
    }

    public static void ForgetProcesses() => ProcessCache.Clear();

    private static (string Name, bool Elevated) ReadProcessFacts(uint processId)
    {
        SafeFileHandle process = PInvoke.OpenProcess_SafeHandle(
            PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION, false, processId);

        if (process.IsInvalid)
        {
            process.Dispose();
            return (string.Empty, true);
        }

        using (process)
        {
            string name = ImageNameOf(process);
            return (name, IsElevated(process));
        }
    }

    private static string ImageNameOf(SafeFileHandle process)
    {
        Span<char> buffer = stackalloc char[520];
        uint size = (uint)buffer.Length;

        unsafe
        {
            fixed (char* p = buffer)
            {
                if (!PInvoke.QueryFullProcessImageName(
                        process, PROCESS_NAME_FORMAT.PROCESS_NAME_WIN32, new PWSTR(p), ref size))
                {
                    return string.Empty;
                }
            }
        }

        string path = new(buffer[..(int)size]);
        string file = Path.GetFileNameWithoutExtension(path);
        return file;
    }

    private static bool IsElevated(SafeFileHandle process)
    {
        if (!PInvoke.OpenProcessToken(process, TOKEN_ACCESS_MASK.TOKEN_QUERY, out SafeFileHandle token))
        {
            // Access denied: a higher integrity level than ours. That is the
            // only answer this question is asked for.
            return true;
        }

        using (token)
        {
            unsafe
            {
                TOKEN_ELEVATION elevation = default;
                if (!PInvoke.GetTokenInformation(
                        token,
                        TOKEN_INFORMATION_CLASS.TokenElevation,
                        &elevation,
                        (uint)sizeof(TOKEN_ELEVATION),
                        out uint _))
                {
                    return true;
                }

                return elevation.TokenIsElevated != 0;
            }
        }
    }
}
