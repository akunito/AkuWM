using AkuWM.Core.Model;
using AkuWM.Core.Platform;

namespace AkuWM.Tests;

/// <summary>
/// A desk made of values, so the model is exercised on Linux.
/// </summary>
/// <remarks>
/// The snapshots the tests build with it are the shapes read off the real desk
/// -- a 3840x2160 monitor at 150 % and a vertical 1440x2560 at 125 %, the same
/// two this setup has -- so a test failing here means the decision is wrong,
/// not that the fake is unrealistic.
/// </remarks>
public sealed class FakePlatform : IPlatform
{
    public List<MonitorSnapshot> MonitorList { get; } = [];

    public List<WindowSnapshot> WindowList { get; } = [];

    public WindowHandle ForegroundWindow { get; set; } = WindowHandle.None;

    public (int X, int Y) Cursor { get; set; } = (0, 0);

    public IReadOnlyList<MonitorSnapshot> Monitors() => MonitorList;

    public IReadOnlyList<WindowSnapshot> Windows() => WindowList;

    public WindowSnapshot? Window(WindowHandle handle) =>
        WindowList.FirstOrDefault(w => w.Handle == handle);

    public WindowHandle Foreground() => ForegroundWindow;

    public (int X, int Y) CursorPosition() => Cursor;

    /// <summary>The main monitor of this desk: 4K at 150 %, taskbar at the bottom.</summary>
    public static MonitorSnapshot MainMonitor(long handle = 1) => new()
    {
        Handle = new MonitorHandle(handle),
        DeviceName = @"\\.\DISPLAY2",
        FriendlyName = "Odyssey G70NC",
        HardwareId = "SAM7233",
        Bounds = new Rect(0, 0, 3840, 2160),
        WorkArea = new Rect(0, 42, 3840, 2118),
        Dpi = 144,
        IsPrimary = true,
    };

    /// <summary>The second one: portrait, 125 %.</summary>
    public static MonitorSnapshot SecondMonitor(long handle = 2) => new()
    {
        Handle = new MonitorHandle(handle),
        DeviceName = @"\\.\DISPLAY1",
        FriendlyName = "RGB-27QHDS",
        HardwareId = "NSL2711",
        Bounds = new Rect(3840, -408, 1440, 2560),
        WorkArea = new Rect(3840, -373, 1440, 2525),
        Dpi = 120,
        IsPrimary = false,
    };

    public static WindowSnapshot Window(
        long handle,
        string process,
        string className = "Window",
        string title = "a window",
        Rect? frame = null,
        MonitorHandle? monitor = null,
        bool resizable = true,
        bool minimized = false,
        bool maximized = false,
        bool elevated = false,
        CloakKind cloak = CloakKind.None,
        bool onCurrentDesktop = true)
    {
        Rect bounds = frame ?? new Rect(100, 100, 800, 600);
        return new WindowSnapshot
        {
            Handle = new WindowHandle(handle),
            ProcessId = (uint)handle,
            ProcessName = process,
            ClassName = className,
            Title = title,
            WindowRect = bounds.Inflate(9),
            FrameBounds = bounds,
            Monitor = monitor ?? new MonitorHandle(1),
            IsVisible = true,
            IsMinimized = minimized,
            IsMaximized = maximized,
            Cloak = cloak,
            IsTopmost = false,
            IsResizable = resizable,
            IsToolWindow = false,
            IsElevated = elevated,
            OnCurrentVirtualDesktop = onCurrentDesktop,
        };
    }
}
