using System.Diagnostics;
using AkuWM.Core.Config;
using AkuWM.Core.Ipc;
using AkuWM.Core.Logging;
using AkuWM.Core.Model;
using AkuWM.Core.Platform;
using AkuWM.Core.State;

namespace AkuWM.Core.Commands;

/// <summary>
/// <c>akuwm rescue</c>: stop the window manager and give the desk back.
/// </summary>
/// <remarks>
/// <para>
/// This is the way out of a bad afternoon. AkuWM hides windows by cloaking
/// them and moves the rest into a layout; if it goes wrong while it is doing
/// either, the person in front of the machine is looking at a desk with
/// windows missing and no obvious way to get them back. Rescue is the obvious
/// way.
/// </para>
/// <para>
/// It assumes nothing about the process it is rescuing from. It does not ask
/// the daemon to tidy up -- a daemon that could tidy up would not need
/// rescuing -- it stops it and then works from the two files the daemon wrote
/// before it touched anything: the cloak ledger and the geometry journal. So
/// it works when AkuWM has crashed, when it is frozen, and when it is running
/// perfectly and simply is not wanted.
/// </para>
/// <para>
/// It is deliberately narrow. Only windows AkuWM recorded are touched;
/// <c>--all</c> widens it to every shell-cloaked window on the desk, which is
/// the right thing when the records themselves were lost and the wrong thing
/// while another window manager is running, because its hidden workspaces
/// would all appear at once.
/// </para>
/// </remarks>
public sealed class RescueCommand
{
    private readonly ConfigPaths _paths;
    private readonly IPlatform _platform;
    private readonly IPlatformActions _actions;
    private readonly Func<PipeClient> _client;
    private readonly Func<int, string?> _processName;
    private readonly Action<int> _kill;

    /// <param name="processName">
    /// The name of a running process, or null when there is none. Injected so
    /// the tests can rescue from a daemon that never existed.
    /// </param>
    public RescueCommand(
        ConfigPaths paths,
        IPlatform platform,
        IPlatformActions actions,
        Func<PipeClient>? client = null,
        Func<int, string?>? processName = null,
        Action<int>? kill = null)
    {
        _paths = paths;
        _platform = platform;
        _actions = actions;
        _client = client ?? (() => new PipeClient());
        _processName = processName ?? LiveProcessName;
        _kill = kill ?? KillProcess;
    }

    public CommandResponse Execute(string line, string[] tokens)
    {
        bool everything = tokens.Any(t => t.Equals("--all", StringComparison.OrdinalIgnoreCase));
        bool keepRunning = tokens.Any(t => t.Equals("--keep-daemon", StringComparison.OrdinalIgnoreCase));
        bool forgive = tokens.Any(t => t.Equals("--forgive", StringComparison.OrdinalIgnoreCase));

        Log.Info($"rescue: stopping AkuWM and putting the desk back{(everything ? ", every cloaked window" : string.Empty)}");

        string daemon = keepRunning ? "left running" : StopTheDaemon();

        // The cloaks first: a window nobody can see is the urgent part, and
        // moving one that is still invisible helps nobody.
        var ledger = new CloakLedger(_paths.CloakLedgerFile);
        RecoveryResult cloaks = ledger.Recover(_platform, _actions);

        var swept = new List<object>();
        if (everything)
        {
            swept = SweepEveryCloak();
        }

        var journal = new GeometryJournal(_paths.GeometryJournalFile);
        GeometryRestoreResult geometry = journal.Restore(_platform, _actions);

        if (forgive)
        {
            new SessionMarker(_paths.SessionFile).Forgive();
        }

        int given = cloaks.Recovered.Count + swept.Count;
        Log.Info($"rescue: {given} window(s) visible again, {geometry.Restored.Count} put back where they were");

        return CommandResponse.Ok(line, new
        {
            daemon,
            uncloaked = given,
            refused = cloaks.Failed.Count,
            restored = geometry.Restored.Count,
            staleEntries = cloaks.Stale.Count + geometry.Stale.Count,
            windows = cloaks.Recovered
                .Select(w => new { handle = w.Handle, process = w.Process, title = w.Title })
                .ToArray(),
            alsoUncloaked = swept,
            refusals = cloaks.Failed
                .Select(f => new { handle = f.Window.Handle, process = f.Window.Process, error = f.Error })
                .ToArray(),
            safeModeCleared = forgive,
        });
    }

    /// <summary>
    /// Asks the daemon to stop, and stops it itself if it will not.
    /// </summary>
    /// <remarks>
    /// The polite request has a short deadline on purpose. The reason someone
    /// is running this is usually that the daemon has stopped answering, and
    /// waiting five seconds for an answer that is not coming is five seconds
    /// of a desk with windows missing.
    /// </remarks>
    private string StopTheDaemon()
    {
        PipeClient client = _client();
        if (client.IsRunning(timeoutMs: 300))
        {
            CommandResponse response = client.Send("exit", timeoutMs: 1500);
            if (response.Success)
            {
                return "asked to stop, and it did";
            }
        }

        Session? session = new SessionMarker(_paths.SessionFile).Previous;
        if (session is null || session.CleanExit)
        {
            return "was not running";
        }

        string? name = _processName(session.Pid);
        if (name is null)
        {
            return "was not running (the last run left no process behind)";
        }

        if (!name.Contains("akuwm", StringComparison.OrdinalIgnoreCase))
        {
            // The pid was handed to somebody else after AkuWM died. Killing it
            // would be a rescue that breaks something unrelated.
            return $"was not running (pid {session.Pid} belongs to {name} now)";
        }

        try
        {
            _kill(session.Pid);
            Log.Warn($"rescue: AkuWM (pid {session.Pid}) did not answer and was stopped");
            return $"did not answer; stopped (pid {session.Pid})";
        }
        catch (Exception ex)
        {
            Log.Error($"rescue: could not stop pid {session.Pid}: {ex.Message}");
            return $"could not be stopped: {ex.Message}";
        }
    }

    /// <summary>
    /// Every window the shell has cloaked, whoever cloaked it.
    /// </summary>
    /// <remarks>
    /// Windows parked on another native virtual desktop carry the same flag
    /// and are left alone -- uncloaking one drags it onto this desktop, which
    /// is not a rescue but a mess.
    /// </remarks>
    private List<object> SweepEveryCloak()
    {
        var swept = new List<object>();

        // Read the desk once, then change it: what is being walked is a list
        // of windows that the very next line makes out of date.
        foreach (WindowSnapshot window in _platform.Windows().ToArray())
        {
            if (!window.Cloak.HasFlag(CloakKind.Shell) || !window.OnCurrentVirtualDesktop)
            {
                continue;
            }

            _actions.SetCloak(window.Handle, false);

            // The shell has a spelling of this call that reports success and
            // does nothing, so the flag is read back rather than believed.
            if (_platform.Window(window.Handle)?.Cloak.HasFlag(CloakKind.Shell) == true)
            {
                continue;
            }

            swept.Add(new { handle = window.Handle.Value, process = window.ProcessName, title = window.Title });
            Log.Info($"rescue: uncloaked {window}");
        }

        return swept;
    }

    private static string? LiveProcessName(int pid)
    {
        try
        {
            return Process.GetProcessById(pid).ProcessName;
        }
        catch (ArgumentException)
        {
            return null; // no such process
        }
        catch (InvalidOperationException)
        {
            return null; // it exited between the two calls
        }
    }

    private static void KillProcess(int pid) => Process.GetProcessById(pid).Kill(entireProcessTree: true);
}
