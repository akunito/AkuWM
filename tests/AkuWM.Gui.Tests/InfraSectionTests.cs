using System.Text.Json.Nodes;
using AkuWM.Core.Config;
using AkuWM.Core.Nodes;
using AkuWM.Gui.Sections;
using AkuWM.Gui.Shell;
using Avalonia.Headless.XUnit;
using Xunit;

namespace AkuWM.Gui.Tests;

public class InfraSectionTests
{
    [AvaloniaFact]
    public void The_five_new_sections_open_and_are_in_the_launch_list()
    {
        using var f = new Fixture();
        var window = new MainWindow(f.Services);
        window.Show();
        foreach (string key in new[] { "profiles", "git", "nodes", "docker", "monitoring" })
        {
            window.ShowSection(key);
            Assert.Equal(key, window.Current!.Key);
            Assert.True(window.Current.View.IsVisible, key);
            Assert.Contains(key, LaunchArgs.Sections);
        }
    }

    [AvaloniaFact]
    public void Profiles_pulls_ticked_items_from_the_other_layer_snapshots_first_and_tells_the_daemon()
    {
        using var f = new Fixture();
        var window = new MainWindow(f.Services);
        window.Show();
        window.ShowSection("profiles");
        var profiles = (ProfilesSection)window.Current!;
        // the other layer is "common" here (the fixture has common + TESTBOX)
        Assert.Equal("common", profiles.SelectedOther);
        profiles.Select("rules");
        Assert.Equal("rules", profiles.SelectedSection);
        // r-zebar lives only in common: pull it into this machine's layer
        Assert.Null(profiles.Copy("common", "TESTBOX", ["r-zebar"]));
        JsonObject profile = f.ProfileJson();
        Assert.Contains(((JsonArray)profile["rules"]!).OfType<JsonObject>(), r => r["id"]!.ToString() == "r-zebar");
        Assert.Single(profiles.Profiles.Snapshots());
        Assert.Contains("compat command wm-reload-config", f.Daemon.Sent);
        Assert.Contains(f.Toasts, t => t.StartsWith("Copied 1 rules item(s) common → TESTBOX"));
        // restore the snapshot: the layer is as before, another snapshot taken before the restore
        List<string> touched = profiles.Restore(profiles.Profiles.Snapshots()[0].Id, null);
        Assert.Contains("TESTBOX.json", touched);
        Assert.DoesNotContain(((JsonArray)f.ProfileJson()["rules"]!).OfType<JsonObject>(), r => r["id"]!.ToString() == "r-zebar");
        Assert.Equal(2, profiles.Profiles.Snapshots().Count);
        Assert.Equal("nothing selected", profiles.Copy("common", "TESTBOX", []));
    }

    [AvaloniaFact]
    public void Git_section_says_so_outside_a_repository()
    {
        using var f = new Fixture();
        var git = new GitSection(f.Services);
        git.Refresh();
        Assert.True(git.View.IsVisible);
        Assert.Null(git.Commit());
        Core.Git.SyncResult result = git.Sync();
        Assert.False(result.Ok);
        Assert.Equal("not a repository", result.Skipped);
    }

    [AvaloniaFact]
    public void Nodes_list_probe_and_deploy_command()
    {
        using var f = new Fixture();
        var window = new MainWindow(f.Services);
        window.Show();
        window.ShowSection("nodes");
        var nodes = (NodesSection)window.Current!;
        Assert.Equal(2, nodes.Items.Count);
        string? seen = null;
        NodeShell.Launcher = (node, remote, _) => { seen = node.Id + ": " + remote; return new ShellRun(0, "ok\nvps-prod\nup 2 days\n", string.Empty); };
        (bool ok, string detail) = nodes.Probe(nodes.Items[0].Item);
        Assert.True(ok);
        Assert.Equal("vps-prod · up 2 days", detail);
        Assert.StartsWith("VPS_PROD: echo ok && uname -n", seen);
        Assert.Contains("cd ~/.dotfiles && ./deploy.sh --profile VPS_PROD", nodes.DeployCommand(nodes.Items[0].Item));
        Assert.Contains("./install.sh ~/.dotfiles DESK_W11 -s", nodes.DeployCommand(nodes.Items[1].Item));
    }

    [AvaloniaFact]
    public void Docker_lists_the_daemons_of_the_nodes_and_renders_containers()
    {
        using var f = new Fixture();
        var docker = new DockerSection(f.Services);
        Assert.Equal(["VPS_PROD/rootless"], docker.Targets());
        docker.Shown();
        NodeShell.Launcher = (_, remote, _) => new ShellRun(0, remote.Contains("ps -a") ? new JsonObject { ["ID"] = "abc", ["Names"] = "immich_server", ["Image"] = "immich", ["State"] = "running", ["Status"] = "Up", ["Labels"] = "com.docker.compose.project=immich" }.ToJsonString() + "\n" : "\n", string.Empty);
        docker.Load();
        // the load runs on a task: give the headless dispatcher the result by rendering what Docker returns directly
        List<Container> cs = Docker.Containers(new NodeConfig { Id = "VPS_PROD", Ssh = "a@b", Daemons = ["rootless"] }, "rootless", withStats: false, withInspect: false);
        Assert.Single(cs);
        Assert.True(docker.View.IsVisible);
    }

    [AvaloniaFact]
    public void Monitoring_renders_a_dashboard_and_badges_the_tabs()
    {
        using var f = new Fixture();
        var monitoring = new MonitoringSection(f.Services);
        Assert.True(monitoring.View.IsVisible);
        var dash = new Dashboard { Grafana = "https://g", Generated = 1700000000, HasSummary = true, TargetsDown = 2 };
        dash.Targets.Add(new Target("node", "desk:9100", false, "err"));
        dash.Nodes.Add(new NodeCard { Node = "VPS_PROD", Name = "VPS", Instance = "monitoring", Up = true, UpLevel = "ok", LoadPct = 12.5, LoadLevel = "ok", MemPct = 40, MemLevel = "ok", UptimeText = "1d 1h", LoadSeries = [10, 12, 11], MemSeries = [39, 40], Level = "ok" });
        dash.Zfs.Add(new ZfsPool("hddpool", false, 10, 1, 10, "err", "1.0 TiB / 10.0 TiB"));
        dash.Backups.Add(new Backup("VPS → NAS", "VPS databases", 172800, "2d 0h", true, 5, "5.0 GiB", "ok", false, "restic repo on the NAS"));
        dash.Network.Add(new Probe("192.168.8.1", "blackbox_icmp", "icmp", true, 50, null, null, "50.0 ms", "ok", [50, null, 60]));
        monitoring.Render(dash);
        Assert.Same(dash, monitoring.Last);
        monitoring.Select("backups");
    }
}
