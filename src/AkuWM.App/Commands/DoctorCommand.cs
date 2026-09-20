using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using AkuWM.Cli;
using AkuWM.Core.Config;
using AkuWM.Core.Ipc;
using AkuWM.Core.Logging;

namespace AkuWM.App.Commands;

public enum CheckStatus
{
    Ok,
    Info,
    Warn,
    Fail,
}

/// <param name="Name">What is being checked, three or four words.</param>
/// <param name="Status">Whether it is a problem.</param>
/// <param name="Detail">The value that was found; enough to act on.</param>
public readonly record struct Check(string Name, CheckStatus Status, string Detail);

/// <summary>
/// <c>akuwm doctor</c>: everything that has to be true for the desk to behave,
/// checked in one place and answered in one screen.
/// </summary>
/// <remarks>
/// The list grows with the milestones. M0 can check the process, the
/// configuration, the pipe and what else is running; the hooks, the cloak
/// read-back, the monitor roles and Zebar's subscription arrive with the code
/// that owns them (M1-M2). A check that cannot be made from where the process
/// runs says so instead of passing quietly -- from WSL, for instance, the
/// Windows processes are not visible at all.
/// </remarks>
public sealed class DoctorCommand
{
    private readonly ConfigPaths _paths;
    private readonly Func<bool> _daemonRunning;

    public DoctorCommand(ConfigPaths paths, Func<bool>? daemonRunning = null)
    {
        _paths = paths;
        _daemonRunning = daemonRunning ?? (() => new PipeClient().IsRunning());
    }

    public CommandResponse Execute(string line)
    {
        List<Check> checks = Run();
        int failures = checks.Count(c => c.Status == CheckStatus.Fail);

        return CommandResponse.Ok(line, new
        {
            ok = failures == 0,
            failures,
            warnings = checks.Count(c => c.Status == CheckStatus.Warn),
            checks = checks.Select(c => new
            {
                name = c.Name,
                status = c.Status.ToString().ToLowerInvariant(),
                detail = c.Detail,
            }),
        });
    }

    public List<Check> Run()
    {
        var checks = new List<Check>
        {
            new("akuwm build", CheckStatus.Info, Build.Description),
            new("runtime", CheckStatus.Info,
                $"{RuntimeInformation.FrameworkDescription} on {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})"),
        };

        CheckConfig(checks);
        CheckRuntimeDir(checks);
        CheckPipe(checks);
        CheckOtherProcesses(checks);
        CheckIpcPort(checks);

        return checks;
    }

    private void CheckConfig(List<Check> checks)
    {
        checks.Add(new("config directory", Directory.Exists(_paths.ConfigDir) ? CheckStatus.Ok : CheckStatus.Fail,
            _paths.ConfigDir));

        if (!File.Exists(_paths.CommonFile))
        {
            checks.Add(new("common.json", CheckStatus.Fail,
                $"not found at {_paths.CommonFile}; run `akuwm config import glazewm`"));
            return;
        }

        LoadedConfig loaded;
        try
        {
            loaded = ConfigStore.Load(_paths);
        }
        catch (ConfigException ex)
        {
            checks.Add(new("common.json", CheckStatus.Fail, ex.Message));
            return;
        }

        checks.Add(new("common.json", CheckStatus.Ok,
            $"{loaded.Effective.Rules?.Count ?? 0} rules, {loaded.Effective.Workspaces?.Count ?? 0} workspaces, " +
            $"{loaded.Effective.Shortcuts?.Count ?? 0} shortcuts"));

        checks.Add(new($"{_paths.Profile}.json",
            loaded.ProfileExists ? CheckStatus.Ok : CheckStatus.Info,
            loaded.ProfileExists ? _paths.ProfileFile : "no machine layer (everything comes from common.json)"));

        int errors = loaded.Validation.Errors.Count();
        int warnings = loaded.Validation.Warnings.Count();
        checks.Add(new("configuration valid",
            errors > 0 ? CheckStatus.Fail : warnings > 0 ? CheckStatus.Warn : CheckStatus.Ok,
            errors > 0
                ? string.Join("; ", loaded.Validation.Errors.Select(e => e.ToString()))
                : warnings > 0
                    ? string.Join("; ", loaded.Validation.Warnings.Take(4).Select(e => e.ToString()))
                    : "no errors, no warnings"));
    }

    private void CheckRuntimeDir(List<Check> checks)
    {
        try
        {
            Directory.CreateDirectory(_paths.RuntimeDir);
            string probe = Path.Combine(_paths.RuntimeDir, ".write-probe");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            checks.Add(new("runtime directory", CheckStatus.Ok, _paths.RuntimeDir));
        }
        catch (Exception ex)
        {
            checks.Add(new("runtime directory", CheckStatus.Fail, $"{_paths.RuntimeDir}: {ex.Message}"));
        }

        checks.Add(new("log file", Log.FileName is null ? CheckStatus.Info : CheckStatus.Ok,
            Log.FileName ?? "not writing to a file in this process"));
    }

    private void CheckPipe(List<Check> checks)
    {
        bool running = _daemonRunning();
        checks.Add(new("akuwm daemon", running ? CheckStatus.Ok : CheckStatus.Info,
            running
                ? $"answering on the {Protocol.PipeName} pipe"
                : "not running (M0 has no window manager to run yet)"));
    }

    private static void CheckOtherProcesses(List<Check> checks)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            checks.Add(new("windows processes", CheckStatus.Info,
                "not visible from here: this doctor is running under WSL, not on the Windows session"));
            return;
        }

        foreach ((string process, string what) in new[]
        {
            ("glazewm", "GlazeWM"),
            ("AutoHotkey64_UIA", "AutoHotkey (hyper-desktops.ahk)"),
            ("zebar", "Zebar"),
        })
        {
            Process[] found = Process.GetProcessesByName(process);
            checks.Add(new($"{what} running", CheckStatus.Info,
                found.Length > 0
                    ? $"yes, pid {string.Join(", ", found.Select(p => p.Id))}"
                    : "no"));
            foreach (Process p in found)
            {
                p.Dispose();
            }
        }
    }

    private static void CheckIpcPort(List<Check> checks)
    {
        const int port = 6123;
        try
        {
            bool listening = IPGlobalProperties.GetIPGlobalProperties()
                .GetActiveTcpListeners()
                .Any(endpoint => endpoint.Port == port);

            checks.Add(new($"port {port}", CheckStatus.Info,
                listening
                    ? "something is listening (GlazeWM today, AkuWM from M2)"
                    : "nothing is listening here"));
        }
        catch (Exception ex)
        {
            checks.Add(new($"port {port}", CheckStatus.Info, $"could not be read: {ex.Message}"));
        }
    }

    /// <summary>The human-readable form the CLI prints.</summary>
    public static string Format(IEnumerable<Check> checks)
    {
        var text = new System.Text.StringBuilder();
        foreach (Check check in checks)
        {
            string mark = check.Status switch
            {
                CheckStatus.Ok => "ok  ",
                CheckStatus.Warn => "warn",
                CheckStatus.Fail => "FAIL",
                _ => "    ",
            };
            text.AppendLine($"{mark}  {check.Name,-22} {check.Detail}");
        }

        return text.ToString();
    }
}
