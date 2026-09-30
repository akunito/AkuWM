using System.Text.Json.Nodes;
using AkuWM.Core.Config;
using AkuWM.Core.Git;
using AkuWM.Core.Ipc;
using AkuWM.Core.Nodes;
using AkuWM.Core.Profiles;

namespace AkuWM.Core.Commands;

/// <summary>
/// <c>akuwm profiles|git|nodes|docker|monitor ...</c>: the sway-apps CLI for
/// the M6 sections, JSON out. None needs the daemon: they read files and
/// talk to the nodes.
/// </summary>
public sealed class OpsCommands
{
    private readonly ConfigPaths _paths;
    private readonly Func<long> _now;

    public OpsCommands(ConfigPaths paths, Func<long>? now = null)
    {
        _paths = paths;
        _now = now ?? (() => DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    }

    public static readonly string[] Help =
    [
        "profiles list | diff <a> <b> [--section s] | copy <from> <to> --section s [--ids a,b] [--replace] | snapshot list|create [reason]|restore <id> [--sections s,t] [--files f.json]",
        "git status | commit [-m msg] | push | pull | sync [--no-push]",
        "nodes list | probe <id>",
        "docker ps <node/daemon> [--fast] | df <node/daemon> | logs <node/daemon> <name> [--tail n] | start|stop|restart|pull|recreate|up|down <node/daemon> <name>",
        "monitor dashboard | query <promql>",
    ];

    public CommandResponse Execute(string line, string[] tokens)
    {
        Dictionary<string, string?> options = CommandLine.Options(tokens, 1);
        string[] args = tokens.Skip(1).Where(t => !t.StartsWith("--", StringComparison.Ordinal) && !options.ContainsValue(t)).ToArray();
        try
        {
            return tokens[0].ToLowerInvariant() switch
            {
                "profiles" => ProfilesCmd(line, args, options),
                "git" => GitCmd(line, args, options),
                "nodes" => NodesCmd(line, args),
                "docker" => DockerCmd(line, args, options),
                "monitor" => MonitorCmd(line, args),
                _ => CommandResponse.Fail(line, $"'{tokens[0]}' is not profiles, git, nodes, docker or monitor"),
            };
        }
        catch (Exception ex) when (ex is NodeException or InvalidOperationException or IOException or ArgumentException or ConfigException or FileNotFoundException)
        {
            return CommandResponse.Fail(line, ex.Message);
        }
    }

    private static JsonObject Obj(SectionDiff d) => new()
    {
        ["only_a"] = new JsonArray([.. d.OnlyA.Select(i => (JsonNode)i.DeepClone())]),
        ["only_b"] = new JsonArray([.. d.OnlyB.Select(i => (JsonNode)i.DeepClone())]),
        ["changed"] = new JsonArray([.. d.Changed.Select(c => (JsonNode)new JsonObject { ["a"] = c.A.DeepClone(), ["b"] = c.B.DeepClone() })]),
        ["same"] = new JsonArray([.. d.Same.Select(i => (JsonNode)i.DeepClone())]),
    };

    private CommandResponse ProfilesCmd(string line, string[] args, Dictionary<string, string?> options)
    {
        var profiles = new Profiles.Profiles(_paths, () => DateTimeOffset.FromUnixTimeSeconds(_now()));
        string verb = args.Length > 0 ? args[0] : "list";
        switch (verb)
        {
            case "list":
                {
                    var rows = new JsonArray();
                    foreach ((string name, _) in profiles.Layers())
                    {
                        JsonObject layer = profiles.Layer(name);
                        var row = new JsonObject { ["profile"] = name, ["current"] = name == _paths.Profile };
                        foreach (string section in Profiles.Profiles.Sections)
                        {
                            row[section] = Profiles.Profiles.Items(layer, section).Count;
                        }

                        rows.Add((JsonNode)row);
                    }

                    return CommandResponse.Ok(line, rows);
                }

            case "diff":
                {
                    if (args.Length < 3)
                    {
                        return CommandResponse.Fail(line, "profiles diff <a> <b> [--section s]");
                    }

                    if (options.GetValueOrDefault("section") is { } section)
                    {
                        return CommandResponse.Ok(line, Obj(profiles.Diff(args[1], args[2], section)));
                    }

                    var summary = new JsonObject();
                    foreach ((string s, (int a, int b, int c, int d)) in profiles.Summary(args[1], args[2]))
                    {
                        summary[s] = new JsonObject { ["only_a"] = a, ["only_b"] = b, ["changed"] = c, ["same"] = d };
                    }

                    return CommandResponse.Ok(line, summary);
                }

            case "copy":
                {
                    if (args.Length < 3 || options.GetValueOrDefault("section") is not { } section)
                    {
                        return CommandResponse.Fail(line, "profiles copy <from> <to> --section s [--ids a,b] [--replace]");
                    }

                    if (args[1] == args[2])
                    {
                        return CommandResponse.Fail(line, "source and destination are the same layer");
                    }

                    string[]? ids = options.GetValueOrDefault("ids")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    CopyResult r = profiles.CopyItems(args[1], args[2], section, ids, options.ContainsKey("replace"));
                    return CommandResponse.Ok(line, new { copied = r.Copied, r.Section, from = r.From, to = r.To, snapshot = r.Snapshot, destinationTotal = r.DestinationTotal });
                }

            case "snapshot":
                {
                    string what = args.Length > 1 ? args[1] : "list";
                    switch (what)
                    {
                        case "list":
                            return CommandResponse.Ok(line, profiles.Snapshots().Select(s => new { s.Id, s.Reason, s.Profile, s.Time, s.Files }).ToList());
                        case "create":
                            return CommandResponse.Ok(line, new { snapshot = profiles.Snapshot(args.Length > 2 ? string.Join(' ', args[2..]) : "manual") });
                        case "restore":
                            {
                                if (args.Length < 3)
                                {
                                    return CommandResponse.Fail(line, "profiles snapshot restore <id> [--sections s,t] [--files f.json,g.json]");
                                }

                                string[]? sections = options.GetValueOrDefault("sections")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                                string[]? files = options.GetValueOrDefault("files")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                                return CommandResponse.Ok(line, new { restored = profiles.Restore(args[2], files, sections) });
                            }

                        case "diff":
                            {
                                if (args.Length < 3)
                                {
                                    return CommandResponse.Fail(line, "profiles snapshot diff <id>");
                                }

                                var per = new JsonObject();
                                foreach ((string file, var sections) in profiles.DiffSnapshot(args[2]))
                                {
                                    var o = new JsonObject();
                                    foreach ((string s, (int a, int b, int c)) in sections)
                                    {
                                        o[s] = new JsonObject { ["only_snapshot"] = a, ["only_now"] = b, ["changed"] = c };
                                    }

                                    per[file] = o;
                                }

                                return CommandResponse.Ok(line, per);
                            }

                        default:
                            return CommandResponse.Fail(line, $"'{what}' is not list, create, restore or diff");
                    }
                }

            default:
                return CommandResponse.Fail(line, $"'{verb}' is not list, diff, copy or snapshot");
        }
    }

    private CommandResponse GitCmd(string line, string[] args, Dictionary<string, string?> options)
    {
        var git = new GitSync(_paths);
        string verb = args.Length > 0 ? args[0] : "status";
        switch (verb)
        {
            case "status":
                {
                    GitStatus s = git.Status();
                    return CommandResponse.Ok(line, new { repo = s.Repo, top = git.Top, s.Branch, s.Dirty, s.Ahead, s.Behind, s.Upstream });
                }

            case "commit":
                {
                    string message = options.GetValueOrDefault("m") ?? options.GetValueOrDefault("message") ?? (args.Length > 1 ? string.Join(' ', args[1..]) : "edited from the CLI");
                    return CommandResponse.Ok(line, new { sha = git.Commit(git.StateFiles(), message) });
                }

            case "push":
                return CommandResponse.Ok(line, new { output = git.Push() });
            case "pull":
                return CommandResponse.Ok(line, new { output = git.Pull() });
            case "sync":
                {
                    SyncResult r = git.Sync(pushAfter: !options.ContainsKey("no-push"));
                    return CommandResponse.Ok(line, new { ok = r.Ok, r.Behind, r.Ahead, r.Merged, r.Pushed, r.Error, r.Skipped });
                }

            default:
                return CommandResponse.Fail(line, $"'{verb}' is not status, commit, push, pull or sync");
        }
    }

    private List<NodeConfig> Nodes() => (ConfigStore.Load(_paths).Effective.Nodes ?? []).OrderBy(n => n.Order ?? 100).ToList();

    private NodeConfig Node(string id) =>
        Nodes().FirstOrDefault(n => string.Equals(n.Id, id, StringComparison.OrdinalIgnoreCase)) ?? throw new NodeException($"no node '{id}' (see: akuwm nodes list)");

    private CommandResponse NodesCmd(string line, string[] args)
    {
        string verb = args.Length > 0 ? args[0] : "list";
        switch (verb)
        {
            case "list":
                return CommandResponse.Ok(line, Nodes().Select(n => new { n.Id, n.Name, n.Profile, n.Ssh, n.Daemons, n.SudoRootful, n.PrometheusInstance, n.Order, enabled = n.Enabled != false, local = NodeShell.IsLocal(n) }).ToList());
            case "probe":
                {
                    if (args.Length < 2)
                    {
                        return CommandResponse.Fail(line, "nodes probe <id>");
                    }

                    NodeConfig node = Node(args[1]);
                    (bool ok, string detail) = NodeShell.Reachable(node);
                    return CommandResponse.Ok(line, new { node = node.Id, ok, detail });
                }

            default:
                return CommandResponse.Fail(line, $"'{verb}' is not list or probe");
        }
    }

    private (NodeConfig Node, string Daemon) Target(string spec)
    {
        string[] parts = spec.Split('/');
        NodeConfig node = Node(parts[0]);
        string daemon = parts.Length > 1 ? parts[1] : (node.Daemons ?? []).FirstOrDefault() ?? throw new NodeException($"{node.Id} has no docker daemon");
        return !ConfigDefaults.DockerDaemons.Contains(daemon) ? throw new NodeException($"'{daemon}' is not rootful or rootless") : (node, daemon);
    }

    private CommandResponse DockerCmd(string line, string[] args, Dictionary<string, string?> options)
    {
        if (args.Length < 2)
        {
            return CommandResponse.Fail(line, "docker ps|df|logs|start|stop|restart|pull|recreate|up|down <node/daemon> [name]");
        }

        (NodeConfig node, string daemon) = Target(args[1]);
        switch (args[0])
        {
            case "ps":
                {
                    bool fast = options.ContainsKey("fast");
                    return CommandResponse.Ok(line, Docker.Containers(node, daemon, withStats: !fast, withInspect: !fast));
                }

            case "df":
                return CommandResponse.Ok(line, Docker.Usage(node, daemon));
            case "logs":
                {
                    if (args.Length < 3)
                    {
                        return CommandResponse.Fail(line, "docker logs <node/daemon> <name> [--tail n]");
                    }

                    int tail = int.TryParse(options.GetValueOrDefault("tail"), out int t) ? t : 300;
                    return CommandResponse.Ok(line, new { name = args[2], logs = Docker.Logs(node, daemon, args[2], tail) });
                }

            default:
                {
                    if (Array.IndexOf(Docker.Actions, args[0]) < 0)
                    {
                        return CommandResponse.Fail(line, $"'{args[0]}' is not ps, df, logs or one of {string.Join(", ", Docker.Actions)}");
                    }

                    if (args.Length < 3)
                    {
                        return CommandResponse.Fail(line, $"docker {args[0]} <node/daemon> <name>");
                    }

                    Container c = Docker.Containers(node, daemon, withStats: false, withInspect: false).FirstOrDefault(x => x.Name == args[2])
                                  ?? throw new NodeException($"no container '{args[2]}' on {node.Id}/{daemon}");
                    return CommandResponse.Ok(line, new { action = args[0], container = c.Name, output = Docker.Action(node, daemon, c, args[0]) });
                }
        }
    }

    private CommandResponse MonitorCmd(string line, string[] args)
    {
        AkuWmConfig config = ConfigStore.Load(_paths).Effective;
        string verb = args.Length > 0 ? args[0] : "dashboard";
        switch (verb)
        {
            case "dashboard":
                return CommandResponse.Ok(line, Prometheus.Build(config, _now()));
            case "query":
                {
                    if (args.Length < 2)
                    {
                        return CommandResponse.Fail(line, "monitor query <promql>");
                    }

                    List<JsonArray> results = Prometheus.Batch(config, [new QuerySpec(string.Join(' ', args[1..]))], _now());
                    return CommandResponse.Ok(line, results[0]);
                }

            default:
                return CommandResponse.Fail(line, $"'{verb}' is not dashboard or query");
        }
    }
}
