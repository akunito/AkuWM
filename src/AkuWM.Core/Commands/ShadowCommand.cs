using AkuWM.Core.Ipc;
using AkuWM.Core.Model;

namespace AkuWM.Core.Commands;

/// <summary>
/// <c>akuwm shadow ...</c>: M1's own instrument.
/// </summary>
/// <remarks>
/// <c>shadow view</c> prints what AkuWM makes of the desk; <c>shadow diff</c>
/// puts that next to what the manager actually in charge reports, and is the
/// command the milestone is signed off with. Both go away once AkuWM is the
/// one in charge -- there will be nothing to compare against.
/// </remarks>
public sealed class ShadowCommand
{
    private readonly QueryCommands _query;
    private readonly Func<string, string?>? _askTheOtherManager;

    /// <param name="query">AkuWM's own view.</param>
    /// <param name="askTheOtherManager">
    /// Runs a query against the window manager currently in charge and returns
    /// its raw reply, or null when it cannot be reached. Injected so the
    /// comparison itself stays testable against recorded replies.
    /// </param>
    public ShadowCommand(QueryCommands query, Func<string, string?>? askTheOtherManager)
    {
        _query = query;
        _askTheOtherManager = askTheOtherManager;
    }

    public CommandResponse Execute(string line, string[] tokens)
    {
        if (tokens.Length < 2)
        {
            return CommandResponse.Fail(line, "shadow needs a verb: view, diff");
        }

        return tokens[1].ToLowerInvariant() switch
        {
            "view" => View(line),
            "diff" => Diff(line),
            "watch" => Watch(line, CommandLine.Options(tokens, 2)),
            _ => CommandResponse.Fail(line, $"'{tokens[1]}' is not view, diff or watch"),
        };
    }

    private CommandResponse View(string line)
    {
        ShadowView view = _query.View();

        return CommandResponse.Ok(line, new
        {
            monitors = view.Monitors.Select(m => new
            {
                role = view.Roles.GetValueOrDefault(m.Handle),
                name = m.FriendlyName,
                hardwareId = m.HardwareId,
                bounds = m.Bounds.ToString(),
                dpi = m.Dpi,
            }),
            managed = view.Managed.Count(),
            unmanaged = view.Windows.Count(w => !w.Managed),
            windows = view.Windows.Select(w => QueryCommands.Describe(w)).ToList(),
        });
    }

    /// <summary>
    /// Compares the two views over and over while the desk is being used, and
    /// reports everything it ever disagreed about.
    /// </summary>
    /// <remarks>
    /// One comparison only proves the desk as it is at that second. The
    /// milestone asks for an hour of normal use, because the interesting
    /// disagreements appear when windows are opened, moved between monitors,
    /// maximised and closed -- not while nothing is happening.
    /// </remarks>
    private CommandResponse Watch(string line, Dictionary<string, string?> options)
    {
        if (_askTheOtherManager is null)
        {
            return CommandResponse.Fail(line, "there is no other window manager to ask on this host");
        }

        int seconds = Number(options, "seconds", 60);
        int every = Math.Max(1, Number(options, "every", 5));

        var seen = new SortedSet<string>(StringComparer.Ordinal);
        var counts = new List<int>();
        int samples = 0;
        int agreed = 0;
        DateTime until = DateTime.UtcNow.AddSeconds(seconds);

        while (DateTime.UtcNow < until)
        {
            string? windowsJson = _askTheOtherManager("query windows");
            if (windowsJson is not null)
            {
                ShadowDiffResult result = ShadowDiff.Compare(
                    _query.View(), ShadowDiff.ParseGlazeWindows(windowsJson));

                samples++;
                counts.Add(result.Mine);
                if (result.Agrees)
                {
                    agreed++;
                }

                foreach (string window in result.OnlyMine)
                {
                    seen.Add("only AkuWM would manage: " + window);
                }

                foreach (string window in result.OnlyTheirs)
                {
                    seen.Add("AkuWM would leave alone:  " + window);
                }

                foreach (Disagreement d in result.Disagreements)
                {
                    seen.Add($"{d.What}: {d.Field} mine={d.Mine} theirs={d.Theirs}");
                }
            }

            Thread.Sleep(TimeSpan.FromSeconds(every));
        }

        return CommandResponse.Ok(line, new
        {
            seconds,
            every,
            samples,
            agreed,
            agreedAlways = samples > 0 && agreed == samples,
            windowsSeen = counts.Count == 0 ? 0 : counts.Max(),
            findings = seen.ToList(),
        });
    }

    private static int Number(Dictionary<string, string?> options, string name, int fallback) =>
        options.TryGetValue(name, out string? value) && int.TryParse(value, out int parsed)
            ? parsed
            : fallback;

    private CommandResponse Diff(string line)
    {
        if (_askTheOtherManager is null)
        {
            return CommandResponse.Fail(
                line, "there is no other window manager to ask on this host");
        }

        string? windowsJson = _askTheOtherManager("query windows");
        if (windowsJson is null)
        {
            return CommandResponse.Fail(
                line, "the window manager in charge did not answer `query windows`");
        }

        List<ExternalWindow> theirs = ShadowDiff.ParseGlazeWindows(windowsJson);
        ShadowDiffResult result = ShadowDiff.Compare(_query.View(), theirs);

        return CommandResponse.Ok(line, new
        {
            agrees = result.Agrees,
            mine = result.Mine,
            theirs = result.Theirs,
            onlyMine = result.OnlyMine,
            onlyTheirs = result.OnlyTheirs,
            disagreements = result.Disagreements.Select(d => new
            {
                handle = d.Handle,
                what = d.What,
                field = d.Field,
                mine = d.Mine,
                theirs = d.Theirs,
            }),
            text = ShadowDiff.Format(result),
        });
    }
}
