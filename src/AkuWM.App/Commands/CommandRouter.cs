using AkuWM.Core.Ipc;
using AkuWM.Core.Logging;

namespace AkuWM.App.Commands;

/// <summary>
/// One command grammar, whether the line arrives from the pipe, from the
/// process's own arguments, or (from M2) from the WebSocket.
/// </summary>
public sealed class CommandRouter
{
    private readonly ConfigCommands _config;
    private readonly DoctorCommand _doctor;

    public CommandRouter(ConfigCommands config, DoctorCommand doctor)
    {
        _config = config;
        _doctor = doctor;
    }

    /// <summary>
    /// Commands that need no running window manager, so <c>akuwm</c> answers
    /// them itself instead of refusing when the daemon is down.
    /// </summary>
    public static bool NeedsNoDaemon(string verb) =>
        verb is "config" or "doctor" or "version" or "help";

    public CommandResponse Execute(string line)
    {
        string[] tokens = CommandLine.Split(line);
        if (tokens.Length == 0)
        {
            return CommandResponse.Fail(line, "empty command");
        }

        Log.Debug(() => $"command: {line}");

        try
        {
            return tokens[0].ToLowerInvariant() switch
            {
                "config" => _config.Execute(line, tokens),
                "doctor" => _doctor.Execute(line),
                "version" => CommandResponse.Ok(line, new { version = Build.Version, build = Build.Description }),
                "help" => CommandResponse.Ok(line, new { commands = Help }),
                "exit" => CommandResponse.Ok(line, new { stopping = true }),
                _ => CommandResponse.Fail(
                    line,
                    $"'{tokens[0]}' is not a command AkuWM knows yet (M0 speaks: {string.Join(", ", Help.Select(h => h.Split(' ')[0]).Distinct())})"),
            };
        }
        catch (Exception ex)
        {
            Log.Error($"command '{line}' failed", ex);
            return CommandResponse.Fail(line, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    public static readonly string[] Help =
    [
        "config show [--layer common|<profile>|effective]",
        "config path",
        "config validate",
        "config import glazewm [--from <config.yaml>] [--ahk <hyper-desktops.ahk>] [--startup-dir <dir>] [--dry-run] [--force]",
        "doctor",
        "version",
        "help",
        "exit",
    ];
}

/// <summary>Who this build is, for <c>version</c> and the log header.</summary>
public static class Build
{
    public static string Version =>
        typeof(Build).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    public static string Description => $"AkuWM {Version} (M0: configuration, import, pipe, doctor)";
}
