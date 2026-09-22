using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using AkuWM.Core.Config;
using AkuWM.Core.Ipc;
using AkuWM.Core.Logging;
using AkuWM.Core.Model;
using AkuWM.Core.Platform;
using AkuWM.Core.State;

namespace AkuWM.Core.Commands;

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
    private readonly IPlatform? _platform;
    private readonly IReadOnlyList<Func<Check>> _extra;
    private readonly CloakLedger? _ledger;

    /// <param name="platform">
    /// When there is one, doctor also reports the desk itself: the monitors and
    /// whether every role was recognised by identity.
    /// </param>
    /// <param name="extra">
    /// Checks only the host can make -- the DPI awareness of this process, the
    /// shell's cloak -- passed in so this class stays free of Win32.
    /// </param>
    public DoctorCommand(
        ConfigPaths paths,
        Func<bool>? daemonRunning = null,
        IPlatform? platform = null,
        IReadOnlyList<Func<Check>>? extra = null,
        CloakLedger? ledger = null)
    {
        _paths = paths;
        _daemonRunning = daemonRunning ?? (() => new PipeClient().IsRunning());
        _platform = platform;
        _extra = extra ?? [];
        _ledger = ledger;
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
        CheckMonitors(checks);
        CheckCloakedWindows(checks);
        CheckSafetyNet(checks);

        foreach (Func<Check> check in _extra)
        {
            try
            {
                checks.Add(check());
            }
            catch (Exception ex)
            {
                checks.Add(new Check("a check failed", CheckStatus.Warn, ex.Message));
            }
        }

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
                : "not running: nothing is arranging the desk, and nothing of AkuWM's is hidden "
                  + "(the desk is on whatever else is running)"));
    }

    /// <summary>
    /// Every monitor role should be recognised by identity. A role assigned by
    /// position is a role a sleep cycle can move to the wrong screen.
    /// </summary>
    private void CheckMonitors(List<Check> checks)
    {
        if (_platform is null)
        {
            return;
        }

        IReadOnlyList<MonitorSnapshot> monitors = _platform.Monitors();
        List<MonitorConfig> configured = ConfigStore.Load(_paths).Effective.Monitors ?? [];
        Dictionary<MonitorHandle, string> roles = MonitorRoles.Resolve(configured, monitors);

        var unidentified = monitors
            .Where(m => !configured.Any(c => MonitorRoles.Matches(c, m)))
            .ToList();

        checks.Add(new("monitors", unidentified.Count > 0 ? CheckStatus.Warn : CheckStatus.Ok,
            string.Join("; ", monitors.Select(m =>
                $"{roles.GetValueOrDefault(m.Handle) ?? "no role"}={m.FriendlyName.Trim()} [{m.HardwareId}] {m.Bounds}"))));

        if (unidentified.Count > 0)
        {
            checks.Add(new("monitor identities", CheckStatus.Warn,
                $"{unidentified.Count} matched by position, not identity; run `akuwm monitors identify`"));
        }
    }

    /// <summary>
    /// Windows the shell has cloaked that AkuWM did not cloak.
    /// </summary>
    /// <remarks>
    /// Most of them are normal: a window on another native virtual desktop is
    /// cloaked by the shell, and that is the shell's business. The ones worth
    /// knowing about are the orphans -- hidden by a window manager that then
    /// forgot them, which is what happens when one is restarted while
    /// workspaces are hidden -- because an orphan is invisible in every way
    /// that matters: not on screen, not on the taskbar, not in Alt+Tab.
    /// <c>akuwm uncloak-all</c> tells the two apart by trying, since the shell
    /// refuses to uncloak the first kind and allows the second.
    /// </remarks>
    private void CheckCloakedWindows(List<Check> checks)
    {
        if (_platform is null)
        {
            return;
        }

        IReadOnlyList<WindowSnapshot> windows = _platform.Windows();
        List<WindowSnapshot> cloaked = windows
            .Where(w => w.Cloak.HasFlag(CloakKind.Shell))
            .ToList();
        checks.Add(new("windows", CheckStatus.Info,
            $"{windows.Count} on the desk, {cloaked.Count} cloaked by something other than AkuWM"));

        if (_ledger is { } ledger)
        {
            ledger.Reload();
            int ours = ledger.Entries.Count;
            checks.Add(new("cloak ledger", ours > 0 ? CheckStatus.Warn : CheckStatus.Ok,
                ours > 0
                    ? $"{ours} window(s) AkuWM hid and has not given back; they are recovered on the next start, " +
                      "or now with `akuwm uncloak-all`"
                    : "empty: AkuWM is not holding any window hidden"));
        }

        if (cloaked.Count > 0)
        {
            // Whether each one is on another virtual desktop or orphaned by a
            // dead manager cannot be read: IVirtualDesktopManager claims every
            // window is on the current desktop, including ones that are not
            // (measured, spike S6). Trying to uncloak is the only way to know,
            // and that is a command, not a check.
            checks.Add(new("cloaked elsewhere", CheckStatus.Info,
                $"{cloaked.Count} window(s) hidden by something else " +
                $"({string.Join(", ", cloaked.Take(6).Select(w => w.ProcessName).Distinct())}" +
                $"{(cloaked.Count > 6 ? ", ..." : string.Empty)}); most will be on other native virtual " +
                "desktops. `akuwm uncloak-all` reports which ones the shell lets go"));
        }
    }

    /// <summary>
    /// What is standing between a bad run and a desk the person cannot use.
    /// </summary>
    /// <remarks>
    /// Reported even when everything is fine, because the answer a person
    /// wants before letting a window manager rearrange their screen is not
    /// "no problems found" but "here is what happens when there is one".
    /// </remarks>
    private void CheckSafetyNet(List<Check> checks)
    {
        Session? last = new SessionMarker(_paths.SessionFile).Previous;

        if (last is null)
        {
            checks.Add(new("last run", CheckStatus.Info, "AkuWM has not run on this machine yet"));
        }
        else if (last.CleanExit)
        {
            checks.Add(new("last run", CheckStatus.Ok, "ended cleanly"));
        }
        else
        {
            bool safeMode = last.UncleanInARow + 1 >= SessionMarker.SafeModeAfter;
            checks.Add(new("last run", safeMode ? CheckStatus.Warn : CheckStatus.Info,
                $"did not shut down ({last.UncleanInARow + 1} in a row)"
                + (safeMode
                    ? ". The next start manages nothing unless it is `akuwm daemon --force`"
                    : string.Empty)));
        }

        int moved = new GeometryJournal(_paths.GeometryJournalFile).Entries.Count;
        checks.Add(new("geometry journal", moved > 0 ? CheckStatus.Info : CheckStatus.Ok,
            moved > 0
                ? $"{moved} window(s) AkuWM has moved and can put back where it found them"
                : "empty: no window is out of place because of AkuWM"));

        checks.Add(new("the way out", CheckStatus.Info,
            "`akuwm rescue` stops AkuWM and gives every window back. Nothing of AkuWM is in the "
            + "Startup folder, so restarting the machine comes up on the old stack"));
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
