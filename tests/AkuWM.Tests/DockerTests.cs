using System.Text.Json.Nodes;
using AkuWM.Core.Config;
using AkuWM.Core.Nodes;
using Xunit;

namespace AkuWM.Tests;

/// <summary>sway-apps' dockerctl tests, ported: command lines and parsing, no ssh ever opened.</summary>
[Collection("NodeShell")] // both replace the static launcher: never in parallel
public class DockerTests : IDisposable
{
    private const string Rootless = "env DOCKER_HOST=\"unix:///run/user/$(id -u)/docker.sock\" docker";
    private const string Rootful = "env DOCKER_HOST=unix:///var/run/docker.sock docker";

    private static readonly NodeConfig Vps = new() { Id = "VPS_PROD", Ssh = "akunito@100.64.0.6:56777", Daemons = ["rootless"] };
    private static readonly NodeConfig Nas = new() { Id = "NAS_PROD", Ssh = "akunito@192.168.20.200", Daemons = ["rootful", "rootless"], SudoRootful = true };
    private static readonly NodeConfig Local = new() { Id = "DESK", Daemons = ["rootful"] };

    private readonly Func<NodeConfig, string, int, ShellRun> _before = NodeShell.Launcher;
    private readonly List<string> _calls = [];

    public void Dispose() => NodeShell.Launcher = _before;

    /// <summary>Match the remote command line against (substring, reply) pairs in order; record every line.</summary>
    private void Answers(params (string Needle, object Reply)[] responses)
    {
        NodeShell.Launcher = (node, remote, timeout) =>
        {
            _calls.Add(remote);
            foreach ((string needle, object reply) in responses)
            {
                if (remote.Contains(needle, StringComparison.Ordinal))
                {
                    return reply switch
                    {
                        ShellRun run => run,
                        Exception ex => throw ex,
                        _ => new ShellRun(0, reply.ToString()!, string.Empty),
                    };
                }
            }

            throw new InvalidOperationException("unexpected command: " + remote);
        };
    }

    private static string Lines(params JsonObject[] objects) => string.Join('\n', objects.Select(o => o.ToJsonString())) + "\n";

    private static readonly string PsLines = Lines(
        new JsonObject
        {
            ["ID"] = "abcdef1234567890abcdef",
            ["Names"] = "immich_server",
            ["Image"] = "ghcr.io/immich-app/immich-server:v3.1.0",
            ["State"] = "running",
            ["Status"] = "Up 3 days (healthy)",
            ["CreatedAt"] = "2026-09-01 10:00:00 +0000 UTC",
            ["Ports"] = "0.0.0.0:2283->2283/tcp",
            ["Labels"] = "com.docker.compose.project=immich,com.docker.compose.service=immich-server,com.docker.compose.project.working_dir=/home/a/.homelab/immich,com.docker.compose.project.config_files=/home/a/.homelab/immich/docker-compose.yml,org.opencontainers.image.url=https://x/y?a=b",
        },
        new JsonObject
        {
            ["ID"] = "1111111111112222",
            ["Names"] = "immich_postgres",
            ["Image"] = "tensorchord/pgvecto-rs:pg14",
            ["State"] = "running",
            ["Status"] = "Up 3 days",
            ["CreatedAt"] = "",
            ["Ports"] = "5432/tcp",
            ["Labels"] = "com.docker.compose.project=immich,com.docker.compose.service=database,com.docker.compose.project.working_dir=/home/a/.homelab/immich,com.docker.compose.project.config_files=/home/a/.homelab/immich/docker-compose.yml",
        },
        new JsonObject { ["ID"] = "333333333333", ["Names"] = "portainer", ["Image"] = "portainer/portainer-ce", ["State"] = "exited", ["Status"] = "Exited (0) 2 hours ago", ["CreatedAt"] = "", ["Ports"] = "", ["Labels"] = "" },
        new JsonObject
        {
            ["ID"] = "444444444444",
            ["Names"] = "plane-api",
            ["Image"] = "makeplane/plane-backend",
            ["State"] = "running",
            ["Status"] = "Up 1 hour",
            ["CreatedAt"] = "",
            ["Ports"] = "",
            ["Labels"] = "com.docker.compose.project=plane,com.docker.compose.service=api,com.docker.compose.project.working_dir=/home/a/.homelab/plane,com.docker.compose.project.config_files=/home/a/.homelab/plane/a.yml,/home/a/.homelab/plane/b.yml",
        });

