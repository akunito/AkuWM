using System.Text.Json.Nodes;
using AkuWM.Core.Config;
using AkuWM.Core.Desk;
using AkuWM.Core.Ipc;
using AkuWM.Core.Matching;
using AkuWM.Core.Model;

namespace AkuWM.Core.Commands;

/// <summary>
/// <c>akuwm rules ...</c>: what the rules are, what they catch, and what they miss.
/// </summary>
/// <remarks>
/// <para>
/// Built for the GUI in M5, which has to do three things this answers: point
/// at a window and edit the rules that apply to it, pick a process off a list
/// and write a rule for it, and show which of the configured rules are doing
/// anything at all.
/// </para>
/// <para>
/// Read-only. Writing a rule is <c>config set</c>, which validates the whole
/// file; this exists so the GUI does not have to guess what to write.
/// </para>
/// </remarks>
public sealed class RulesCommand
{
    private readonly Func<Func<Desk.Desk, CommandResponse>, CommandResponse> _onTheWmThread;

    /// <param name="onTheWmThread">
    /// Runs a read against the model where the model lives. Reading it from
    /// the pipe's thread would see a workspace half switched -- the same
    /// reason every compat query is marshalled.
    /// </param>
    public RulesCommand(Func<Func<Desk.Desk, CommandResponse>, CommandResponse> onTheWmThread) =>
        _onTheWmThread = onTheWmThread;

    public CommandResponse Execute(string line, string[] tokens)
    {
        if (tokens.Length < 2)
        {
            return CommandResponse.Fail(line, "rules needs a subject: list, for, processes");
        }

        Dictionary<string, string?> options = CommandLine.Options(tokens, 2);

        return tokens[1].ToLowerInvariant() switch
        {
            "list" => List(line),
            "for" => For(line, options),
            "processes" => Processes(line),
            _ => CommandResponse.Fail(line, $"'{tokens[1]}' is not list, for or processes"),
        };
    }

    /// <summary>Every configured rule, and which windows it is catching now.</summary>
    private CommandResponse List(string line)
    {
        return _onTheWmThread(desk =>
        {
            var rules = new JsonArray();

            foreach (RuleConfig rule in desk.Config.Rules ?? [])
            {
                if (rule.Id is not { Length: > 0 } id)
                {
                    continue;
                }

                var caught = new JsonArray();
                foreach (DeskWindow window in desk.Windows)
                {
                    if (window.Rules.Contains(id))
                    {
                        caught.Add((JsonNode)Identify(window));
                    }
                }

                rules.Add((JsonNode)new JsonObject
                {
                    ["id"] = id,
                    ["enabled"] = rule.Enabled != false,
                    ["actions"] = new JsonArray([.. (rule.Actions ?? []).Select(a => (JsonNode)a!)]),
                    ["match"] = Criteria(rule.Match),
                    ["catching"] = caught,

                    // A rule that catches nothing is either waiting for an app that
                    // is not running or quietly misspelled, and the GUI should be
                    // able to tell the person which of its rules are idle.
                    ["idle"] = caught.Count == 0,
                });
            }

            return CommandResponse.Ok(line, new JsonObject { ["rules"] = rules });
        });
    }

    /// <summary>
    /// One window: what it is, which rules caught it, and what a rule for it
    /// would have to say.
    /// </summary>
    /// <remarks>
    /// The answer to "click a button, pick a window, edit its rules". The
    /// picking is the GUI's; this turns a window into the words a rule is
    /// written in.
    /// </remarks>
    private CommandResponse For(string line, Dictionary<string, string?> options)
    {
        return _onTheWmThread(desk =>
        {
            DeskWindow? window = Find(desk, options);

            if (window is null)
            {
                return CommandResponse.Fail(
                    line, "rules for needs --id <container>, --handle <hwnd> or --focused");
            }

            JsonObject identity = Identify(window);

            identity["matchedBy"] = new JsonArray([.. window.Rules.Select(r => (JsonNode)r)]);
            identity["decidedByHand"] = window.DecidedByHand;
            identity["managed"] = window.Managed;
            identity["reason"] = window.Reason.ToString();

            // What to write, in the file's own words, narrowest first: a person
            // editing this almost always means "this app", and sometimes "this
            // window of this app".
            identity["suggested"] = new JsonArray(
                (JsonNode)new JsonObject
                {
                    ["what"] = "every window of this process",
                    ["match"] = new JsonObject { ["process"] = window.Snapshot.ProcessName },
                },
                (JsonNode)new JsonObject
                {
                    ["what"] = "this kind of window of this process",
                    ["match"] = new JsonObject
                    {
                        ["process"] = window.Snapshot.ProcessName,
                        ["class"] = window.Snapshot.ClassName,
                    },
                },
                (JsonNode)new JsonObject
                {
                    ["what"] = "windows of this process with this title",
                    ["match"] = new JsonObject
                    {
                        ["process"] = window.Snapshot.ProcessName,
                        ["title"] = "re:" + Regex(window.Snapshot.Title),
                    },
                });

            return CommandResponse.Ok(line, identity);
        });
    }

