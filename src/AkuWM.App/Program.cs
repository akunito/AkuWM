using AkuWM.Core.Commands;
using AkuWM.Core.Config;
using AkuWM.Core.Ipc;
using AkuWM.Core.Logging;
using AkuWM.Core.Platform;
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

        string line = CommandLine.Join(args);
        var client = new PipeClient();

        // A running daemon answers everything, so a query never sees a state
        // assembled by a second process that is not managing the desk.
        if (client.IsRunning())
        {
            return Print(client.Send(line), verb, args);
        }

        if (!CommandRouter.NeedsNoDaemon(verb))
        {
            Console.Error.WriteLine(
                $"AkuWM is not running, and '{verb}' needs it. Start it with `akuwm daemon`.");
            return 1;
        }

        Log.Console = false; // the command's own output is the interface here
        return Print(Router(paths).Execute(line), verb, args);
    }

    private static int Daemon(ConfigPaths paths, string[] args)
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

        var stopping = new ManualResetEventSlim(false);
        var server = new PipeServer(Router(paths));
        server.ExitRequested += () => stopping.Set();
        server.Start();

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stopping.Set();
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => stopping.Set();

        Log.Info("up, in shadow mode: AkuWM watches the desk and changes nothing.");
        stopping.Wait();

        Log.Info("stopping");
        server.DisposeAsync().AsTask().GetAwaiter().GetResult();
        Log.Close();
        return 0;
    }

    private static CommandRouter Router(ConfigPaths paths)
    {
        var windows = new WindowsPlatform();
        IPlatform platform = windows;
        var query = new QueryCommands(platform, paths);

        return new CommandRouter(
            new ConfigCommands(paths),
            new DoctorCommand(paths, () => new PipeClient().IsRunning(), platform, PlatformChecks(windows)),
            query,
            new ShadowCommand(query, Compat.GlazeWmProbe.Ask),
            new MonitorCommands(platform, paths),
            new UncloakCommand(platform, windows),
            new BenchCommand(platform, paths, windows));
    }

    /// <summary>The checks only the Windows host can make.</summary>
    private static List<Func<Check>> PlatformChecks(WindowsPlatform platform) =>
    [
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
    private static int Print(CommandResponse response, string verb, string[] args)
    {
        if (verb == "doctor" && response.Success && response.Data is not null)
        {
            Console.Write(DoctorText(response));
            return response.Data["ok"]?.GetValue<bool>() == true ? 0 : 1;
        }

        if (verb == "shadow" && args.Length > 1 && args[1] == "diff"
            && response.Success && response.Data?["text"] is { } text)
        {
            Console.Write(text.GetValue<string>());
            return response.Data["agrees"]?.GetValue<bool>() == true ? 0 : 1;
        }

        return Cli.Program.Print(response);
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
        Console.WriteLine("  daemon [--foreground]   run the window manager");
        Console.WriteLine("  spike <s1..s5>          M1: what Windows actually allows (see `akuwm spike`)");
        foreach (string help in CommandRouter.Help)
        {
            Console.WriteLine("  " + help);
        }
    }
}
