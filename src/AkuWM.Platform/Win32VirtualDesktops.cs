using AkuWM.Core.Logging;
using AkuWM.Core.Model;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Shell;

namespace AkuWM.Platform;

/// <summary>
/// Which windows are on the native virtual desktop the user is looking at.
/// </summary>
/// <remarks>
/// <para>
/// Windows has its own virtual desktops, and a window parked on another one is
/// cloaked by the shell -- the same flag a window manager sets to hide a
/// workspace. Telling the two apart matters: one belongs to AkuWM, the other
/// belongs to the shell and must be left alone.
/// </para>
/// <para>
/// <c>IVirtualDesktopManager</c> is the documented, supported interface, and
/// it answers exactly this question. The undocumented ones (the ones that can
/// move a window between desktops, which is what the startup fold needs) come
/// at M3 from the MIT sources named in LICENSING.md.
/// </para>
/// </remarks>
public sealed class Win32VirtualDesktops : IDisposable
{
    private IVirtualDesktopManager? _manager;
    private bool _warned;

    /// <summary>True when the question can be answered at all.</summary>
    public bool Available => _manager is not null;

    /// <summary>Why it cannot, when it cannot.</summary>
    public string? Unavailable { get; private set; }

    /// <summary>How many calls came back with an error, for doctor.</summary>
    public int Failures { get; private set; }

    public Win32VirtualDesktops()
    {
        try
        {
            _manager = (IVirtualDesktopManager)new VirtualDesktopManager();
        }
        catch (Exception ex)
        {
            Unavailable = $"{ex.GetType().Name}: {ex.Message}";
            Log.Warn($"the virtual desktop manager is not available: {Unavailable}");
            _manager = null;
        }
    }

    /// <summary>
    /// True when the window is on the desktop in front of the user; true as
    /// well when the question cannot be answered, because assuming a window is
    /// somewhere else would make AkuWM ignore windows it should arrange.
    /// </summary>
    public bool IsOnCurrentDesktop(WindowHandle handle)
    {
        if (_manager is null)
        {
            return true;
        }

        try
        {
            unsafe
            {
                BOOL onCurrent = default;
                _manager.IsWindowOnCurrentVirtualDesktop(new HWND((IntPtr)handle.Value), &onCurrent);
                return onCurrent;
            }
        }
        catch (Exception ex)
        {
            Failures++;

            // A window that is being destroyed answers with an error; that is
            // normal and not worth a line every time.
            if (!_warned)
            {
                Log.Debug(() => $"IsWindowOnCurrentVirtualDesktop({handle}): {ex.Message}");
                _warned = true;
            }

            return true;
        }
    }

    /// <summary>
    /// Which native virtual desktop a window is on, as a guid.
    /// </summary>
    /// <remarks>
    /// The definitive answer, and the one to trust when
    /// <see cref="IsOnCurrentDesktop"/> looks wrong: two windows with different
    /// guids are on different desktops, whatever else anything says.
    /// </remarks>
    public Guid? DesktopOf(WindowHandle handle)
    {
        if (_manager is null)
        {
            return null;
        }

        try
        {
            unsafe
            {
                Guid desktop;
                _manager.GetWindowDesktopId(new HWND((IntPtr)handle.Value), &desktop);
                return desktop;
            }
        }
        catch (Exception)
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (_manager is not null)
        {
            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(_manager);
            _manager = null;
        }
    }
}
