using AkuWM.Core.Ipc;
using AkuWM.Core.Logging;

namespace AkuWM.Core.Commands;

/// <summary>
/// One command grammar, whether the line arrives from the pipe, from the
/// process's own arguments, or (from M2) from the WebSocket.
/// </summary>
public sealed class CommandRouter
{
    private readonly ConfigCommands _config;
    private readonly DoctorCommand _doctor;
    private readonly QueryCommands? _query;
    private readonly ShadowCommand? _shadow;
    private readonly MonitorCommands? _monitors;
    private readonly UncloakCommand? _uncloak;
    private readonly BenchCommand? _bench;
    private readonly RescueCommand? _rescue;
    private readonly CompatCommand? _compat;
    private readonly StateCommand? _state;

    /// <param name="query">
    /// Null on a host with no platform layer -- running the CLI on Linux, or a
    /// unit test that only cares about the configuration. The commands that
    /// need one then say so instead of pretending the desk is empty.
    /// </param>
    public CommandRouter(
        ConfigCommands config,
        DoctorCommand doctor,
        QueryCommands? query = null,
        ShadowCommand? shadow = null,
        MonitorCommands? monitors = null,
        UncloakCommand? uncloak = null,
        BenchCommand? bench = null,
        RescueCommand? rescue = null,
        CompatCommand? compat = null,
        StateCommand? state = null)
    {
        _config = config;
        _doctor = doctor;
        _query = query;
        _shadow = shadow;
        _monitors = monitors;
        _uncloak = uncloak;
        _bench = bench;
        _rescue = rescue;
        _compat = compat;
        _state = state;
    }

    /// <summary>
    /// Commands that need no running window manager, so <c>akuwm</c> answers
    /// them itself instead of refusing when the daemon is down.
    /// </summary>
    /// <remarks>
    /// <c>query</c> and <c>shadow</c> are in the list only while AkuWM manages
    /// nothing: in M1 they are computed from a fresh set of snapshots, so a
    /// second process gets the same answer as the daemon would. From M2 they
    /// are answered from the model on the wm thread and come off this list --
    /// a query must never report a desk assembled by a process that is not the
    /// one arranging it.
    /// </remarks>
    public static bool NeedsNoDaemon(string verb) =>
        verb is "config" or "doctor" or "version" or "help" or "query" or "shadow" or "monitors"
            or "uncloak-all" or "bench" or "rescue" or "state";

    /// <summary>
    /// Commands a second process answers itself even when the daemon is up.
    /// </summary>
    /// <remarks>
    /// There is one, and it is the reason the rule exists: <c>rescue</c> is
    /// what a person runs when AkuWM has stopped behaving, and handing it to
    /// the misbehaving process to execute would be handing it to the problem.
    /// It stops that process and works from the files on disk instead.
    /// </remarks>
    public static bool NeverDelegates(string verb) => verb is "rescue";

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
                "query" => _query?.Execute(line, tokens)
                    ?? CommandResponse.Fail(line, "there is no platform layer on this host to query"),
                "shadow" => _shadow?.Execute(line, tokens)
                    ?? CommandResponse.Fail(line, "there is no platform layer on this host to shadow"),
                "monitors" => _monitors?.Execute(line, tokens)
                    ?? CommandResponse.Fail(line, "there is no platform layer on this host to read monitors from"),
                "bench" => _bench?.Execute(line, tokens)
                    ?? CommandResponse.Fail(line, "there is no platform layer on this host to measure"),
                "uncloak-all" => _uncloak?.Execute(line)
                    ?? CommandResponse.Fail(line, "there is no platform layer on this host to uncloak with"),
                "rescue" => _rescue?.Execute(line, tokens)
                    ?? CommandResponse.Fail(line, "there is no platform layer on this host to rescue"),
                "state" => _state?.Execute(line)
                    ?? CommandResponse.Fail(line, "there is no state directory on this host"),
                "compat" => _compat?.Execute(line, tokens)
                    ?? CommandResponse.Fail(line, "AkuWM is not managing the desk, so there is nothing to ask"),
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
        "query monitors",
        "query windows [--all]",
        "query focused",
        "shadow view",
        "shadow diff",
        "shadow watch [--seconds 3600] [--every 5]",
        "monitors list",
        "monitors identify [--dry-run]",
        "uncloak-all",
        "rescue [--all] [--keep-daemon] [--forgive]",
        "state",
        "compat <query|command> ...   (what the glazewm shim sends)",
        "bench [--rounds 20]",
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

    public static string Description => $"AkuWM {Version} (M2: it arranges the desk)";
}
