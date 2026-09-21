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

        // What the daemon actually does on a gesture. Everything above this
        // line was the platform layer and a full model rebuild -- which the
        // running window manager never does, because it observes one window
        // and recomputes. Measuring only that over-counted a rebuild that does
        // not happen and left out the decision, the placement and the JSON the
        // bar asks for on every event, twice over.
        var desk = new Desk.Desk(config);
        desk.SetMonitors(monitors);
        desk.Sync(windows);

        double compute = Time(rounds, () => desk.Compute());


        double observeOne = one is null
            ? 0
            : Time(rounds * 10, () =>
            {
                if (_platform.Window(one.Handle) is { } fresh)
                {
                    desk.Observe(fresh);
                }
            });

        // Moving real windows, which is the one thing here that is not AkuWM's
        // own work: SetWindowPos sends WM_WINDOWPOSCHANGING and WM_SIZE to the
        // application and waits for it to lay itself out again. Measured by
        // asking for the rectangles the windows are ALREADY in, so the desk
        // does not visibly jump while the bench runs -- Windows still does the
        // whole round trip for each one.
        double placeThem = 0;
        double placeRereading = 0;
        if (_actions is not null && windows.Count > 0)
        {
            var batch = new List<Placement>(windows.Count);
            foreach (WindowSnapshot window in windows)
            {
                batch.Add(new Placement(window.Handle, window.FrameBounds, window.BorderDelta));
            }

            placeThem = Time(Math.Min(rounds, 10), () => _actions.Place(batch));

            // The same work with the border left for the platform to look up,
            // which is what it did before the model started carrying it: a
            // GetWindowRect and a DWM round trip per window, inside the batch.
            // Both paths in one process, so the comparison is not two runs of
            // a busy machine.
            var reread = new List<Placement>(windows.Count);
            foreach (WindowSnapshot window in windows)
            {
                reread.Add(new Placement(window.Handle, window.FrameBounds));
            }

            placeRereading = Time(Math.Min(rounds, 10), () => _actions.Place(reread));
        }

        // The bar re-reads the whole desk on every event it is sent, and there
        // are two widgets, so this is paid twice per event.
        // The whole reply, not just the view: the envelope is where the data
        // used to be deep-cloned, so measuring the view alone missed the
        // largest thing on this path. What goes on the wire is what counts.
        double serialise = Time(rounds, () => Compat.GlazeProtocol.Reply(
            "query monitors",
            Compat.ExecResult.Ok(data: new System.Text.Json.Nodes.JsonObject
            {
                ["monitors"] = Compat.GlazeView.Monitors(desk),
            })).ToJsonString(Compat.GlazeProtocol.Compact));

        var results = new List<object>
        {
            Measurement("read one window", readOne, "an event becoming a fact"),
            Measurement("observe one window", observeOne, "the read, and the model taking it in"),
            Measurement("decide (Compute)", compute, $"{windows.Count} windows: what has to change"),
            Measurement("move every window (Windows does the work)", placeThem,
                $"{windows.Count} windows, already where they are asked to go"),
            Measurement("the same, asking Windows for each border", placeRereading,
                "what it cost before the model carried the border delta"),
            Measurement("serialise the desk for the bar", serialise,
                "what `query monitors` costs; the bar asks on EVERY event, per widget"),
            Measurement("build the model", buildModel,
                $"{windows.Count} windows, rules and all -- startup only, NOT on a gesture"),
            Measurement("record one cloak", ledgerWrite, "paid once per window hidden, inside the gesture"),
            Measurement("a workspace switch of 8", compute + (ledgerWrite * 16) + (serialise * 2),
                "decide once, record 16 cloaks, and answer the bar twice"),
            Measurement("enumerate every window", enumerateWindows,
                "every window appearing or disappearing, menus and tooltips included"),
            Measurement("enumerate the monitors", enumerateMonitors, "only after a display change"),
        };

        // The hot path as the daemon runs it: one window observed, the desk
        // decided. Not a model rebuild, which happens once at startup.
        double hotPath = observeOne + compute;

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

    /// <summary>
    /// Times one thing, warmed and with the worst run thrown away.
    /// </summary>
    /// <remarks>
    /// One warm-up pass was not enough. Measured on the desk: the same work,
    /// three runs, came out 0.205, 0.099 and 0.104 ms -- so anything under
    /// about a factor of two was the machine, not the code, and an
    /// optimisation could be "measured" either way depending on when it ran.
    /// Tiered JIT needs a few dozen calls before it settles, and a scheduler
    /// hiccup in a run of twenty moves the mean by half.
    ///
    /// So: warm properly, then take the best of three batches. The best is the
    /// run least interrupted by everything else on a desktop, which is the
    /// honest answer to "what does this cost" -- a mean over a busy machine
    /// measures the machine.
    /// </remarks>
    private static double Time(int rounds, Action work)
    {
        for (int i = 0; i < Math.Max(rounds, 50); i++)
        {
            work();
        }

        double best = double.MaxValue;

        for (int batch = 0; batch < 3; batch++)
        {
            var watch = Stopwatch.StartNew();
            for (int i = 0; i < rounds; i++)
            {
                work();
            }

            best = Math.Min(best, watch.Elapsed.TotalMilliseconds / rounds);
        }

        return best;
    }

    private static int Rounds(Dictionary<string, string?> options) =>
        options.TryGetValue("rounds", out string? value) && int.TryParse(value, out int parsed) && parsed > 0
            ? parsed
            : 20;
}
