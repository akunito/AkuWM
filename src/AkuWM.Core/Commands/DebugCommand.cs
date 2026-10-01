using AkuWM.Core.Config;
using AkuWM.Core.Ipc;
using AkuWM.Core.Logging;

namespace AkuWM.Core.Commands;

/// <summary>
/// <c>akuwm debug on|off|status</c>: the log level, flipped in the running
/// daemon and remembered in the marker file its next start reads.
/// </summary>
public sealed class DebugCommand
{
    private readonly ConfigPaths _paths;
    private readonly Func<object>? _layout;

    /// <param name="layout">The desk's <c>LayoutDump</c>, read on the window-manager thread; null outside the daemon.</param>
    public DebugCommand(ConfigPaths paths, Func<object>? layout = null)
    {
        _paths = paths;
        _layout = layout;
    }

    public CommandResponse Execute(string line, string[] tokens)
    {
        string verb = tokens.Length > 1 ? tokens[1].ToLowerInvariant() : "status";
        string? problem = null;
        switch (verb)
        {
            case "on":
                Log.Level = LogLevel.Debug;
                problem = Marker(true);
                break;
            case "off":
                Log.Level = LogLevel.Info;
                problem = Marker(false);
                break;
            case "status":
                break;

            case "layout":
                // Every workspace's tiling tree with what each window says its
                // workspace is: the view that would have shown Notepad++ in
                // two trees on 2026-10-01 (the compat view dedupes).
                return _layout is null
                    ? CommandResponse.Fail(line, "debug layout needs the daemon")
                    : CommandResponse.Ok(line, _layout());

            default:
                return CommandResponse.Fail(line, $"'{tokens[1]}' is not on, off, status or layout");
        }

        return CommandResponse.Ok(line, new { debug = Log.DebugOn, marker = _paths.DebugMarkerFile, problem });
    }

    private string? Marker(bool present)
    {
        try
        {
            if (present)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_paths.DebugMarkerFile)!);
                File.WriteAllText(_paths.DebugMarkerFile, string.Empty);
            }
            else if (File.Exists(_paths.DebugMarkerFile))
            {
                File.Delete(_paths.DebugMarkerFile);
            }

            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"the marker was not written: {ex.Message}";
        }
    }
}
