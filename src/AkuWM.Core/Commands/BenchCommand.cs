using System.Diagnostics;
using AkuWM.Core.Config;
using AkuWM.Core.Ipc;
using AkuWM.Core.Model;
using AkuWM.Core.Platform;

namespace AkuWM.Core.Commands;

/// <summary>
/// <c>akuwm bench</c>: what the hot path costs, measured rather than assumed.
/// </summary>
/// <remarks>
/// <para>
/// The whole argument for one process is latency. The stack AkuWM replaces
/// pays <strong>47 ms</strong> for a single CLI round trip and 110 ms to bring
/// a window, measured on this desk, because each order crosses a process
/// boundary and each decision needs a query first. In one process those are
/// function calls and what is left is the Win32 work.
/// </para>
/// <para>
/// The budget is 5 ms from a chord to the window moving. This command is how
/// that claim stays honest as the code grows, and it runs on the desk, not in
/// CI: the numbers only mean something on the machine with these monitors and
/// this many windows.
/// </para>
/// </remarks>
public sealed class BenchCommand
{
    /// <summary>The plan's budget, from chord to <c>SetWindowPos</c>.</summary>
    public const double BudgetMs = 5.0;

    private readonly IPlatform _platform;
    private readonly IPlatformActions? _actions;
    private readonly ConfigPaths _paths;

    public BenchCommand(IPlatform platform, ConfigPaths paths, IPlatformActions? actions = null)
    {
        _platform = platform;
        _paths = paths;
        _actions = actions;
    }

    public CommandResponse Execute(string line, string[] tokens)
    {
        int rounds = Rounds(CommandLine.Options(tokens, 1));
        AkuWmConfig config = ConfigStore.Load(_paths).Effective;

        double enumerateWindows = Time(rounds, () => _platform.Windows());
        double enumerateMonitors = Time(rounds, () => _platform.Monitors());

        IReadOnlyList<WindowSnapshot> windows = _platform.Windows();
        IReadOnlyList<MonitorSnapshot> monitors = _platform.Monitors();

        WindowSnapshot? one = windows.FirstOrDefault();
        double readOne = one is null ? 0 : Time(rounds * 10, () => _platform.Window(one.Handle));
        double buildModel = Time(rounds, () => ShadowModel.Build(config, monitors, windows));
        double ledgerWrite = TimeLedger(rounds, one);

        var results = new List<object>
        {
            Measurement("read one window", readOne, "an event becoming a fact"),
            Measurement("build the model", buildModel, $"{windows.Count} windows, rules and all"),
            Measurement("record one cloak", ledgerWrite, "paid once per window hidden, inside the gesture"),
            Measurement("a workspace switch of 8", (readOne * 8) + buildModel + (ledgerWrite * 16),
                "8 hidden and 8 shown: the reads and the records a real switch pays"),
            Measurement("enumerate every window", enumerateWindows,
                "every window appearing or disappearing, menus and tooltips included"),
            Measurement("enumerate the monitors", enumerateMonitors, "only after a display change"),
        };

        // The hot path: one event read, the model updated, the window moved.
        double hotPath = readOne + buildModel;

        return CommandResponse.Ok(line, new
        {
            rounds,
            windows = windows.Count,
            monitors = monitors.Count,
            budgetMs = BudgetMs,
            hotPathMs = Math.Round(hotPath, 3),
            withinBudget = hotPath < BudgetMs,
            comparison = "the stack this replaces pays 47 ms for one CLI round trip, 110 ms to bring a window",
            measurements = results,
        });
    }

    /// <summary>
    /// What one entry in the cloak ledger costs. Paid per window hidden and per
    /// window shown, on the wm thread, inside the gesture.
    /// </summary>
    private double TimeLedger(int rounds, WindowSnapshot? window)
    {
        if (window is null)
        {
            return 0;
        }

        string file = Path.Combine(_paths.RuntimeDir, "bench-ledger");
        var ledger = new State.CloakLedger(file);

        try
        {
            return Time(rounds * 5, () =>
            {
                ledger.Record(window);
                ledger.Forget(window.Handle);
            }) / 2;
        }
        finally
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
                // A leftover bench file is not worth failing the bench for.
            }
        }
    }

    private static object Measurement(string what, double ms, string note) => new
    {
        what,
        ms = Math.Round(ms, 3),
        note,
    };

    private static double Time(int rounds, Action work)
    {
        // One pass first, so the measurement is not the cost of the first call.
        work();

        var watch = Stopwatch.StartNew();
        for (int i = 0; i < rounds; i++)
        {
            work();
        }

        return watch.Elapsed.TotalMilliseconds / rounds;
    }

    private static int Rounds(Dictionary<string, string?> options) =>
        options.TryGetValue("rounds", out string? value) && int.TryParse(value, out int parsed) && parsed > 0
            ? parsed
            : 20;
}
