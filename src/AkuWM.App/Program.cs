using AkuWM.Core.Commands;
using AkuWM.Core.Config;
using AkuWM.Core.Ipc;
using AkuWM.Core.Logging;
using AkuWM.Core.Platform;
using AkuWM.Core.State;
using AkuWM.Core.Wm;
using AkuWM.Platform;

namespace AkuWM.App;

/// <summary>
/// <c>akuwm</c>: the window manager, and the client that talks to it.
/// </summary>
/// <remarks>
/// One executable, two behaviours. <c>akuwm daemon</c> is the manager itself;
/// <c>akuwm &lt;command&gt;</c> hands the line to a running daemon over the
/// pipe, or answers it here when the command needs no daemon.
/// </remarks>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Before anything asks Windows for a rectangle.
        WindowsPlatform.DeclareDpiAwareness();

        ConfigPaths paths = ConfigPaths.Discover();
        Log.Level = File.Exists(paths.DebugMarkerFile) ? LogLevel.Debug : LogLevel.Info;

        if (args.Length == 0)
        {
            Usage();
            return 2;
        }

        string verb = args[0].ToLowerInvariant();
        if (verb is "--help" or "-h" or "help")
        {
            Usage();
            return 0;
        }

        if (verb == "daemon")
        {
            return Daemon(paths, args);
        }

        // M1's instrumentation: five questions put to Windows directly, with
        // no daemon and no configuration in the way.
        if (verb == "spike")
        {
            return Spikes.Run(args);
        }

        // A uiAccess process is launched through AppInfo, and its standard
        // output cannot be redirected by the caller -- any script that tries
        // gets "the requested operation requires elevation" and an empty
        // string. So the program can write its own output to a file instead,
        // which is the only way a script can read what it said.
        string? outFile = OutFile(ref args);

        string line = CommandLine.Join(args);
        var client = new PipeClient();

        // A running daemon answers everything, so a query never sees a state
        // assembled by a second process that is not managing the desk. The one
        // exception is the command whose whole job is to deal with a daemon
        // that has stopped behaving.
        if (!CommandRouter.NeverDelegates(verb) && client.IsRunning())
        {
            return Print(client.Send(line), verb, args, outFile);
        }

        if (!CommandRouter.NeedsNoDaemon(verb))
        {
            Console.Error.WriteLine(
                $"AkuWM is not running, and '{verb}' needs it. Start it with `akuwm daemon`.");
            return 1;
        }

        Log.Console = false; // the command's own output is the interface here
        return Print(Router(paths).Execute(line), verb, args, outFile);
    }

    private static int Daemon(ConfigPaths paths, string[] args) =>
        DaemonAsync(paths, args).GetAwaiter().GetResult();

    private static async Task<int> DaemonAsync(ConfigPaths paths, string[] args)
    {
        Log.ToDirectory(paths.LogDir);
        Log.Console = args.Contains("--foreground");
        Log.Info(Build.Description + " starting");
        Log.Info($"config: {paths.ConfigDir} (profile {paths.Profile})");

        if (!WindowsPlatform.IsPerMonitorDpiAware())
        {
            // Without it every rectangle on the 150 % monitor is a lie, and a
            // layout computed from lies puts windows in the wrong place.
            Log.Warn("this process is not per-monitor DPI aware; rectangles will be wrong on scaled monitors");
        }

        LoadedConfig loaded;
        try
        {
            loaded = ConfigStore.Load(paths);
        }
        catch (ConfigException ex)
        {
            Log.Error($"the configuration could not be read: {ex.Message}");
            Console.Error.WriteLine(ex.Message);
            return 1;
        }

        if (!loaded.CommonExists)
        {
            string message =
                $"no configuration at {paths.CommonFile}. Run `akuwm config import glazewm` first.";
            Log.Error(message);
            Console.Error.WriteLine(message);
            return 1;
        }

        if (!loaded.Validation.Ok)
        {
            foreach (ValidationIssue issue in loaded.Validation.Errors)
            {
                Log.Error(issue.ToString());
                Console.Error.WriteLine(issue.ToString());
            }

            return 1;
        }

        foreach (ValidationIssue issue in loaded.Validation.Warnings)
        {
            Log.Warn(issue.ToString());
        }

        var session = new SessionMarker(paths.SessionFile);
        SessionVerdict verdict = session.Begin();
        bool force = args.Contains("--force");

        if (verdict.SafeMode && !force)
        {
            Log.Warn("safe mode: " + verdict.Reason);
            Console.Error.WriteLine("AkuWM is in safe mode. " + verdict.Reason);
        }

        var platform = new WindowsPlatform();
        var ledger = new CloakLedger(paths.CloakLedgerFile);
        var journal = new GeometryJournal(paths.GeometryJournalFile);

        // Before anything else touches a window: whatever went wrong in the
        // last run, the desk is whole again by the time AkuWM is listening.
        RecoveryResult recovery = ledger.Recover(platform, platform);
        GeometryRestoreResult restored = journal.Restore(platform, platform);
        if (recovery.Anything || restored.Anything)
        {
            Log.Info(
                $"recovery: {recovery.Recovered.Count} window(s) given back, " +
                $"{recovery.Failed.Count} refused, {restored.Restored.Count} put back where they were, " +
                $"{recovery.Stale.Count + restored.Stale.Count} stale entries dropped");
        }

        // The desk goes back however this process ends: on request, on Ctrl+C,
        // on an exception nobody caught, or because the loop stopped answering
        // and the watchdog gave up on it. Each path runs the same restore, and
        // the restore is safe to run twice.
        int restoring = 0;
        void GiveTheDeskBack(string why)
        {
            if (Interlocked.Exchange(ref restoring, 1) != 0)
            {
                return;
            }

            Log.Info($"giving the desk back ({why})");
            ledger.Recover(platform, platform);
            journal.Restore(platform, platform);
        }

        using var watchdog = new Watchdog(
            TimeSpan.FromSeconds(10),
            () =>
            {
                GiveTheDeskBack("the window-manager loop stopped answering");
                session.End(); // a stall the watchdog handled is not a crash to hold against the next run
                Environment.Exit(3);
            });

        // Managing unless something says otherwise: safe mode after two bad
        // endings, or --shadow, which watches and changes nothing.
        bool shadow = args.Contains("--shadow");
        bool manage = !shadow && (!verdict.SafeMode || force);

        var manager = new WindowManager(
            loaded.Effective, platform, ledger, journal, watchdog, manage, CompatPort(args));

        var stopping = new ManualResetEventSlim(false);
        var server = new PipeServer(Router(paths, manager));
        server.ExitRequested += () => stopping.Set();
        server.Start();

        manager.Start();
        watchdog.Start();

        // Proof rather than a promise: `akuwm daemon --stall-test 30` wedges
        // the loop on purpose, so the watchdog can be watched doing its job on
        // the real machine instead of only in a unit test.
        if (StallTest(args) is { } seconds)
        {
            Log.Warn($"--stall-test: wedging the window-manager loop for {seconds}s on purpose");
            _ = manager.Do<object?>("the deliberate stall", _ =>
            {
                Thread.Sleep(TimeSpan.FromSeconds(seconds));
                return null;
            });
        }

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stopping.Set();
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            GiveTheDeskBack("the process is exiting");
            stopping.Set();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception fatal)
            {
                Log.Error("unhandled exception; giving the desk back before dying", fatal);
            }
            else
            {
                Log.Error($"unhandled failure ({e.ExceptionObject}); giving the desk back before dying");
            }

            GiveTheDeskBack("an unhandled exception");
        };

        if (!manager.Compat.Listening)
        {
            Log.Warn(
                $"the compatibility server is not up ({manager.Compat.Unavailable}); "
                + "the bar and the scripts cannot reach AkuWM. Stop the old window manager first.");
        }

        Log.Info(
            manage
                ? "up, and managing the desk."
                : verdict.SafeMode && !force
                    ? "up, in safe mode: AkuWM manages nothing until it is started with --force."
                    : "up, watching: AkuWM changes nothing (--shadow).");
        stopping.Wait();

        Log.Info("stopping");

        // The window manager goes first. Restoring the desk while its loop is
        // still running means the loop puts everything back the way it wants
        // it, one pass after the restore.
        server.DisposeAsync().AsTask().GetAwaiter().GetResult();
        await manager.DisposeAsync();

        GiveTheDeskBack("stopping");
        session.End();
        platform.Dispose();
        Log.Close();
        return 0;
    }

    /// <summary>
    /// Which port the compatibility server listens on.
    /// </summary>
    /// <remarks>
    /// Overridable so AkuWM can be exercised against the real desk while the
    /// window manager it replaces still holds 6123 -- which is the only way to
    /// test the bar's side of it without switching the desk over first.
    /// </remarks>
    private static int CompatPort(string[] args)
    {
        int at = Array.FindIndex(args, a => a.Equals("--compat-port", StringComparison.OrdinalIgnoreCase));
        return at >= 0 && at + 1 < args.Length && int.TryParse(args[at + 1], out int port)
            ? port
            : AkuWM.Core.Compat.GlazeProtocol.Port;
    }

    /// <summary>The seconds asked for by <c>--stall-test</c>, or null.</summary>
    private static int? StallTest(string[] args)
    {
        int at = Array.FindIndex(args, a => a.Equals("--stall-test", StringComparison.OrdinalIgnoreCase));
        return at >= 0 && at + 1 < args.Length && int.TryParse(args[at + 1], out int seconds)
            ? seconds
            : null;
    }

    private static CommandRouter Router(ConfigPaths paths, WindowManager? manager = null)
    {
        var windows = new WindowsPlatform();
        IPlatform platform = windows;
        var query = new QueryCommands(platform, paths);
        var ledger = new CloakLedger(paths.CloakLedgerFile);

        return new CommandRouter(
            new ConfigCommands(paths),
            new DoctorCommand(
                paths, () => new PipeClient().IsRunning(), platform, PlatformChecks(windows, manager), ledger),
            query,
            new ShadowCommand(query, Compat.GlazeWmProbe.Ask),
            new MonitorCommands(platform, paths),
            new UncloakCommand(platform, windows, ledger),
            new BenchCommand(platform, paths, windows),
            new RescueCommand(paths, platform, windows),
            manager is null ? null : new CompatCommand(manager.Envelope));
    }

    /// <summary>The checks only the Windows host can make.</summary>
    /// <param name="manager">
    /// Null for a second process answering `doctor` on its own: it can report
    /// what Windows allows, but not what the running window manager is doing.
    /// </param>
    private static List<Func<Check>> PlatformChecks(WindowsPlatform platform, WindowManager? manager = null) =>
    [
        () => manager is null
            ? new Check("window manager", CheckStatus.Info, "not running in this process")
            : new Check(
                "window manager",
                manager.Managing ? CheckStatus.Ok : CheckStatus.Warn,
                manager.Managing
                    ? $"arranging the desk ({manager.Redraws} redraw(s), last {manager.Last})"
                    : "watching only: it changes nothing"),
        () => manager is null
            ? new Check("hiding windows", CheckStatus.Info, "not running in this process")
            : new Check(
                "hiding windows",
                manager.CanHide ? CheckStatus.Ok : CheckStatus.Fail,
                manager.CanHide
                    ? "a hidden window comes back, proven on the first hide of this run"
                    : "a hidden window does NOT come back on this machine, so nothing is hidden "
                      + "and every workspace shows all of its windows"),
        () => manager is null
            ? new Check("bar and scripts", CheckStatus.Info, "not running in this process")
            : new Check(
                "bar and scripts",
                manager.Compat.Listening ? CheckStatus.Ok : CheckStatus.Warn,
                manager.Compat.Listening
                    ? $"listening on 127.0.0.1:{AkuWM.Core.Compat.GlazeProtocol.Port}, "
                      + $"{manager.Compat.Connections} client(s) connected"
                    : manager.Compat.Unavailable
                      ?? "not listening: the bar and the scripts cannot reach AkuWM"),
        () =>
        {
            bool granted = Win32Token.HasUiAccess();
            bool asked = Win32Token.ManifestRequestsUiAccess();

            return new Check(
                "uiAccess",
                granted ? CheckStatus.Ok : CheckStatus.Warn,
                granted
                    ? "granted: chords work while a game has the foreground"
                    : asked
                        ? "asked for but NOT granted — the binary is signed and in a secure " +
                          $"directory, or it is not. Run tools/install-uiaccess.ps1 as administrator " +
                          $"(running from {AppContext.BaseDirectory})"
                        : "this build never asked for it: it was published with -p:UiAccess=false, " +
                          "which is the development manifest. Chords over a game will not work.");
        },
        () => new Check(
            "elevation",
            Win32Token.IsElevated() ? CheckStatus.Warn : CheckStatus.Ok,
            Win32Token.IsElevated()
                ? "running elevated, which AkuWM does not need and should not have"
                : "running as the user, which is right"),
        () => new Check(
            "dpi awareness",
            WindowsPlatform.IsPerMonitorDpiAware() ? CheckStatus.Ok : CheckStatus.Fail,
            WindowsPlatform.IsPerMonitorDpiAware()
                ? "per-monitor v2"
                : "NOT per-monitor: every rectangle on a scaled monitor will be wrong"),
        () => new Check(
            "virtual desktops",
            platform.Desktops.Available ? CheckStatus.Ok : CheckStatus.Warn,
            platform.Desktops.Available
                ? $"IVirtualDesktopManager answering ({platform.Desktops.Failures} failed calls)"
                : platform.Desktops.Unavailable
                  ?? "not available: a window on another desktop cannot be told from one AkuWM hid"),
        () =>
        {
            using var shell = new ImmersiveShell();
            return new Check(
                "shell cloak",
                shell.Available ? CheckStatus.Ok : CheckStatus.Fail,
                shell.Available
                    ? "available: windows can be hidden and shown"
                    : shell.Unavailable ?? "not available");
        },
    ];

    /// <summary>
    /// Prints a reply. <c>doctor</c> and <c>shadow diff</c> get their readable
    /// form, everything else the JSON a script can read.
    /// </summary>
    /// <summary>
    /// Pulls <c>--out &lt;path&gt;</c> out of the arguments before the command
    /// sees them.
    /// </summary>
    private static string? OutFile(ref string[] args)
    {
        int at = Array.FindIndex(args, a => a.Equals("--out", StringComparison.OrdinalIgnoreCase));
        if (at < 0 || at + 1 >= args.Length)
        {
            return null;
        }

        string path = args[at + 1];
        args = [.. args[..at], .. args[(at + 2)..]];
        return path;
    }

    private static int Print(CommandResponse response, string verb, string[] args, string? outFile)
    {
        string text = Render(response, verb, args, out int code);

        Console.Write(text);
        if (outFile is not null)
        {
            try
            {
                File.WriteAllText(outFile, text);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"could not write {outFile}: {ex.Message}");
            }
        }

        return code;
    }

    private static string Render(CommandResponse response, string verb, string[] args, out int code)
    {
        if (verb == "doctor" && response.Success && response.Data is not null)
        {
            code = response.Data["ok"]?.GetValue<bool>() == true ? 0 : 1;
            return DoctorText(response);
        }

        if (verb == "shadow" && args.Length > 1 && args[1] == "diff"
            && response.Success && response.Data?["text"] is { } diff)
        {
            code = response.Data["agrees"]?.GetValue<bool>() == true ? 0 : 1;
            return diff.GetValue<string>();
        }

        if (!response.Success)
        {
            code = 1;
            return (response.Error ?? "failed") + Environment.NewLine;
        }

        code = 0;
        return response.Data is null
            ? string.Empty
            : response.Data.ToJsonString(Protocol.Pretty) + Environment.NewLine;
    }

    private static string DoctorText(CommandResponse response)
    {
        var checks = new List<Check>();
        foreach (System.Text.Json.Nodes.JsonNode? node in response.Data?["checks"]?.AsArray() ?? [])
        {
            if (node is null)
            {
                continue;
            }

            string status = node["status"]?.GetValue<string>() ?? "info";
            checks.Add(new Check(
                node["name"]?.GetValue<string>() ?? "?",
                Enum.TryParse(status, ignoreCase: true, out CheckStatus parsed) ? parsed : CheckStatus.Info,
                node["detail"]?.GetValue<string>() ?? string.Empty));
        }

        return DoctorCommand.Format(checks);
    }

    private static void Usage()
    {
        Console.WriteLine(Build.Description);
        Console.WriteLine();
        Console.WriteLine("usage: akuwm <command>");
        Console.WriteLine();
        Console.WriteLine("  daemon [--foreground] [--force] [--shadow] [--compat-port <n>]");
        Console.WriteLine("                          run the window manager");
        Console.WriteLine("  spike <s1..s9>          what Windows actually allows (see `akuwm spike`)");
        foreach (string help in CommandRouter.Help)
        {
            Console.WriteLine("  " + help);
        }
    }
}
