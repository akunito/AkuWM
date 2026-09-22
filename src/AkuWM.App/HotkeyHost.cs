using System.Diagnostics;
using AkuWM.Core.Config;
using AkuWM.Core.Logging;

namespace AkuWM.App;

/// <summary>
/// The hotkey process (the AutoHotkey script) around a game: killed when a
/// window with the <c>anticheat</c> action appears, started again when the
/// last one is gone. See docs/input-and-anticheat.md.
/// </summary>
internal static class HotkeyHost
{
    public static void Toggle(HotkeyHostConfig? host, bool wanted)
    {
        if (host is null || string.IsNullOrWhiteSpace(host.Process) || string.IsNullOrWhiteSpace(host.Command))
        {
            Log.Warn("game mode: general.hotkey_host names no process and command, so the hotkey process is left as it is");
            return;
        }

        string name = host.Process.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? host.Process[..^4] : host.Process;
        Process[] running = Process.GetProcessesByName(name);

        if (!wanted)
        {
            int killed = 0;
            for (int i = 0; i < running.Length; i++)
            {
                try
                {
                    running[i].Kill();
                    killed++;
                }
                catch (Exception ex)
                {
                    Log.Warn($"game mode: could not stop {name} (pid {running[i].Id}): {ex.Message}");
                }
                finally
                {
                    running[i].Dispose();
                }
            }

            Log.Info($"game mode: stopped {killed} {name} process(es) for the game");
            return;
        }

        for (int i = 0; i < running.Length; i++)
        {
            running[i].Dispose();
        }

        if (running.Length > 0)
        {
            Log.Info($"game mode: {name} is already running");
            return;
        }

        try
        {
            // Through the shell: the command is a .lnk on this desk.
            Process.Start(new ProcessStartInfo(Environment.ExpandEnvironmentVariables(host.Command)) { UseShellExecute = true });
            Log.Info($"game mode: started {name} again");
        }
        catch (Exception ex)
        {
            Log.Warn($"game mode: {host.Command} did not start: {ex.Message}");
        }
    }
}
