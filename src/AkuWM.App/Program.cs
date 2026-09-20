using AkuWM.App.Commands;
using AkuWM.App.Ipc;
using AkuWM.Cli;
using AkuWM.Core.Config;
using AkuWM.Core.Ipc;
using AkuWM.Core.Logging;

namespace AkuWM.App;

/// <summary>
/// <c>akuwm</c>: the window manager, and the client that talks to it.
/// </summary>
/// <remarks>
/// One executable, two behaviours. <c>akuwm daemon</c> is the manager itself;
/// <c>akuwm &lt;command&gt;</c> hands the line to a running daemon over the
/// pipe, or answers it here when the command needs no window manager -- which
/// is how <c>config import glazewm</c> and <c>doctor</c> work from WSL, where
/// there is no session to manage.
/// </remarks>
public static class Program
{
    public static int Main(string[] args)
    {
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

        string line = CommandLine.Join(args);
        var client = new PipeClient();

        // A running daemon answers everything, so a query never sees a state
        // assembled by a second process that is not managing the desk.
        if (client.IsRunning())
        {
            return Print(client.Send(line), verb);
        }

        if (!CommandRouter.NeedsNoDaemon(verb))
        {
            Console.Error.WriteLine(
                $"AkuWM is not running, and '{verb}' needs it. Start it with `akuwm daemon`.");
            return 1;
        }

        Log.Console = false; // the command's own output is the interface here
        return Print(Router(paths).Execute(line), verb);
    }

    private static int Daemon(ConfigPaths paths, string[] args)
    {
        Log.ToDirectory(paths.LogDir);
        Log.Console = args.Contains("--foreground");
        Log.Info(Build.Description + " starting");
        Log.Info($"config: {paths.ConfigDir} (profile {paths.Profile})");

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

        Log.Info("up. M0 manages no windows yet: the pipe, the configuration and doctor are the whole of it.");
        stopping.Wait();

        Log.Info("stopping");
        server.DisposeAsync().AsTask().GetAwaiter().GetResult();
        Log.Close();
        return 0;
    }

    private static CommandRouter Router(ConfigPaths paths) =>
        new(new ConfigCommands(paths), new DoctorCommand(paths));

    /// <summary>
    /// Prints a reply. <c>doctor</c> gets the readable table, everything else
    /// the JSON a script can read -- the same rule the GlazeWM CLI followed, so
    /// the suites keep working when the shim lands.
    /// </summary>
    private static int Print(CommandResponse response, string verb)
    {
        if (verb == "doctor" && response.Success && response.Data is not null)
        {
            Console.Write(DoctorText(response));
            return response.Data["ok"]?.GetValue<bool>() == true ? 0 : 1;
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
        foreach (string help in CommandRouter.Help)
        {
            Console.WriteLine("  " + help);
        }
    }
}
