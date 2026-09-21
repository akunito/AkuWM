using AkuWM.Core.Logging;
using AkuWM.Core.Model;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Shell;

namespace AkuWM.Platform;

/// <summary>
/// Telling the taskbar that a window is fullscreen.
/// </summary>
/// <remarks>
/// <para>
/// A borderless fullscreen window does not automatically push the taskbar out
/// of the way; the shell decides that, and the way to tell it is
/// <c>ITaskbarList2::MarkFullscreenWindow</c>. Without it the bar stays on
/// top of a game that is supposed to own the screen.
/// </para>
/// <para>
/// It has to be told again when the window stops being fullscreen, or the
/// taskbar stays hidden after the game is closed -- which looks exactly like
/// a broken shell and is the kind of thing a person blames on the window
/// manager, correctly.
/// </para>
/// </remarks>
public sealed class Win32Taskbar : AkuWM.Core.Platform.ITaskbar, IDisposable
{
    private ITaskbarList2? _taskbar;
    private bool _tried;

    /// <summary>Why the taskbar could not be reached, when it could not.</summary>
    public string? Unavailable { get; private set; }

    public bool Available => Interface is not null;

    private ITaskbarList2? Interface
    {
        get
        {
            if (_tried)
            {
                return _taskbar;
            }

            _tried = true;

            try
            {
                _taskbar = (ITaskbarList2)new TaskbarList();
                _taskbar.HrInit();
            }
            catch (Exception ex)
            {
                Unavailable = $"{ex.GetType().Name}: {ex.Message}";
                Log.Warn($"the taskbar could not be reached: {Unavailable}");
                _taskbar = null;
            }

            return _taskbar;
        }
    }

    public bool MarkFullscreen(WindowHandle window, bool fullscreen)
    {
        if (Interface is not { } taskbar)
        {
            return false;
        }

        try
        {
            taskbar.MarkFullscreenWindow(new HWND((IntPtr)window.Value), fullscreen);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"the taskbar refused the fullscreen mark for {window}: {ex.Message}");
            return false;
        }
    }

    public bool ShowInTaskbar(WindowHandle window, bool shown)
    {
        if (Interface is not { } taskbar)
        {
            return false;
        }

        try
        {
            var hwnd = new HWND((IntPtr)window.Value);

            if (shown)
            {
                taskbar.AddTab(hwnd);
            }
            else
            {
                taskbar.DeleteTab(hwnd);
            }

            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"the taskbar refused to {(shown ? "show" : "hide")} {window}: {ex.Message}");
            return false;
        }
    }

    public void Dispose()
    {
        if (_taskbar is not null)
        {
            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(_taskbar);
            _taskbar = null;
        }
    }
}
