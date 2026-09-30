using System.Text.Json.Nodes;
using AkuWM.Core.Commands;
using AkuWM.Core.Config;
using AkuWM.Core.Ipc;
using AkuWM.Core.Nodes;
using Xunit;

namespace AkuWM.Tests;

[Collection("NodeShell")]
public class OpsCommandsTests : IDisposable
{
    private readonly string _dir;
    private readonly ConfigPaths _paths;
    private readonly CommandRouter _router;
    private readonly Func<NodeConfig, string, int, ShellRun> _before = NodeShell.Launcher;

    public OpsCommandsTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "akuwm-ops-" + Guid.NewGuid().ToString("N"));
        string config = Path.Combine(_dir, "akuwm");
        Directory.CreateDirectory(config);
        _paths = new ConfigPaths(config, "X13", Path.Combine(_dir, "state"));
        File.WriteAllText(Path.Combine(config, "common.json"), """
            { "version": 1,
              "shortcuts": [ { "id": "k-common", "keys": "Hyper+A", "kind": "exec", "command": "a" } ],
              "nodes": [ { "id": "VPS_PROD", "name": "VPS", "ssh": "akunito@100.64.0.6:56777", "daemons": [ "rootless" ], "prometheus_instance": "monitoring", "order": 1 },
                         { "id": "LOCAL", "ssh": "", "daemons": [] } ] }
            """);
        File.WriteAllText(Path.Combine(config, "DESK.json"), """{ "version": 1, "shortcuts": [ { "id": "k2", "keys": "Hyper+B", "kind": "exec", "command": "b" } ] }""");
        File.WriteAllText(Path.Combine(config, "X13.json"), """{ "version": 1, "shortcuts": [ { "id": "k3", "keys": "Hyper+C", "kind": "exec", "command": "c" } ] }""");
        _router = new CommandRouter(new ConfigCommands(_paths), new DoctorCommand(_paths, () => false), paths: _paths);
    }

    public void Dispose()
    {
        NodeShell.Launcher = _before;
        Directory.Delete(_dir, recursive: true);
    }

    private JsonNode Data(string line)
    {
        CommandResponse r = _router.Execute(line);
        Assert.True(r.Success, r.Error);
        return r.Data!;
    }

    [Fact]
    public void Profiles_list_diff_copy_and_snapshots_through_the_router()
    {
        Assert.True(CommandRouter.NeedsNoDaemon("profiles"));
        var rows = (JsonArray)Data("profiles list");
        Assert.Equal(["DESK", "X13", "common"], rows.Select(r => r!["profile"]!.ToString()).ToArray());
        Assert.True(rows[1]!["current"]!.GetValue<bool>());
        Assert.Equal(1, rows[0]!["shortcuts"]!.GetValue<int>());

        JsonNode summary = Data("profiles diff DESK X13");
        Assert.Equal(1, summary["shortcuts"]!["only_a"]!.GetValue<int>());
        Assert.Equal(1, summary["shortcuts"]!["only_b"]!.GetValue<int>());
        JsonNode diff = Data("profiles diff DESK X13 --section shortcuts");
        Assert.Equal("k2", diff["only_a"]![0]!["id"]!.ToString());

        Assert.False(_router.Execute("profiles copy DESK DESK --section shortcuts").Success);
        JsonNode copied = Data("profiles copy DESK X13 --section shortcuts --ids k2");
        Assert.Equal("k2", copied["copied"]![0]!.ToString());
        Assert.Equal(2, copied["destinationTotal"]!.GetValue<int>());
        var snaps = (JsonArray)Data("profiles snapshot list");
        Assert.Single(snaps);
        Assert.Equal("copy-shortcuts-DESK-to-X13", snaps[0]!["reason"]!.ToString());
        string id = snaps[0]!["id"]!.ToString();
        JsonNode sdiff = Data($"profiles snapshot diff {id}");
        Assert.Equal(1, sdiff["X13.json"]!["shortcuts"]!["only_now"]!.GetValue<int>());
        JsonNode restored = Data($"profiles snapshot restore {id} --sections shortcuts");
        Assert.Contains("X13.json", restored["restored"]!.AsArray().Select(f => f!.ToString()));
        Assert.Single(((JsonArray)Data("profiles diff X13 common --section shortcuts")["only_a"]!));
        Assert.False(_router.Execute("profiles snapshot restore nope").Success);
    }

    [Fact]
    public void Git_status_outside_a_repository_and_nodes_list_probe()
    {
        JsonNode status = Data("git status");
        Assert.False(status["repo"]!.GetValue<bool>());
        Assert.Null(Data("git commit -m x")["sha"]);

        var nodes = (JsonArray)Data("nodes list");
        Assert.Equal(["VPS_PROD", "LOCAL"], nodes.Select(n => n!["id"]!.ToString()).ToArray());
        Assert.True(nodes[1]!["local"]!.GetValue<bool>());
        NodeShell.Launcher = (node, remote, _) => new ShellRun(0, "ok\nvps-prod\n", string.Empty);
        JsonNode probe = Data("nodes probe vps_prod");
        Assert.True(probe["ok"]!.GetValue<bool>());
        Assert.Equal("vps-prod", probe["detail"]!.ToString());
        Assert.False(_router.Execute("nodes probe NOPE").Success);
    }

    [Fact]
    public void Docker_and_monitor_go_through_the_node_shell()
    {
        var calls = new List<string>();
        NodeShell.Launcher = (node, remote, _) =>
        {
            calls.Add(remote);
            if (remote.Contains("ps -a"))
            {
                return new ShellRun(0, new JsonObject { ["ID"] = "abc", ["Names"] = "immich_server", ["Image"] = "i", ["State"] = "running", ["Status"] = "Up", ["Labels"] = "" }.ToJsonString() + "\n", string.Empty);
            }

            if (remote.Contains("restart"))
            {
                return new ShellRun(0, "immich_server\n", string.Empty);
            }

            return new ShellRun(0, "\n", string.Empty);
        };
        var ps = (JsonArray)Data("docker ps VPS_PROD/rootless --fast");
        Assert.Single(ps);
        Assert.Equal("immich_server", ps[0]!["name"]!.ToString());
        Assert.Single(calls);
        JsonNode restarted = Data("docker restart VPS_PROD immich_server");
        Assert.Equal("immich_server", restarted["output"]!.ToString());
        Assert.Contains(calls, c => c.EndsWith("docker restart immich_server", StringComparison.Ordinal));
        Assert.False(_router.Execute("docker ps LOCAL").Success);
        Assert.False(_router.Execute("docker ps VPS_PROD/podman").Success);

        JsonNode dash = Data("monitor dashboard");
        Assert.Empty(dash["errors"]!.AsArray());
        Assert.Single(dash["nodes"]!.AsArray());
        Assert.Contains(calls, c => c.Contains("/api/v1/query --data-urlencode query=up;"));
    }
}
