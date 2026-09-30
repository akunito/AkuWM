using System.Text.Json;
using System.Text.Json.Nodes;
using AkuWM.Core.Config;

namespace AkuWM.Core.Nodes;

public sealed class Container
{
    public string Node { get; init; } = string.Empty;
    public string Daemon { get; init; } = string.Empty;
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Image { get; init; } = string.Empty;
    public string State { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public string Created { get; init; } = string.Empty;
    public string Ports { get; init; } = string.Empty;
    public string Project { get; init; } = string.Empty;
    public string Service { get; init; } = string.Empty;
    public string WorkingDir { get; init; } = string.Empty;
    public string ConfigFiles { get; init; } = string.Empty;
    public string RestartPolicy { get; set; } = string.Empty;
    public List<Mount> Mounts { get; set; } = [];
    public long MemLimit { get; set; }
    public double CpuLimit { get; set; }
    public string CpuPct { get; set; } = string.Empty;
    public string MemUsage { get; set; } = string.Empty;
    public string MemPct { get; set; } = string.Empty;
    public string NetIo { get; set; } = string.Empty;
    public string BlockIo { get; set; } = string.Empty;
    public string Pids { get; set; } = string.Empty;
    public string Health { get; set; } = string.Empty;
}

public sealed record Mount(string Type, string Source, string Destination, string Mode, string Rw);

public sealed record DfRow(string Type, string TotalCount, string Active, string Size, string Reclaimable);

public sealed record Volume(string Name, string Size, string Links, string Mountpoint);

public sealed record DiskUsage(List<DfRow> Summary, List<Volume> Volumes, string? Error);

/// <summary>
/// Docker on a node with the plain CLI over <see cref="NodeShell"/>: every
/// query is <c>docker ... --format '{{json .}}'</c> parsed here. The port of
/// sway-apps' dockerctl; command lines identical so its tests apply.
/// </summary>
public static class Docker
{
    public const string RootlessPrefix = "env DOCKER_HOST=\"unix:///run/user/$(id -u)/docker.sock\" docker";
    public const string RootfulPrefix = "env DOCKER_HOST=unix:///var/run/docker.sock docker";

    /// <summary>
    /// Both sockets named explicitly: on hosts with rootless docker NixOS
    /// exports DOCKER_HOST for the rootless daemon system-wide, so a bare
    /// <c>docker</c> would silently talk to it when the rootful one is meant
    /// (seen on NAS_PROD).
    /// </summary>
    public static string Prefix(NodeConfig node, string daemon) =>
        daemon == "rootless" ? RootlessPrefix : node.SudoRootful == true ? "sudo -n " + RootfulPrefix : RootfulPrefix;

    public static string Run(NodeConfig node, string daemon, string args, int timeout = 60) =>
        NodeShell.Run(node, $"{Prefix(node, daemon)} {args}", timeout).Out;

    public static Dictionary<string, string> Labels(string? labels)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string part in (labels ?? string.Empty).Split(','))
        {
            int eq = part.IndexOf('=');
            if (eq > 0)
            {
                map[part[..eq]] = part[(eq + 1)..];
            }
            else if (eq == 0)
            {
                continue;
            }
        }