    private const string Inspect = """
        [ { "Name": "/immich_server", "HostConfig": { "Memory": 2147483648, "NanoCpus": 1500000000, "RestartPolicy": { "Name": "unless-stopped" } },
            "State": { "Health": { "Status": "healthy" } },
            "Mounts": [ { "Type": "bind", "Source": "/srv/photos", "Destination": "/usr/src/app/upload", "Mode": "rw", "RW": true },
                        { "Type": "volume", "Name": "model-cache", "Destination": "/cache", "Mode": "", "RW": false } ] },
          { "Name": "/immich_postgres", "HostConfig": { "Memory": 0, "NanoCpus": 0, "CpuQuota": 50000, "CpuPeriod": 100000, "RestartPolicy": { "Name": "always" } }, "State": {}, "Mounts": [] },
          { "Name": "/portainer", "HostConfig": {}, "State": {}, "Mounts": [] },
          { "Name": "/plane-api", "HostConfig": { "RestartPolicy": {} }, "State": {}, "Mounts": [] },
          { "Name": "/ghost_not_in_ps", "HostConfig": { "Memory": 1 }, "State": {}, "Mounts": [] } ]
        """;

    private static readonly string StatsLines = Lines(
        new JsonObject { ["Name"] = "immich_server", ["CPUPerc"] = "12.34%", ["MemUsage"] = "512MiB / 2GiB", ["MemPerc"] = "25.00%", ["NetIO"] = "1.2kB / 3.4kB", ["BlockIO"] = "0B / 8kB", ["PIDs"] = "42" },
        new JsonObject { ["Name"] = "immich_postgres", ["CPUPerc"] = "0.50%", ["MemUsage"] = "100MiB / 31GiB", ["MemPerc"] = "0.31%", ["NetIO"] = "0B / 0B", ["BlockIO"] = "0B / 0B", ["PIDs"] = "7" });

    [Fact]
    public void Prefixes_and_ssh_targets()
    {
        Assert.Equal(Rootless, Docker.Prefix(Vps, "rootless"));
        Assert.Equal(Rootful, Docker.Prefix(Local, "rootful"));
        Assert.Equal("sudo -n " + Rootful, Docker.Prefix(Nas, "rootful"));
        Assert.Equal(["-p", "56777", "akunito@100.64.0.6"], NodeShell.SshTarget(Vps));
        Assert.Equal(["akunito@192.168.20.200"], NodeShell.SshTarget(Nas));
        Assert.Equal(["sh", "-c", "echo hi"], NodeShell.Argv(Local, "echo hi"));
        string[] remote = NodeShell.Argv(Vps, "echo hi");
        Assert.Equal(["ssh", "-A"], remote[..2]);
        Assert.Equal(["-p", "56777", "akunito@100.64.0.6", "echo hi"], remote[^4..]);
        Assert.Contains("BatchMode=yes", remote);
    }

    [Fact]
    public void Run_failures_become_node_errors_and_reachable_answers_both_ways()
    {
        Answers(("boom", new ShellRun(1, "", "ssh: connect to host x port 22: Connection refused\n")));
        NodeException ex = Assert.Throws<NodeException>(() => NodeShell.Run(Vps, "boom"));
        Assert.Contains("VPS_PROD: unreachable (ssh: connect to host x port 22: Connection refused)", ex.Message);
        Answers(("boom", new ShellRun(2, "", "something else\n")));
        Assert.Contains("VPS_PROD: something else", Assert.Throws<NodeException>(() => NodeShell.Run(Vps, "boom")).Message);
        Answers(("boom", new ShellRun(124, "", "")));
        Assert.Contains("timeout after 60s", Assert.Throws<NodeException>(() => NodeShell.Run(Vps, "boom")).Message);
        Answers(("boom", new ShellRun(2, "", "something else\n")));
        Assert.Equal(2, NodeShell.Run(Vps, "boom", check: false).Code);

        Answers(("uname", "ok\nvps-prod\nup 3 days\n"));
        Assert.Equal((true, "vps-prod · up 3 days"), NodeShell.Reachable(Vps));
        Answers(("uname", "ok\n"));
        Assert.Equal((true, "ok"), NodeShell.Reachable(Vps));
        Answers(("uname", new ShellRun(255, "", "ssh: No route to host\n")));
        (bool ok, string detail) = NodeShell.Reachable(Nas);
        Assert.False(ok);
        Assert.Contains("unreachable", detail);
    }

