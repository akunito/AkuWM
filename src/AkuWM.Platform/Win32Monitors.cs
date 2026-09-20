using AkuWM.Core.Model;
using Windows.Win32;
using Windows.Win32.Devices.Display;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.HiDpi;

namespace AkuWM.Platform;

/// <summary>
/// Reads the displays, and gives each one an identity that outlives it being
/// switched off.
/// </summary>
/// <remarks>
/// <para>
/// A monitor index is worthless here: this desk's monitors go to sleep, come
/// back in a different order, and the vertical one is sometimes not there at
/// all. So every monitor is identified by its <c>DISPLAY#&lt;id&gt;</c> segment --
/// the manufacturer and product code out of its EDID, which the display device
/// path carries -- and the configuration binds workspaces to a <em>role</em>
/// matched on that.
/// </para>
/// <para>
/// GDI knows the rectangles and the work areas; the display-configuration API
/// knows the names and the identities. They are joined on the GDI device name
/// (<c>\\.\DISPLAY1</c>), which both of them speak.
/// </para>
/// </remarks>
public static class Win32Monitors
{
    /// <summary><c>MONITORINFOF_PRIMARY</c>; the only flag the structure defines.</summary>
    private const uint MonitorInfoPrimary = 1;

    public static List<MonitorSnapshot> Enumerate()
    {
        Dictionary<string, (string Friendly, string DevicePath)> identities = Identities();
        var monitors = new List<MonitorSnapshot>(4);

        unsafe
        {
            PInvoke.EnumDisplayMonitors(
                default,
                (RECT*)null,
                (handle, _, _, _) =>
                {
                    MonitorSnapshot? snapshot = Read(handle, identities);
                    if (snapshot is not null)
                    {
                        monitors.Add(snapshot);
                    }

                    return true;
                },
                IntPtr.Zero);
        }

        // Primary first: it is the one a workspace falls back to, and the one
        // the fullscreen tests measure.
        monitors.Sort((a, b) => a.IsPrimary == b.IsPrimary
            ? string.CompareOrdinal(a.DeviceName, b.DeviceName)
            : a.IsPrimary ? -1 : 1);
        return monitors;
    }

    private static unsafe MonitorSnapshot? Read(
        HMONITOR handle, Dictionary<string, (string Friendly, string DevicePath)> identities)
    {
        var info = new MONITORINFOEXW { monitorInfo = { cbSize = (uint)sizeof(MONITORINFOEXW) } };
        if (!PInvoke.GetMonitorInfo(handle, (MONITORINFO*)&info))
        {
            return null;
        }

        string device = info.szDevice.ToString();
        identities.TryGetValue(device, out (string Friendly, string DevicePath) identity);

        uint dpi = 96;
        if (PInvoke.GetDpiForMonitor(handle, MONITOR_DPI_TYPE.MDT_EFFECTIVE_DPI, out uint dpiX, out _).Succeeded)
        {
            dpi = dpiX;
        }

        RECT bounds = info.monitorInfo.rcMonitor;
        RECT work = info.monitorInfo.rcWork;

        return new MonitorSnapshot
        {
            Handle = new MonitorHandle(handle.Value),
            DeviceName = device,
            FriendlyName = identity.Friendly ?? string.Empty,
            HardwareId = HardwareIdOf(identity.DevicePath),
            Bounds = Rect.FromEdges(bounds.left, bounds.top, bounds.right, bounds.bottom),
            WorkArea = Rect.FromEdges(work.left, work.top, work.right, work.bottom),
            Dpi = dpi,
            IsPrimary = (info.monitorInfo.dwFlags & MonitorInfoPrimary) != 0,
        };
    }

    /// <summary>
    /// <c>\\?\DISPLAY#SAM0F1E#5&amp;1a2b3c&amp;0&amp;UID4353#{guid}</c> → <c>SAM0F1E</c>.
    /// </summary>
    /// <remarks>
    /// The first segment is three letters of manufacturer and four hex digits
    /// of product code, straight out of the EDID. The rest of the path is the
    /// adapter and the output it happens to be plugged into, which is exactly
    /// the part that changes -- so it is dropped. Two identical models would
    /// collide; this desk has four different ones, and the full path is kept
    /// alongside for the day that stops being true.
    /// </remarks>
    internal static string HardwareIdOf(string? devicePath)
    {
        if (string.IsNullOrEmpty(devicePath))
        {
            return string.Empty;
        }

        const string marker = "DISPLAY#";
        int start = devicePath.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return devicePath;
        }

        start += marker.Length;
        int end = devicePath.IndexOf('#', start);
        return end < 0 ? devicePath[start..] : devicePath[start..end];
    }

    /// <summary>GDI device name → the monitor's friendly name and device path.</summary>
    private static unsafe Dictionary<string, (string Friendly, string DevicePath)> Identities()
    {
        var identities = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);

        if (PInvoke.GetDisplayConfigBufferSizes(
                QUERY_DISPLAY_CONFIG_FLAGS.QDC_ONLY_ACTIVE_PATHS, out uint pathCount, out uint modeCount) != 0)
        {
            return identities;
        }

        var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
        var modes = new DISPLAYCONFIG_MODE_INFO[modeCount];

        fixed (DISPLAYCONFIG_PATH_INFO* pathPtr = paths)
        fixed (DISPLAYCONFIG_MODE_INFO* modePtr = modes)
        {
            if (PInvoke.QueryDisplayConfig(
                    QUERY_DISPLAY_CONFIG_FLAGS.QDC_ONLY_ACTIVE_PATHS,
                    ref pathCount, pathPtr, ref modeCount, modePtr, null) != 0)
            {
                return identities;
            }
        }

        for (int i = 0; i < pathCount; i++)
        {
            DISPLAYCONFIG_PATH_INFO path = paths[i];

            var source = new DISPLAYCONFIG_SOURCE_DEVICE_NAME
            {
                header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
                {
                    type = DISPLAYCONFIG_DEVICE_INFO_TYPE.DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME,
                    size = (uint)sizeof(DISPLAYCONFIG_SOURCE_DEVICE_NAME),
                    adapterId = path.sourceInfo.adapterId,
                    id = path.sourceInfo.id,
                },
            };

            var target = new DISPLAYCONFIG_TARGET_DEVICE_NAME
            {
                header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
                {
                    type = DISPLAYCONFIG_DEVICE_INFO_TYPE.DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME,
                    size = (uint)sizeof(DISPLAYCONFIG_TARGET_DEVICE_NAME),
                    adapterId = path.targetInfo.adapterId,
                    id = path.targetInfo.id,
                },
            };

            if (PInvoke.DisplayConfigGetDeviceInfo(&source.header) != 0
                || PInvoke.DisplayConfigGetDeviceInfo(&target.header) != 0)
            {
                continue;
            }

            identities[source.viewGdiDeviceName.ToString()] =
                (target.monitorFriendlyDeviceName.ToString(), target.monitorDevicePath.ToString());
        }

        return identities;
    }
}
