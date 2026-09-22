namespace AkuWM.Core.Model;

/// <summary>A window handle, kept as a number so <c>AkuWM.Core</c> needs no Win32 types.</summary>
public readonly record struct WindowHandle(long Value)
{
    public static readonly WindowHandle None = new(0);

    public bool IsNone => Value == 0;

    public override string ToString() => $"0x{Value:x}";
}

public readonly record struct MonitorHandle(long Value)
{
    public static readonly MonitorHandle None = new(0);

    public override string ToString() => $"0x{Value:x}";
}

/// <summary>Why a window is invisible to the compositor.</summary>
[Flags]
public enum CloakKind
{
    None = 0,

    /// <summary>The application cloaked itself (an app that went to the tray).</summary>
    App = 1,

    /// <summary>The shell cloaked it -- which is what AkuWM does to hide a workspace.</summary>
    Shell = 2,

    /// <summary>It belongs to another native virtual desktop.</summary>
    InheritedOrOtherDesktop = 4,
}

/// <summary>
/// Everything AkuWM knows about a window at one instant, read from the
/// platform and never written back.
/// </summary>
/// <remarks>
/// A snapshot, not a live handle: the model is updated from these, so the
/// logic that decides what a window should be is testable on Linux against
/// values taken from the real desk.
/// </remarks>
public sealed record WindowSnapshot
{
    public required WindowHandle Handle { get; init; }
    public required uint ProcessId { get; init; }

    /// <summary>Without <c>.exe</c>, as the rules are written.</summary>
    public required string ProcessName { get; init; }

    public required string ClassName { get; init; }
    public required string Title { get; init; }

    /// <summary>
    /// What <c>GetWindowRect</c> says: it includes the invisible resize border
    /// (9 px at 150 % on this desk), so it is never what a layout should use.
    /// </summary>
    public required Rect WindowRect { get; init; }

    /// <summary>
    /// <c>DWMWA_EXTENDED_FRAME_BOUNDS</c>: what the window looks like on
    /// screen. This is the rectangle a layout compares and positions against.
    /// </summary>
    public required Rect FrameBounds { get; init; }

    public required MonitorHandle Monitor { get; init; }
    public required bool IsVisible { get; init; }
    public required bool IsMinimized { get; init; }
    public required bool IsMaximized { get; init; }
    public required CloakKind Cloak { get; init; }
    public required bool IsTopmost { get; init; }

    /// <summary>No <c>WS_THICKFRAME</c>: the layout must float it instead of sizing it.</summary>
    public required bool IsResizable { get; init; }

    public required bool IsToolWindow { get; init; }

    /// <summary>
    /// Whether the window scales itself per monitor, or leaves that to
    /// Windows (system-DPI aware, or unaware).
    /// </summary>
    /// <remarks>
    /// Measured on this desk 2026-09-22 with Notepad++ (system-DPI aware):
    /// a window of that kind whose OUTER rectangle touches a monitor of a
    /// different scale, by a single pixel, is rescaled by Windows right
    /// there -- 3840 wide at x=0 is fine, 3841 becomes 5761, and 1000 wide
    /// straddling the seam became 2460. Every edge column AkuWM lays out has
    /// an invisible 9 px border past the monitor's edge, so every such
    /// placement blew the window up and it could then not be resized, moved
    /// or floated. True by default: a window not read from Windows is
    /// assumed to look after itself.
    /// </remarks>
    public bool PerMonitorDpi { get; init; } = true;

    /// <summary>
    /// Runs at a higher integrity level than AkuWM. It can be cloaked but
    /// never positioned (UIPI), which is the whole reason the distinction is
    /// carried in the model.
    /// </summary>
    public required bool IsElevated { get; init; }

    /// <summary>
    /// On the native virtual desktop the user is looking at.
    /// </summary>
    /// <remarks>
    /// A window parked on another one is cloaked by the shell, with the very
    /// same flag a window manager uses to hide a workspace. Without this
    /// answer the two are indistinguishable, and AkuWM would count windows
    /// that belong to the shell.
    /// </remarks>
    public bool OnCurrentVirtualDesktop { get; init; } = true;

    /// <summary>
    /// Which native virtual desktop it is on, when that can be read. Two
    /// windows with different values are on different desktops -- the answer
    /// to trust when the "is it current" flag looks wrong.
    /// </summary>
    public string? VirtualDesktop { get; init; }

    /// <summary>
    /// The difference between the outer rectangle and the visible frame, as a
    /// margin. Positioning has to add it back or every window lands 9 px off.
    /// </summary>
    public (int Top, int Right, int Bottom, int Left) BorderDelta =>
    (
        FrameBounds.Top - WindowRect.Top,
        WindowRect.Right - FrameBounds.Right,
        WindowRect.Bottom - FrameBounds.Bottom,
        FrameBounds.Left - WindowRect.Left
    );

    public bool IsCloaked => Cloak != CloakKind.None;

    public override string ToString() =>
        $"{Handle} {ProcessName} [{ClassName}] \"{Title}\" {FrameBounds}";
}

/// <summary>One physical display, identified by what it is rather than where it is.</summary>
public sealed record MonitorSnapshot
{
    public required MonitorHandle Handle { get; init; }

    /// <summary><c>\\.\DISPLAY1</c>: stable only until the displays are re-enumerated.</summary>
    public required string DeviceName { get; init; }

    /// <summary>The name the Settings app shows, e.g. "Odyssey G70NC".</summary>
    public required string FriendlyName { get; init; }

    /// <summary>
    /// Manufacturer, product code and serial out of the EDID, as one string.
    /// This is what a monitor role is matched on: it survives a sleep cycle,
    /// a cable swap and a re-enumeration, which an index does not.
    /// </summary>
    public required string HardwareId { get; init; }

    public required Rect Bounds { get; init; }

    /// <summary>Bounds minus the taskbar. A fullscreen window covers Bounds, not this.</summary>
    public required Rect WorkArea { get; init; }

    public required uint Dpi { get; init; }
    public required bool IsPrimary { get; init; }

    public double ScaleFactor => Dpi / 96.0;

    /// <summary>Taller than wide: the layout stacks on this one by default.</summary>
    public bool IsVertical => Bounds.Height > Bounds.Width;

    public override string ToString() =>
        $"{DeviceName} \"{FriendlyName}\" [{HardwareId}] {Bounds} @{Dpi}dpi{(IsPrimary ? " primary" : string.Empty)}";
}