    [Fact]
    public void Labels_parsing_with_equals_in_a_value()
    {
        var lab = Docker.Labels("a=1,com.docker.compose.project=immich,b=x=y,url=https://h/p?q=1&r=2,novalue,,c=");
        Assert.Equal(new Dictionary<string, string> { ["a"] = "1", ["com.docker.compose.project"] = "immich", ["b"] = "x=y", ["url"] = "https://h/p?q=1&r=2", ["c"] = "" }, lab);
        Assert.Empty(Docker.Labels(string.Empty));
        Assert.Empty(Docker.Labels(null));
    }

    [Fact]
    public void Containers_parse_ps_inspect_and_stats()
    {
        Answers(("ps -a", PsLines), ("inspect", Inspect), ("stats", StatsLines));
        List<Container> cs = Docker.Containers(Vps, "rootless");
        Assert.Equal(4, cs.Count);
        Assert.Equal($"{Rootless} ps -a --no-trunc --format '{{{{json .}}}}'", _calls[0]);
        Assert.StartsWith($"{Rootless} inspect immich_server immich_postgres portainer plane-api", _calls[1]);
        Assert.Equal($"{Rootless} stats --no-stream --format '{{{{json .}}}}'", _calls[2]);
        Container c = cs.First(x => x.Name == "immich_server");
        Assert.Equal(("VPS_PROD", "rootless", "abcdef123456"), (c.Node, c.Daemon, c.Id));
        Assert.Equal(("running", "Up 3 days (healthy)", "0.0.0.0:2283->2283/tcp"), (c.State, c.Status, c.Ports));
        Assert.Equal(("immich", "immich-server", "/home/a/.homelab/immich", "/home/a/.homelab/immich/docker-compose.yml"), (c.Project, c.Service, c.WorkingDir, c.ConfigFiles));
        Assert.Equal(("healthy", "unless-stopped", 2L * 1024 * 1024 * 1024, 1.5), (c.Health, c.RestartPolicy, c.MemLimit, c.CpuLimit));
        Assert.Equal([new Mount("bind", "/srv/photos", "/usr/src/app/upload", "rw", "rw"), new Mount("volume", "model-cache", "/cache", "", "ro")], c.Mounts);
        Assert.Equal(("12.34%", "512MiB / 2GiB", "25.00%", "1.2kB / 3.4kB", "0B / 8kB", "42"), (c.CpuPct, c.MemUsage, c.MemPct, c.NetIo, c.BlockIo, c.Pids));
        Container pg = cs.First(x => x.Name == "immich_postgres");
        Assert.Equal((0.5, 0L, "", "always", "0.50%"), (pg.CpuLimit, pg.MemLimit, pg.Health, pg.RestartPolicy, pg.CpuPct));
        Container po = cs.First(x => x.Name == "portainer");
        Assert.Equal(("", "", "", "", 0.0), (po.CpuPct, po.MemUsage, po.Health, po.Project, po.CpuLimit));
        Assert.Equal("", cs.First(x => x.Name == "plane-api").RestartPolicy);
        Assert.DoesNotContain(cs, x => x.Name == "ghost_not_in_ps");
    }

