using AkuWM.Core.Bindings;
using AkuWM.Core.Logging;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace AkuWM.Platform;

/// <summary>
/// The AutoHotkey script that binds the chords (plan 10.27): AkuWM never
/// hooks the keyboard itself. The script keeps a hidden Gui window titled
/// <see cref="Bindings.ScriptWindowTitle"/> as its letterbox (not its main
/// window renamed: #SingleInstance finds the previous instance by that
/// title) and re-reads <c>bindings.tsv</c> on <see cref="Bindings.ReloadMessage"/>.
/// The script is uiAccess and this process is not, so the script opens the
/// message through UIPI with ChangeWindowMessageFilterEx; without that
/// PostMessage returns false while FindWindow succeeds (2026-09-23).
/// </summary>
public static class Win32Hotkeys
{
    /// <summary>Asks the script to re-read its bindings. False when it is not running.</summary>
    public static bool PokeBindings()
    {
        HWND script = PInvoke.FindWindow("AutoHotkeyGUI", Bindings.ScriptWindowTitle);
        if (script.IsNull)
        {
            Log.Info($"bindings: no window titled \"{Bindings.ScriptWindowTitle}\"; is the script running?");
            return false;
        }

        return PInvoke.PostMessage(script, Bindings.ReloadMessage, default, default);
    }
}