    /// <summary>
    /// Every process with a window on the desk, and whether any rule covers it.
    /// </summary>
    /// <remarks>
    /// The list the GUI offers when the person wants a rule for something that
    /// has none. Grouped by process, because that is the unit a rule is almost
    /// always written against.
    /// </remarks>
    private CommandResponse Processes(string line)
    {
        return _onTheWmThread(desk =>
        {
            var byProcess = new Dictionary<string, List<DeskWindow>>(StringComparer.OrdinalIgnoreCase);

            foreach (DeskWindow window in desk.Windows)
            {
                string name = window.Snapshot.ProcessName;
                if (!byProcess.TryGetValue(name, out List<DeskWindow>? windows))
                {
                    byProcess[name] = windows = [];
                }

                windows.Add(window);
            }

            var processes = new JsonArray();
            foreach ((string name, List<DeskWindow> windows) in byProcess.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
            {
                var rules = new JsonArray();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                var classes = new JsonArray();
                var classesSeen = new HashSet<string>(StringComparer.Ordinal);

                foreach (DeskWindow window in windows)
                {
                    foreach (string rule in window.Rules)
                    {
                        if (seen.Add(rule))
                        {
                            rules.Add((JsonNode)rule);
                        }
                    }

                    if (classesSeen.Add(window.Snapshot.ClassName))
                    {
                        classes.Add((JsonNode)window.Snapshot.ClassName);
                    }
                }

                processes.Add((JsonNode)new JsonObject
                {
                    ["process"] = name,
                    ["windows"] = windows.Count,
                    ["managed"] = windows.Count(w => w.Managed),
                    ["classes"] = classes,
                    ["matchedBy"] = rules,
                    ["hasRule"] = rules.Count > 0,
                });
            }

            return CommandResponse.Ok(line, new JsonObject { ["processes"] = processes });
        });
    }

    private static DeskWindow? Find(Desk.Desk desk, Dictionary<string, string?> options)
    {
        if (options.GetValueOrDefault("focused") is not null || options.ContainsKey("focused"))
        {
            return desk.Window(desk.Focused);
        }

        if (options.GetValueOrDefault("handle") is { Length: > 0 } text
            && long.TryParse(text, out long handle))
        {
            return desk.Window(new WindowHandle(handle));
        }

        if (options.GetValueOrDefault("id") is { Length: > 0 } value && Guid.TryParse(value, out Guid id))
        {
            foreach (DeskWindow window in desk.Windows)
            {
                if (window.Id == id)
                {
                    return window;
                }
            }
        }

        return null;
    }

    private static JsonObject Identify(DeskWindow window) => new()
    {
        ["id"] = window.Id.ToString(),
        ["handle"] = window.Handle.Value,
        ["process"] = window.Snapshot.ProcessName,
        ["class"] = window.Snapshot.ClassName,
        ["title"] = window.Snapshot.Title,
        ["state"] = window.State.ToString().ToLowerInvariant(),
        ["workspace"] = window.Workspace,
    };

    private static JsonArray Criteria(IReadOnlyList<MatchCriteria>? match)
    {
        var all = new JsonArray();
        foreach (MatchCriteria one in match ?? [])
        {
            var fields = new JsonObject();
            if (one.Process is { Length: > 0 } process)
            {
                fields["process"] = process;
            }

            if (one.Class is { Length: > 0 } className)
            {
                fields["class"] = className;
            }

            if (one.Title is { Length: > 0 } title)
            {
                fields["title"] = title;
            }

            all.Add((JsonNode)fields);
        }

        return all;
    }

    /// <summary>
    /// A title turned into a pattern that matches it and nothing surprising.
    /// </summary>
    /// <remarks>
    /// Titles carry brackets, dots and vertical bars -- "(2) Discord | #general"
    /// -- and a person clicking "make a rule for this" does not expect to have
    /// written a regular expression by accident.
    /// </remarks>
    private static string Regex(string title) =>
        System.Text.RegularExpressions.Regex.Escape(title);
}
