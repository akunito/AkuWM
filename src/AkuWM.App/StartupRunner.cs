using System.Diagnostics;
using AkuWM.Core.Config;
using AkuWM.Core.Logging;

namespace AkuWM.App;

/// <summary>
/// Runs the configured <c>startup</c> list, by phase.
/// </summary>
/// <remarks>
/// <para>
/// The list was imported at M0, validated on every load, and never run: the
/// bar was started by its own Startup-folder shortcut, so a daemon restart
/// never brought it back, and after a day with port 6123 held by a dead
/// manager the bar had simply given up (2026-09-22).
/// </para>
/// <para>
/// An entry whose executable is already running is skipped, so this list and
/// the Startup folder can both name Zebar without starting two of it. A
/// shortcut (.lnk) is resolved by the shell, and the process it starts is
/// the target's, so the check reads the target's file name out of the link.
/// </para>
/// </remarks>
public sealed class StartupRunner
{
    private readonly IReadOnlyList<StartupConfig> _entries;
    private readonly HashSet<string> _ran = new(StringComparer.OrdinalIgnoreCase);

    public StartupRunner(IReadOnlyList<StartupConfig>? entries) => _entries = entries ?? [];

    /// <summary>Starts every enabled entry of the phase that has not run yet this session.</summary>
    public void Run(string phase)
    {
        for (int i = 0; i < _entries.Count; i++)
        {
            StartupConfig entry = _entries[i];
            string after = string.IsNullOrEmpty(entry.After) ? "now" : entry.After;

            if (entry.Enabled == false
                || !string.Equals(after, phase, StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(entry.Command)
                || !_ran.Add(entry.Id ?? entry.Command))
            {
                continue;
            }

            string command = Environment.ExpandEnvironmentVariables(entry.Command);
            string? exe = ExecutableOf(command);

            if (exe is not null && Process.GetProcessesByName(exe).Length > 0)
            {
                Log.Info($"startup: {entry.Name ?? exe} is already running");
                continue;
            }

            int delay = entry.DelayMs ?? 0;
            if (delay > 0)
            {
                _ = Task.Delay(delay).ContinueWith(_ => Start(entry, command), TaskScheduler.Default);
            }
            else
            {
                Start(entry, command);
            }
        }
    }

    private static void Start(StartupConfig entry, string command)
    {
        try
        {
            Process.Start(new ProcessStartInfo(command) { UseShellExecute = true });
            Log.Info($"startup: started {entry.Name ?? command}");
        }
        catch (Exception ex)
        {
            Log.Warn($"startup: {entry.Name ?? command} did not start: {ex.Message}");
        }
    }

    /// <summary>The process name a command will run under, or null when it cannot be told.</summary>
    internal static string? ExecutableOf(string command)
    {
        string path = command;

        if (path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            path = ShortcutTarget(path) ?? path;
        }

        string name = Path.GetFileNameWithoutExtension(path);
        return name.Length == 0 || name.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ? null : name;
    }

    /// <summary>
    /// The target path inside a .lnk, read from the file rather than through
    /// the shell's COM object, which would make this thread an apartment.
    /// </summary>
    /// <remarks>
    /// Shell link format: after the 76-byte header, an optional link-target
    /// id list (flag bit 0), then the link-info block (flag bit 1) whose
    /// local base path is a null-terminated ANSI string at the offset stored
    /// at +16 of the block. Enough for a Startup-folder shortcut.
    /// </remarks>
    private static string? ShortcutTarget(string file)
    {
        try
        {
            byte[] bytes = File.ReadAllBytes(file);
            if (bytes.Length < 76 || BitConverter.ToInt32(bytes, 0) != 0x4C)
            {
                return null;
            }

            int flags = BitConverter.ToInt32(bytes, 20);
            int at = 76;
            if ((flags & 1) != 0)
            {
                at += BitConverter.ToUInt16(bytes, at) + 2;
            }

            if ((flags & 2) == 0 || at + 28 > bytes.Length)
            {
                return null;
            }

            int basePath = at + BitConverter.ToInt32(bytes, at + 16);
            int end = Array.IndexOf(bytes, (byte)0, basePath);
            return end > basePath
                ? System.Text.Encoding.Default.GetString(bytes, basePath, end - basePath)
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