    [Fact]
    public void Containers_fast_mode_empty_daemon_and_degraded_inspect()
    {
        Answers(("ps -a", PsLines));
        List<Container> cs = Docker.Containers(Vps, "rootless", withStats: false, withInspect: false);
        Assert.Equal([$"{Rootless} ps -a --no-trunc --format '{{{{json .}}}}'"], _calls);
        var groups = cs.GroupBy(c => c.Project).ToDictionary(g => g.Key, g => g.Select(c => c.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());
        Assert.Equal(["portainer"], groups[""]);
        Assert.Equal(["immich_postgres", "immich_server"], groups["immich"]);
        Assert.Equal(["plane-api"], groups["plane"]);

        _calls.Clear();
        Answers(("ps -a", "\n"));
        Assert.Empty(Docker.Containers(Nas, "rootful"));
        Assert.Single(_calls);
        Assert.StartsWith("sudo -n env DOCKER_HOST=unix:///var/run/docker.sock docker ps -a", _calls[0]);

        Answers(("ps -a", PsLines), ("inspect", new NodeException("boom")), ("stats", "not json\n"));
        cs = Docker.Containers(Vps, "rootless");
        Assert.Equal(4, cs.Count);
        Assert.Equal("", cs[0].Health);
    }

    [Fact]
    public void Compose_prefix_none_for_plain_and_quoted_for_odd_paths()
    {
        var plain = new Container { Node = "VPS_PROD", Daemon = "rootless", Id = "1", Name = "portainer", Image = "p", State = "running", Status = "Up" };
        Assert.Null(Docker.Compose(Vps, "rootless", plain));
        var two = new Container { Name = "plane-api", Project = "plane", Service = "api", WorkingDir = "/home/a/.homelab/plane", ConfigFiles = "/home/a/.homelab/plane/a.yml,/home/a/.homelab/plane/b.yml" };
        Assert.Equal($"cd /home/a/.homelab/plane && {Rootless} compose -p plane -f /home/a/.homelab/plane/a.yml -f /home/a/.homelab/plane/b.yml", Docker.Compose(Vps, "rootless", two));
        var odd = new Container { Name = "x", Project = "my stack", ConfigFiles = "/srv/my stack/compose.yml" };
        string? comp = Docker.Compose(Nas, "rootful", odd);
        Assert.StartsWith("sudo -n env DOCKER_HOST=unix:///var/run/docker.sock docker compose -p 'my stack' -f '/srv/my stack/compose.yml'", comp);
        Assert.DoesNotContain("cd ", comp);
    }

    private static readonly Container Comp = new() { Node = "VPS_PROD", Daemon = "rootless", Id = "1", Name = "immich_server", Image = "ghcr.io/immich:v3", State = "running", Status = "Up", Project = "immich", Service = "immich-server", WorkingDir = "/home/a/.homelab/immich", ConfigFiles = "/home/a/.homelab/immich/docker-compose.yml" };
    private static readonly Container Plain = new() { Node = "NAS_PROD", Daemon = "rootful", Id = "2", Name = "portainer", Image = "portainer/portainer-ce", State = "exited", Status = "Exited" };
    private static readonly string ComposeCmd = $"cd /home/a/.homelab/immich && {Rootless} compose -p immich -f /home/a/.homelab/immich/docker-compose.yml";

    private string RunAction(NodeConfig node, string daemon, Container c, string what, int code = 0, string output = "done\n")
    {
        string? seen = null;
        NodeShell.Launcher = (n, remote, timeout) =>
        {
            Assert.Equal(600, timeout);
            seen = remote;
            return new ShellRun(code, output, string.Empty);
        };
        string result = Docker.Action(node, daemon, c, what);
        Assert.Equal("done", result);
        return seen!;
    }

    [Fact]
    public void Actions_build_the_right_command_lines()
    {
        foreach (string what in new[] { "start", "stop", "restart" })
        {
            string cmd = RunAction(Vps, "rootless", Comp, what);
            Assert.Equal($"{Rootless} {what} immich_server", cmd);
        }

        Assert.Equal("sudo -n env DOCKER_HOST=unix:///var/run/docker.sock docker restart portainer", RunAction(Nas, "rootful", Plain, "restart"));
        Assert.Equal($"{Rootful} stop portainer", RunAction(Local, "rootful", Plain, "stop"));
        Assert.Equal($"{ComposeCmd} pull immich-server && {ComposeCmd} up -d immich-server", RunAction(Vps, "rootless", Comp, "pull"));
        Assert.Equal("sudo -n env DOCKER_HOST=unix:///var/run/docker.sock docker pull portainer/portainer-ce", RunAction(Nas, "rootful", Plain, "pull"));
        Assert.Equal($"{ComposeCmd} up -d --force-recreate immich-server", RunAction(Vps, "rootless", Comp, "recreate"));
        Assert.Equal($"{ComposeCmd} up -d", RunAction(Vps, "rootless", Comp, "up"));
        Assert.Equal($"{ComposeCmd} down", RunAction(Vps, "rootless", Comp, "down"));
    }

    [Fact]
    public void Compose_only_actions_refuse_plain_containers_before_touching_the_node()
    {
        NodeShell.Launcher = (_, _, _) => throw new InvalidOperationException("must not run");
        foreach (string what in new[] { "recreate", "up", "down" })
        {
            Assert.Contains("not a compose service", Assert.Throws<NodeException>(() => Docker.Action(Nas, "rootful", Plain, what)).Message);
        }

        Assert.Throws<NodeException>(() => Docker.Action(Vps, "rootless", Comp, "explode"));
        NodeShell.Launcher = (_, _, _) => new ShellRun(1, "", "Error response from daemon: no such container\n");
        NodeException ex = Assert.Throws<NodeException>(() => Docker.Action(Vps, "rootless", Comp, "restart"));
        Assert.Contains("restart immich_server failed", ex.Message);
        Assert.Contains("no such container", ex.Message);
    }

    [Fact]
    public void Disk_usage_parses_df_and_volumes_with_fallbacks()
    {
        string summary = Lines(
            new JsonObject { ["Type"] = "Images", ["TotalCount"] = "12", ["Active"] = "9", ["Size"] = "8.1GB", ["Reclaimable"] = "1.2GB (14%)" },
            new JsonObject { ["Type"] = "Local Volumes", ["TotalCount"] = "5", ["Active"] = "4", ["Size"] = "3.4GB", ["Reclaimable"] = "100MB (2%)" });
        const string verbose = """{ "Images": [], "Containers": [], "Volumes": [ { "Name": "immich_pgdata", "Size": "1.2GB", "Links": "1", "Mountpoint": "/var/lib/docker/volumes/immich_pgdata/_data" }, { "Name": "model-cache", "Size": "2.2GB", "Links": "0" } ], "BuildCache": [] }""";
        Answers(("system df -v --format json", verbose), ("system df --format", summary));
        DiskUsage d = Docker.Usage(Vps, "rootless");
        Assert.Equal([$"{Rootless} system df --format '{{{{json .}}}}'", $"{Rootless} system df -v --format json"], _calls);
        Assert.Null(d.Error);
        Assert.Equal(["Images", "Local Volumes"], d.Summary.Select(s => s.Type).ToArray());
        Assert.Equal("1.2GB (14%)", d.Summary[0].Reclaimable);
        Assert.Equal([new Volume("immich_pgdata", "1.2GB", "1", "/var/lib/docker/volumes/immich_pgdata/_data"), new Volume("model-cache", "2.2GB", "0", "")], d.Volumes);

        _calls.Clear();
        Answers(("system df -v --format json", "TYPE  TOTAL  ACTIVE\n"), ("system df --format", summary), ("volume ls", new JsonObject { ["Name"] = "v1", ["Driver"] = "local" }.ToJsonString() + "\n"));
        d = Docker.Usage(Nas, "rootful");
        Assert.Equal([new Volume("v1", "?", "", "")], d.Volumes);
        Assert.StartsWith("sudo -n env DOCKER_HOST=unix:///var/run/docker.sock docker volume ls", _calls[^1]);

        Answers(("system df", new NodeException("NAS_PROD: unreachable")), ("volume ls", new NodeException("NAS_PROD: unreachable")));
        d = Docker.Usage(Nas, "rootful");
        Assert.Empty(d.Summary);
        Assert.Empty(d.Volumes);
        Assert.Contains("unreachable", d.Error);
    }

    [Fact]
    public void Logs_command_line_quotes_the_name_and_keeps_output_on_a_bad_exit()
    {
        string? seen = null;
        NodeShell.Launcher = (_, remote, timeout) => { seen = remote; Assert.Equal(60, timeout); return new ShellRun(1, "2026-09-07T10:00:00Z hello\n", ""); };
        Assert.Equal("2026-09-07T10:00:00Z hello\n", Docker.Logs(Vps, "rootless", "immich_server", tail: 50));
        Assert.Equal($"{Rootless} logs --tail 50 --timestamps immich_server 2>&1", seen);
        Docker.Logs(Nas, "rootful", "odd name;rm -rf /");
        Assert.Contains("--tail 300 --timestamps 'odd name;rm -rf /' 2>&1", seen);
        Assert.StartsWith("sudo -n env DOCKER_HOST=", seen);
    }
}
