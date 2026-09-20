using System.Diagnostics;
using AkuWM.Core.Compat;
using AkuWM.Core.Logging;
using AkuWM.Core.Model;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace AkuWM.Platform;

/// <summary>
/// The few things a command needs that a model cannot do: minimise, close,
/// focus, and run something.
/// </summary>
public sealed class Win32DeskPlatform : IDeskPlatform
{
    private const uint WindowCloseMessage = 0x0010;

    public bool Focus(WindowHandle window) => Win32Focus.Focus(window).Succeeded;

    public void Minimize(WindowHandle window) => Win32Show.Minimize(window);

    public void Restore(WindowHandle window) => Win32Show.Restore(window);

    /// <summary>
    /// Asks a window to close, the way its own title-bar button does.
    /// </summary>
    /// <remarks>
    /// Posted, not sent: an application that wants to ask "save changes?" has
    /// to be able to, and a synchronous send from the window manager's thread
    /// would block the whole desk behind that dialog.
    /// </remarks>
    public void Close(WindowHandle window) =>
        PInvoke.PostMessage(new HWND((IntPtr)window.Value), WindowCloseMessage, default, default);

    public void Exec(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return;
        }

        try
        {
            // Through the shell, so the argument can be a document, a URL or a
            // program, which is what the callers of this pass.
            Process.Start(new ProcessStartInfo(command) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn($"could not run '{command}': {ex.Message}");
        }
    }
}
