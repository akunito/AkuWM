using System.Runtime.Versioning;

namespace AkuWM.Platform;

/// <summary>
/// Everything that touches Win32, COM and DWM lives in this project.
/// </summary>
/// <remarks>
/// <para>
/// M0 carries the shape only. What lands here at M1, in this order:
/// window enumeration and attributes (styles, class, process, elevation from
/// the process token, <c>DWMWA_EXTENDED_FRAME_BOUNDS</c> for the visible frame
/// and <c>DWMWA_CLOAKED</c> read back after every cloak); monitors by identity
/// (<c>EnumDisplayMonitors</c> plus <c>DisplayConfigGetDeviceInfo</c> for the
/// EDID, work areas, per-monitor DPI); positioning through
/// <c>DeferWindowPos</c> batches; the cloak through
/// <c>IApplicationView::SetCloak</c>; <c>ITaskbarList2::MarkFullscreenWindow</c>;
/// the <c>SetWinEventHook</c> callbacks and the two low-level hooks.
/// </para>
/// <para>
/// Written from Microsoft's documentation and from the MIT sources named in
/// LICENSING.md, never from GlazeWM, AutoHotkey or Zebar.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public static class PlatformInfo
{
    /// <summary>What this project will be able to do, and does not do yet.</summary>
    public const string Milestone = "M1";
}