        return map;
    }

    private static string S(JsonNode? node, string key) => node?[key]?.ToString() ?? string.Empty;

    /// <summary><c>docker ps -a</c>, plus <c>inspect</c> for mounts, limits and compose, plus <c>stats</c> for usage.</summary>
    public static List<Container> Containers(NodeConfig node, string daemon, bool withStats = true, bool withInspect = true)
    {
        string ps = Run(node, daemon, "ps -a --no-trunc --format '{{json .}}'");
        var items = new List<Container>();
        foreach (string line in ps.Split('\n'))
        {
            if (line.Trim().Length == 0)
            {
                continue;
            }

            JsonNode? d = JsonNode.Parse(line);
            Dictionary<string, string> labels = Labels(S(d, "Labels"));
            string id = S(d, "ID");
            items.Add(new Container
            {
                Node = node.Id ?? string.Empty,
                Daemon = daemon,
                Id = id.Length > 12 ? id[..12] : id,
                Name = S(d, "Names"),
                Image = S(d, "Image"),
                State = S(d, "State"),
                Status = S(d, "Status"),
                Created = S(d, "CreatedAt"),
                Ports = S(d, "Ports"),
                Project = labels.GetValueOrDefault("com.docker.compose.project", string.Empty),
                Service = labels.GetValueOrDefault("com.docker.compose.service", string.Empty),
                WorkingDir = labels.GetValueOrDefault("com.docker.compose.project.working_dir", string.Empty),
                ConfigFiles = labels.GetValueOrDefault("com.docker.compose.project.config_files", string.Empty),
            });
        }

        if (items.Count == 0)
        {
            return items;
        }

        string names = string.Join(' ', items.Select(c => NodeShell.Quote(c.Name)));
        if (withInspect)
        {
            try
            {
                if (JsonNode.Parse(Run(node, daemon, $"inspect {names}", 90)) is JsonArray inspected)
                {
                    foreach (JsonNode? ins in inspected)
                    {
                        string name = S(ins, "Name").TrimStart('/');
                        Container? c = items.Find(x => x.Name == name);
                        if (c is null)
                        {
                            continue;
                        }

                        JsonNode? hc = ins?["HostConfig"];
                        c.MemLimit = hc?["Memory"]?.GetValue<long>() ?? 0;
                        long nano = hc?["NanoCpus"]?.GetValue<long>() ?? 0;
                        double quota = hc?["CpuQuota"]?.GetValue<double>() ?? 0;
                        double period = hc?["CpuPeriod"]?.GetValue<double>() ?? 100000;
                        c.CpuLimit = nano != 0 ? nano / 1e9 : quota != 0 ? quota / (period == 0 ? 100000 : period) : 0.0;
                        c.RestartPolicy = S(hc?["RestartPolicy"], "Name");
                        c.Health = S(ins?["State"]?["Health"], "Status");
                        var mounts = new List<Mount>();
                        if (ins?["Mounts"] is JsonArray ms)
                        {
                            foreach (JsonNode? m in ms)
                            {
                                string source = S(m, "Source");
                                if (source.Length == 0)
                                {
                                    source = S(m, "Name");
                                }

                                bool rw = m?["RW"]?.GetValue<bool>() ?? true;
                                mounts.Add(new Mount(S(m, "Type"), source, S(m, "Destination"), S(m, "Mode"), rw ? "rw" : "ro"));
                            }
                        }

                        c.Mounts = mounts;
                    }
                }
            }
            catch (Exception ex) when (ex is NodeException or JsonException)
            {
                Logging.Log.Warn($"inspect failed on {node.Id}/{daemon}: {ex.Message}");
            }
        }

        if (withStats)
        {
            try
            {
                foreach (string line in Run(node, daemon, "stats --no-stream --format '{{json .}}'", 60).Split('\n'))
                {
                    if (line.Trim().Length == 0)
                    {
                        continue;
                    }

                    JsonNode? st = JsonNode.Parse(line);
                    Container? c = items.Find(x => x.Name == S(st, "Name"));
                    if (c is null)
                    {
                        continue;
                    }

                    c.CpuPct = S(st, "CPUPerc");
                    c.MemUsage = S(st, "MemUsage");
                    c.MemPct = S(st, "MemPerc");
                    c.NetIo = S(st, "NetIO");
                    c.BlockIo = S(st, "BlockIO");
                    c.Pids = S(st, "PIDs");
                }
            }
            catch (Exception ex) when (ex is NodeException or JsonException)
            {
                Logging.Log.Warn($"stats failed on {node.Id}/{daemon}: {ex.Message}");
            }
        }

        return items;
    }

    /// <summary><c>docker system df</c> and the volumes with sizes.</summary>
    public static DiskUsage Usage(NodeConfig node, string daemon)
    {
        var summary = new List<DfRow>();
        var volumes = new List<Volume>();
        string? error = null;
        try
        {
            foreach (string line in Run(node, daemon, "system df --format '{{json .}}'", 120).Split('\n'))
            {
                if (line.Trim().Length > 0)
                {
                    JsonNode? d = JsonNode.Parse(line);
                    summary.Add(new DfRow(S(d, "Type"), S(d, "TotalCount"), S(d, "Active"), S(d, "Size"), S(d, "Reclaimable")));
                }
            }
        }
        catch (Exception ex) when (ex is NodeException or JsonException)
        {
            error = ex.Message;
        }

        try
        {
            JsonNode? data = JsonNode.Parse(Run(node, daemon, "system df -v --format json", 180));
            if (data?["Volumes"] is JsonArray vs)
            {
                foreach (JsonNode? v in vs)
                {
                    volumes.Add(new Volume(S(v, "Name"), S(v, "Size"), S(v, "Links"), S(v, "Mountpoint")));
                }
            }
        }
        catch (Exception ex) when (ex is NodeException or JsonException)
        {
            // older docker: fall back to the table without sizes
            try
            {
                foreach (string line in Run(node, daemon, "volume ls --format '{{json .}}'", 60).Split('\n'))
                {
                    if (line.Trim().Length > 0)
                    {
                        volumes.Add(new Volume(S(JsonNode.Parse(line), "Name"), "?", string.Empty, string.Empty));
                    }
                }
            }
            catch (Exception inner) when (inner is NodeException or JsonException)
            {
            }
        }

        return new DiskUsage(summary, volumes, error);
    }

    public static string Logs(NodeConfig node, string daemon, string name, int tail = 300) =>
        NodeShell.Run(node, $"{Prefix(node, daemon)} logs --tail {tail} --timestamps {NodeShell.Quote(name)} 2>&1", 60, check: false).Out;

    /// <summary>The compose prefix for a container born of a compose project, or null.</summary>
    public static string? Compose(NodeConfig node, string daemon, Container c)
    {
        if (c.ConfigFiles.Length == 0)
        {
            return null;
        }

        string files = string.Join(' ', c.ConfigFiles.Split(',').Select(f => "-f " + NodeShell.Quote(f)));
        string project = c.Project.Length > 0 ? $"-p {NodeShell.Quote(c.Project)} " : string.Empty;
        string cd = c.WorkingDir.Length > 0 ? $"cd {NodeShell.Quote(c.WorkingDir)} && " : string.Empty;
        return $"{cd}{Prefix(node, daemon)} compose {project}{files}";
    }

    public static readonly string[] Actions = ["start", "stop", "restart", "pull", "recreate", "up", "down"];

    /// <summary>start | stop | restart | pull | recreate | up | down, compose-aware. The command's output tail.</summary>
    public static string Action(NodeConfig node, string daemon, Container c, string what)
    {
        string prefix = Prefix(node, daemon);
        string? compose = Compose(node, daemon, c);
        string service = c.Service.Length > 0 ? NodeShell.Quote(c.Service) : string.Empty;
        string name = NodeShell.Quote(c.Name);
        string command = what switch
        {
            "start" or "stop" or "restart" => $"{prefix} {what} {name}",
            "pull" => compose is not null ? $"{compose} pull {service} && {compose} up -d {service}" : $"{prefix} pull {NodeShell.Quote(c.Image)}",
            "recreate" => compose is not null ? $"{compose} up -d --force-recreate {service}" : throw new NodeException($"{c.Name}: not a compose service; recreate needs compose"),
            "up" => compose is not null ? $"{compose} up -d" : throw new NodeException($"{c.Name}: not a compose service"),
            "down" => compose is not null ? $"{compose} down" : throw new NodeException($"{c.Name}: not a compose service"),
            _ => throw new NodeException($"unknown action '{what}'"),
        };
        ShellRun run = NodeShell.Run(node, command, 600, check: false);
        string output = (run.Out + run.Err).Trim();
        if (run.Code != 0)
        {
            throw new NodeException($"{what} {c.Name} failed: {(output.Length > 500 ? output[^500..] : output)}");
        }

        return output.Length > 2000 ? output[^2000..] : output;
    }
}
