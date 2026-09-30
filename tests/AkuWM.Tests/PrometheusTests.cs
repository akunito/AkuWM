using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AkuWM.Core.Config;
using AkuWM.Core.Nodes;
using Xunit;

namespace AkuWM.Tests;

/// <summary>
/// sway-apps' monitoring tests, ported. A fake Prometheus answers each curl
/// of the one-line command from a table keyed by query text (and "range").
/// </summary>
[Collection("NodeShell")] // both replace the static launcher: never in parallel
public partial class PrometheusTests : IDisposable
{
    private const double Day = 86400, Hour = 3600, GiB = 1024.0 * 1024 * 1024, TiB = GiB * 1024;
    private readonly Func<NodeConfig, string, int, ShellRun> _before = NodeShell.Launcher;
    private readonly List<(NodeConfig Node, string Remote, int Timeout)> _calls = [];
    private readonly long _now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    private AkuWmConfig _config;

    public PrometheusTests()
    {
        _config = DeskFixture.Configuration();
        _config.Nodes =
        [
            new NodeConfig { Id = "VPS_PROD", Name = "VPS", Ssh = "akunito@100.64.0.6:56777", PrometheusInstance = "monitoring", Order = 1 },
            new NodeConfig { Id = "NAS_PROD", Name = "NAS", Ssh = "akunito@192.168.20.200", PrometheusInstance = "nas", Order = 2 },
            new NodeConfig { Id = "DESK", Name = "Desk", Ssh = "", PrometheusInstance = "desk", Order = 3 },
            new NodeConfig { Id = "OLD", Name = "Retired", Ssh = "x@old", PrometheusInstance = "old", Order = 4, Enabled = false },
        ];
    }

    public void Dispose() => NodeShell.Launcher = _before;

    [GeneratedRegex("curl -s -m \\d+ \\S+/api/v1/(query_range|query) --data-urlencode '?query=(.*?)'?(?: --data-urlencode start=\\d+ --data-urlencode end=\\d+ --data-urlencode step=\\d+)?; echo$")]
    private static partial Regex Segment();

    /// <summary>The (query, range) of every curl in the command line.</summary>
    private static List<(string Query, bool Range)> Segments(string command)
    {
        var list = new List<(string, bool)>();
        foreach (string seg in command.Split(" ; "))
        {
            Match m = Segment().Match(seg.Trim());
            Assert.True(m.Success, "not a curl segment: " + seg);
            list.Add((m.Groups[2].Value.Replace("'\"'\"'", "'"), m.Groups[1].Value == "query_range"));
        }

        return list;
    }

    private static JsonArray Vec(params (Dictionary<string, string> Labels, object Value)[] rows)
    {
        var a = new JsonArray();
        foreach ((Dictionary<string, string> labels, object value) in rows)
        {
            var metric = new JsonObject();
            foreach ((string k, string v) in labels)
            {
                metric[k] = v;
            }

            a.Add((JsonNode)new JsonObject { ["metric"] = metric, ["value"] = new JsonArray(1700000000, value.ToString()) });
        }

        return a;
    }

    private static Dictionary<string, string> L(params (string K, string V)[] kv) => kv.ToDictionary(p => p.K, p => p.V, StringComparer.Ordinal);

    private static JsonArray Mat(Dictionary<string, string> labels, params object[] values)
    {
        var metric = new JsonObject();
        foreach ((string k, string v) in labels)
        {
            metric[k] = v;
        }

        var series = new JsonArray();
        for (int i = 0; i < values.Length; i++)
        {
            series.Add((JsonNode)new JsonArray(1700000000 + i * 300, values[i].ToString()));
        }

        return [new JsonObject { ["metric"] = metric, ["values"] = series }];
    }

    private static Dictionary<string, string> NodeQueries(string inst)
    {
        string sel = $"instance=~\"{inst}(:.*)?\"";
        string mem = $"100 * (1 - node_memory_MemAvailable_bytes{{{sel}}} / node_memory_MemTotal_bytes{{{sel}}})";
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["up"] = $"max(up{{instance=\"{inst}\"}})",
            ["load1"] = $"node_load1{{{sel}}}",
            ["ncpu"] = $"count(node_cpu_seconds_total{{{sel},mode=\"idle\"}})",
            ["mem"] = mem,
            ["uptime"] = $"time() - node_boot_time_seconds{{{sel}}}",
            ["load_range"] = $"100 * node_load1{{{sel}}} / scalar(count(node_cpu_seconds_total{{{sel},mode=\"idle\"}}))",
            ["mem_range"] = mem,
        };
    }

    private static string R(string q) => "range:" + q;

    private Dictionary<string, object> Canned()
    {
        Dictionary<string, string> m = NodeQueries("monitoring"), n = NodeQueries("nas"), d = NodeQueries("desk");
        string fsSize = $"node_filesystem_size_bytes{{{Prometheus.FsFilter}}}";
        string fsAvail = $"node_filesystem_avail_bytes{{{Prometheus.FsFilter}}}";
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["up"] = Vec((L(("job", "node"), ("instance", "monitoring:9100")), 1), (L(("job", "node"), ("instance", "nas:9100")), 1), (L(("job", "node"), ("instance", "desk:9100")), 0), (L(("job", "blackbox_icmp"), ("instance", "nas-aku")), 1)),
            [m["up"]] = Vec((L(), 1)),
            [m["load1"]] = Vec((L(), 0.5)),
            [m["ncpu"]] = Vec((L(), 4)),
            [m["mem"]] = Vec((L(), 40)),
            [m["uptime"]] = Vec((L(), 90061)),
            [R(m["load_range"])] = Mat(L(), 10, 12, 11, 13, 12.5),
            [R(m["mem_range"])] = Mat(L(), 39, 40, 41),
            [n["up"]] = Vec((L(), 1)),
            [n["load1"]] = Vec((L(), 6)),
            [n["ncpu"]] = Vec((L(), 4)),
            [n["mem"]] = Vec((L(), 90)),
            [n["uptime"]] = Vec((L(), 3700)),
            [R(n["load_range"])] = Mat(L(), 140, 150),
            [R(n["mem_range"])] = Mat(L(), 88, 90),
            [d["up"]] = Vec((L(), 0)),
            [fsSize] = Vec(
                (L(("instance", "monitoring:9100"), ("mountpoint", "/")), 100 * GiB),
                (L(("instance", "nas:9100"), ("mountpoint", "/")), 100 * GiB), (L(("instance", "nas:9100"), ("mountpoint", "/mnt/data")), 10 * TiB),
                (L(("instance", "nas:9100"), ("mountpoint", "/zero")), 0), (L(("instance", "nas:9100"), ("mountpoint", "/noavail")), 5 * GiB),
                (L(("instance", "pfsense:9100"), ("mountpoint", "/")), 20 * GiB)),
            [fsAvail] = Vec(
                (L(("instance", "monitoring:9100"), ("mountpoint", "/")), 50 * GiB),
                (L(("instance", "nas:9100"), ("mountpoint", "/")), 10 * GiB), (L(("instance", "nas:9100"), ("mountpoint", "/mnt/data")), 7 * TiB),
                (L(("instance", "nas:9100"), ("mountpoint", "/zero")), 0),
                (L(("instance", "pfsense:9100"), ("mountpoint", "/")), 15 * GiB)),
            ["nas_zfs_pool_healthy"] = Vec((L(("pool", "ssdpool")), 1), (L(("pool", "hddpool")), 0), (L(("pool", "tank")), 1)),
            ["nas_zfs_pool_size_bytes"] = Vec((L(("pool", "ssdpool")), TiB), (L(("pool", "hddpool")), 10 * TiB)),
            ["nas_zfs_pool_allocated_bytes"] = Vec((L(("pool", "ssdpool")), 700 * GiB), (L(("pool", "hddpool")), TiB)),
            ["time() - nas_backup_last_success"] = Vec((L(("dataset", "vps_databases")), 2 * Day), (L(("dataset", "vps_services")), 4 * Day), (L(("dataset", "vps_nextcloud")), 8 * Day), (L(("dataset", "desk_home")), Day), (L(("dataset", "x13_home")), Day)),
            ["nas_backup_status"] = Vec((L(("dataset", "vps_databases")), 1), (L(("dataset", "vps_services")), 1), (L(("dataset", "vps_nextcloud")), 1), (L(("dataset", "desk_home")), 0)),
            ["backup_repo_size_bytes"] = Vec(
                (L(("dataset", "vps_databases"), ("direction", "vps_to_nas")), 5 * GiB), (L(("dataset", "vps_services"), ("direction", "vps_to_nas")), 6 * GiB),
                (L(("dataset", "vps_nextcloud"), ("direction", "vps_to_nas")), 7 * GiB), (L(("dataset", "mystery"), ("direction", "vps_to_nas")), GiB),
                (L(("dataset", "desk_home"), ("direction", "workstation_to_nas")), 8 * GiB), (L(("dataset", "x13_home"), ("direction", "workstation_to_nas")), 9 * GiB),
                (L(("dataset", "offsite_configs"), ("direction", "nas_to_vps")), GiB), (L(("dataset", "offsite_data"), ("direction", "nas_to_vps")), 2 * TiB)),
            ["time() - nas_offsite_backup_last_success"] = Vec((L(("exported_job", "configs")), Day), (L(("exported_job", "data")), 5 * Day)),
            ["nas_offsite_backup_status"] = Vec((L(("exported_job", "configs")), 1), (L(("exported_job", "data")), 1), (L(("exported_job", "extra")), 0)),
            ["time() - mariadb_backup_daily_last_success_timestamp"] = Vec((L(), Day)),
            ["mariadb_backup_daily_status"] = Vec((L(), 1)),
            ["time() - mariadb_backup_hourly_last_success_timestamp"] = Vec((L(), 4 * Hour)),
            ["mariadb_backup_hourly_status"] = Vec((L(), 1)),
            ["time() - postgresql_backup_daily_last_success_timestamp"] = Vec((L(), 4 * Day)),
            ["postgresql_backup_daily_status"] = Vec((L(), 1)),
            ["time() - postgresql_backup_hourly_last_success_timestamp"] = Vec((L(), 30 * Hour)),
            ["postgresql_backup_hourly_status"] = Vec((L(), 1)),
            ["pfsense_backup_last_success"] = Vec((L(), _now - 2 * Day)),
            ["pfsense_backup_status"] = Vec((L(), 1)),
            ["probe_success"] = Vec((L(("instance", "192.168.8.1"), ("job", "blackbox_icmp")), 1), (L(("instance", "10.0.0.1"), ("job", "blackbox_icmp")), 1),
                (L(("instance", "nas-aku"), ("job", "blackbox_icmp")), 0),
                (L(("instance", "https://plane.akunito.com"), ("job", "blackbox_http_2xx")), 1),
                (L(("instance", "https://bad.akunito.com"), ("job", "blackbox_http_2xx")), 0),
                (L(("instance", "https://slow.akunito.com"), ("job", "blackbox_http_2xx")), 1)),
            ["probe_icmp_duration_seconds{phase=\"rtt\"}"] = Vec((L(("instance", "192.168.8.1")), 0.05), (L(("instance", "10.0.0.1")), 0.1)),
            ["probe_duration_seconds{job=~\"blackbox_http.*\"}"] = Vec((L(("instance", "https://plane.akunito.com")), 0.3), (L(("instance", "https://bad.akunito.com")), 0.1), (L(("instance", "https://slow.akunito.com")), 1.5)),
            ["probe_http_status_code"] = Vec((L(("instance", "https://plane.akunito.com")), 200), (L(("instance", "https://bad.akunito.com")), 502), (L(("instance", "https://slow.akunito.com")), 200)),
            [R("avg by (instance) (probe_icmp_duration_seconds{phase=\"rtt\"}) * 1000")] = Mat(L(("instance", "192.168.8.1")), 50, "NaN", 60),
        };
    }

    /// <summary>Wires the fake: each segment answered from the table, a string emitted verbatim, missing keys empty.</summary>
    private void Fake(Dictionary<string, object> canned)
    {
        NodeShell.Launcher = (node, remote, timeout) =>
        {
            _calls.Add((node, remote, timeout));
            var lines = new List<string>();
            foreach ((string query, bool range) in Segments(remote))
            {
                string key = range ? R(query) : query;
                if (!canned.TryGetValue(key, out object? reply))
                {
                    reply = new JsonArray();
                }

                if (reply is string raw)
                {
                    lines.Add(raw);
                }
                else
                {
                    var data = new JsonObject { ["status"] = "success", ["data"] = new JsonObject { ["resultType"] = range ? "matrix" : "vector", ["result"] = ((JsonArray)reply).DeepClone() } };
                    lines.Add(data.ToJsonString());
                }
            }

            return new ShellRun(0, string.Join('\n', lines) + "\n", string.Empty);
        };
    }

    private Dashboard Dash(Dictionary<string, object>? canned = null)
    {
        Fake(canned ?? Canned());
        return Prometheus.Build(_config, _now);
    }

    private static List<Backup> Rows(Dashboard d, string group) => d.Backups.Where(b => b.Group == group).ToList();

    // ---- levels ---------------------------------------------------------------

    [Fact]
    public void Level_bands_all_families()
    {
        Assert.Equal(["", "ok", "warn", "warn", "err"], new[] { Levels.Pct(null), Levels.Pct(59.9), Levels.Pct(60), Levels.Pct(84.9), Levels.Pct(85) });
        Assert.Equal(["ok", "warn", "err"], new[] { Levels.Load(69), Levels.Load(70), Levels.Load(100) });
        Assert.Equal(["ok", "warn", "err"], new[] { Levels.Age(2 * Day), Levels.Age(3 * Day), Levels.Age(7 * Day) });
        Assert.Equal(["ok", "warn", "err"], new[] { Levels.Age(2 * Hour, hourly: true), Levels.Age(3 * Hour, hourly: true), Levels.Age(24 * Hour, hourly: true) });
        Assert.Equal(["ok", "warn", "err"], new[] { Levels.Rtt(79), Levels.Rtt(80), Levels.Rtt(200) });
        Assert.Equal(["ok", "warn", "err"], new[] { Levels.Http(0.9), Levels.Http(1), Levels.Http(3) });
    }

    [Fact]
    public void Flag_worst_and_pct_text()
    {
        Assert.Equal(("ok", "err", "warn", ""), (Levels.Flag(true), Levels.Flag(false), Levels.Flag(false, "warn"), Levels.Flag(null)));
        Assert.Equal("err", Levels.Worst("ok", "err", "warn"));
        Assert.Equal("warn", Levels.Worst("", "ok", "warn"));
        Assert.Equal("", Levels.Worst());
        Assert.Equal("", Levels.Worst("", ""));
        Assert.Equal("ok", Levels.Worst("ok"));
        Assert.Equal(("—", "50%", "52%"), (Levels.PctText(null), Levels.PctText(49.6), Levels.PctText(51.5)));
        Assert.Equal(12.34, Levels.ParsePct("12.34%"));
        Assert.Equal(7, Levels.ParsePct(" 7 "));
        Assert.Null(Levels.ParsePct("n/a"));
        Assert.Null(Levels.ParsePct(null));
    }

    [Fact]
    public void Fmt_edge_cases()
    {
        Assert.Equal("—", Prometheus.Fmt(null, "pct"));
        Assert.Equal(("UP", "DOWN"), (Prometheus.Fmt(1, "bool"), Prometheus.Fmt(0, "bool")));
        Assert.Equal("50%", Prometheus.Fmt(49.6, "pct"));
        Assert.Equal(("1d 1h", "1h 1m", "5m", "0m", "—"), (Prometheus.Fmt(90061, "dur"), Prometheus.Fmt(3660, "dur"), Prometheus.Fmt(300, "dur"), Prometheus.Fmt(59, "dur"), Prometheus.Fmt(-1, "dur")));
        Assert.Equal(("512.0 B", "1.0 KiB", "1.5 GiB", "2.0 TiB", "1.0 PiB"), (Prometheus.Fmt(512, "bytes"), Prometheus.Fmt(1024, "bytes"), Prometheus.Fmt(1.5 * GiB, "bytes"), Prometheus.Fmt(2 * TiB, "bytes"), Prometheus.Fmt(1024 * TiB, "bytes")));
        Assert.Equal("3", Prometheus.Fmt(3, "num"));
    }

    [Fact]
    public void Scalar_by_and_series_helpers()
    {
        Assert.Null(Prometheus.Scalar([]));
        Assert.Equal(2.5, Prometheus.Scalar(Vec((L(), 2.5))));
        var by = Prometheus.By(Vec((L(("a", "1"), ("b", "x")), 3), (L(("a", "2")), 4)), "a", "b");
        Assert.Equal(3, by["1||x"]);
        Assert.Equal(4, by["2||"]);
        Assert.Equal([1.0, null, 3.0], Prometheus.Series(Mat(L(), 1, "NaN", 3)));
        Assert.Empty(Prometheus.Series([]));
    }

    // ---- batch ----------------------------------------------------------------

    [Fact]
    public void Batch_builds_one_command_and_pads_short_or_garbage_output()
    {
        Fake(new Dictionary<string, object>(StringComparer.Ordinal) { ["up"] = Vec((L(("job", "node")), 1)), [R("node_load1")] = Mat(L(), 1, 2) });
        List<JsonArray> results = Prometheus.Batch(_config, [new("up"), new("node_load1", 3600, 60), new("missing")], _now);
        Assert.Single(_calls);
        Assert.Equal("VPS_PROD", _calls[0].Node.Id);
        List<(string Query, bool Range)> segs = Segments(_calls[0].Remote);
        Assert.Equal([("up", false), ("node_load1", true), ("missing", false)], segs);
        Assert.Contains($"--data-urlencode start={_now - 3600} --data-urlencode end={_now} --data-urlencode step=60", _calls[0].Remote);
        Assert.Equal(3, results.Count);
        Assert.Single(results[0]);
        Assert.Equal(2, ((JsonArray)results[1][0]!["values"]!).Count);
        Assert.Empty(results[2]);

        Fake(new Dictionary<string, object>(StringComparer.Ordinal) { ["a"] = "not json", ["b"] = "{\"status\":\"error\",\"error\":\"bad\"}" });
        results = Prometheus.Batch(_config, [new("a"), new("b"), new("c"), new("d")], _now);
        Assert.Equal(4, results.Count);
        Assert.All(results, r => Assert.Empty(r));
    }

    [Fact]
    public void Without_a_prometheus_node_nothing_is_tried()
    {
        _config.Nodes = [new NodeConfig { Id = "NAS_PROD", Name = "NAS", Ssh = "akunito@192.168.20.200", PrometheusInstance = "nas" }];
        Dashboard dash = Dash(new Dictionary<string, object>(StringComparer.Ordinal));
        Assert.Empty(_calls);
        Assert.Single(dash.Errors);
        Assert.Contains("VPS_PROD", dash.Errors[0]);
        Assert.Empty(dash.Nodes);
        Assert.Empty(dash.Targets);
        Assert.False(dash.HasSummary);
        Assert.Throws<NodeException>(() => Prometheus.Batch(_config, [new("up")], _now));
    }

    // ---- dashboard ------------------------------------------------------------

    [Fact]
    public void Node_levels()
    {
        Dashboard dash = Dash();
        Assert.Empty(dash.Errors);
        Assert.Single(_calls);
        Assert.Equal(["VPS_PROD", "NAS_PROD", "DESK"], dash.Nodes.Select(c => c.Node).ToArray());
        NodeCard vps = dash.Nodes[0], nas = dash.Nodes[1], desk = dash.Nodes[2];
        Assert.Equal((true, "ok"), (vps.Up, vps.UpLevel));
        Assert.Equal((0.5, 4.0, 12.5, "ok"), (vps.Load1, vps.Ncpu, vps.LoadPct, vps.LoadLevel));
        Assert.Equal((40.0, "ok"), (vps.MemPct, vps.MemLevel));
        Assert.Equal((90061.0, "1d 1h"), (vps.UptimeS, vps.UptimeText));
        Assert.Equal("ok", vps.Level);
        Assert.Equal((150.0, "err"), (nas.LoadPct, nas.LoadLevel));
        Assert.Equal((90.0, "err"), (nas.MemPct, nas.MemLevel));
        Assert.Equal("1h 1m", nas.UptimeText);
        Assert.Equal("err", nas.Level);
        Assert.Equal((false, "err"), (desk.Up, desk.UpLevel));
        Assert.Equal(("", "", "—"), (desk.LoadLevel, desk.MemLevel, desk.UptimeText));
        Assert.Equal("err", desk.Level);

        Dictionary<string, object> c = Canned();
        c[NodeQueries("monitoring")["mem"]] = Vec((L(), 60));
        NodeCard vps2 = Dash(c).Nodes[0];
        Assert.Equal(("warn", "warn"), (vps2.MemLevel, vps2.Level));
    }

    [Fact]
    public void Lightweight_nodes_and_sparklines()
    {
        Dashboard dash = Dash();
        NodeCard vps = dash.Nodes[0], nas = dash.Nodes[1], desk = dash.Nodes[2];
        Assert.True(desk.Lightweight);
        Assert.False(vps.Lightweight);
        Assert.Empty(desk.LoadSeries);
        Assert.Empty(desk.MemSeries);
        Assert.Equal(5, vps.LoadSeries.Count);
        Assert.Equal(12.5, vps.LoadSeries[^1]);
        Assert.Equal([39.0, 40.0, 41.0], vps.MemSeries);
        Assert.Equal([140.0, 150.0], nas.LoadSeries);

        Dictionary<string, object> c = Canned();
        c.Remove(NodeQueries("nas")["mem"]);
        nas = Dash(c).Nodes[1];
        Assert.False(nas.Lightweight);
        Assert.Null(nas.MemPct);
        Assert.Equal("", nas.MemLevel);

        c = Canned();
        c.Remove(NodeQueries("monitoring")["up"]);
        vps = Dash(c).Nodes[0];
        Assert.Null(vps.Up);
        Assert.Equal("", vps.UpLevel);
        Assert.Equal("ok", vps.Level);
    }

    [Fact]
    public void Filesystems_map_to_nodes_and_other()
    {
        Dashboard dash = Dash();
        Assert.Contains(Segments(_calls[0].Remote), s => s.Query == $"node_filesystem_size_bytes{{{Prometheus.FsFilter}}}");
        NodeCard vps = dash.Nodes[0], nas = dash.Nodes[1], desk = dash.Nodes[2];
        Assert.Equal(["/"], vps.Fs.Select(f => f.Mountpoint).ToArray());
        Assert.Equal((100 * GiB, 50 * GiB, 50.0, "ok"), (vps.Fs[0].Size, vps.Fs[0].Avail, vps.Fs[0].UsedPct, vps.Fs[0].Level));
        Assert.Equal("50.0 GiB / 100.0 GiB · 50.0 GiB free", vps.Fs[0].Text);
        Assert.Equal(["/", "/mnt/data"], nas.Fs.Select(f => f.Mountpoint).OrderBy(m => m, StringComparer.Ordinal).ToArray());
        Filesystem root = nas.Fs.First(f => f.Mountpoint == "/");
        Assert.Equal((90.0, "err"), (root.UsedPct, root.Level));
        Assert.Equal("ok", nas.Fs.First(f => f.Mountpoint == "/mnt/data").Level);
        Assert.Empty(desk.Fs);
        Assert.Equal(["pfsense"], dash.OtherStorage.Keys.ToArray());
        Assert.Equal(("/", 25.0), (dash.OtherStorage["pfsense"][0].Mountpoint, dash.OtherStorage["pfsense"][0].UsedPct));

        Dictionary<string, object> c = Canned();
        c[$"node_filesystem_avail_bytes{{{Prometheus.FsFilter}}}"] = Vec((L(("instance", "monitoring:9100"), ("mountpoint", "/")), 5 * GiB));
        vps = Dash(c).Nodes[0];
        Assert.Equal(("ok", "ok", "err", "err"), (vps.MemLevel, vps.LoadLevel, vps.Fs[0].Level, vps.Level));
    }

    [Fact]
    public void Zfs_pool_levels()
    {
        Dashboard dash = Dash();
        Assert.Equal(["hddpool", "ssdpool", "tank"], dash.Zfs.Select(p => p.Pool).ToArray());
        ZfsPool hdd = dash.Zfs[0], ssd = dash.Zfs[1], tank = dash.Zfs[2];
        Assert.Equal((false, 10.0, "err"), (hdd.Healthy, hdd.UsedPct, hdd.Level));
        Assert.Equal("1.0 TiB / 10.0 TiB", hdd.Text);
        Assert.True(ssd.Healthy);
        Assert.Equal(100.0 * 700 / 1024, ssd.UsedPct!.Value, 6);
        Assert.Equal("warn", ssd.Level);
        Assert.Equal((true, null, null, null, "ok", "—"), (tank.Healthy, tank.Size, tank.Alloc, tank.UsedPct, tank.Level, tank.Text));

        Dictionary<string, object> c = Canned();
        c["nas_zfs_pool_allocated_bytes"] = Vec((L(("pool", "ssdpool")), 0.9 * TiB), (L(("pool", "hddpool")), TiB));
        Assert.Equal("err", Dash(c).Zfs.First(p => p.Pool == "ssdpool").Level);
    }

    [Fact]
    public void Backup_age_bands_and_status()
    {
        Dashboard dash = Dash();
        Dictionary<string, Backup> vps = Rows(dash, "VPS → NAS").ToDictionary(b => b.Name);
        Assert.Equal((2 * Day, "2d 0h", true, "ok"), (vps["VPS databases"].AgeS, vps["VPS databases"].AgeText, vps["VPS databases"].Ok, vps["VPS databases"].Level));
        Assert.Equal(("4d 0h", "warn"), (vps["VPS services"].AgeText, vps["VPS services"].Level));
        Assert.Equal(("8d 0h", "err"), (vps["VPS Nextcloud"].AgeText, vps["VPS Nextcloud"].Level));
        Assert.Equal((5 * GiB, "5.0 GiB", "restic repo on the NAS"), (vps["VPS databases"].Size, vps["VPS databases"].SizeText, vps["VPS databases"].Detail));
        Dictionary<string, Backup> ws = Rows(dash, "Workstations → NAS").ToDictionary(b => b.Name);
        Assert.Equal(("1d 0h", false, "err"), (ws["DESK home"].AgeText, ws["DESK home"].Ok, ws["DESK home"].Level));
        Assert.Equal((null, "ok"), (ws["LAPTOP_X13 home"].Ok, ws["LAPTOP_X13 home"].Level));
        Assert.DoesNotContain(Rows(dash, "VPS → NAS").Concat(Rows(dash, "Workstations → NAS")), b => b.Hourly);
        Backup mystery = vps["mystery"];
        Assert.Equal((null, "—", null, ""), (mystery.AgeS, mystery.AgeText, mystery.Ok, mystery.Level));
    }

    [Fact]
    public void Backup_names_groups_and_hourly_bands()
    {
        Dashboard dash = Dash();
        var groups = new List<string>();
        foreach (Backup b in dash.Backups)
        {
            if (groups.Count == 0 || groups[^1] != b.Group)
            {
                groups.Add(b.Group);
            }
        }

        Assert.Equal(["VPS → NAS", "Workstations → NAS", "NAS → VPS (offsite)", "Databases (VPS)", "pfSense"], groups);
        Assert.Equal(["mystery", "VPS databases", "VPS Nextcloud", "VPS services"], Rows(dash, "VPS → NAS").Select(b => b.Name).ToArray());
        Assert.Equal(["DESK home", "LAPTOP_X13 home"], Rows(dash, "Workstations → NAS").Select(b => b.Name).ToArray());
        Dictionary<string, Backup> db = Rows(dash, "Databases (VPS)").ToDictionary(b => b.Name);
        Assert.Equal(["MariaDB daily", "MariaDB hourly", "PostgreSQL daily", "PostgreSQL hourly"], db.Keys.ToArray());
        Assert.Equal((false, "1d 0h", "ok"), (db["MariaDB daily"].Hourly, db["MariaDB daily"].AgeText, db["MariaDB daily"].Level));
        Assert.Equal((true, "4h 0m", "warn"), (db["MariaDB hourly"].Hourly, db["MariaDB hourly"].AgeText, db["MariaDB hourly"].Level));
        Assert.Equal("warn", db["PostgreSQL daily"].Level);
        Assert.Equal(("1d 6h", "err"), (db["PostgreSQL hourly"].AgeText, db["PostgreSQL hourly"].Level));
        Assert.All(db.Values, b => Assert.True(b.Ok == true && b.Size is null && b.SizeText == string.Empty));

        Dictionary<string, object> c = Canned();
        c["time() - mariadb_backup_hourly_last_success_timestamp"] = Vec((L(), 600));
        c["mariadb_backup_hourly_status"] = Vec((L(), 0));
        Backup row = Dash(c).Backups.First(b => b.Name == "MariaDB hourly");
        Assert.Equal(("10m", false, "err"), (row.AgeText, row.Ok, row.Level));
    }

    [Fact]
    public void Offsite_uses_the_exported_job()
    {
        List<Backup> off = Rows(Dash(), "NAS → VPS (offsite)");
        Assert.Equal(["NAS configs", "NAS data", "extra"], off.Select(b => b.Name).ToArray());
        Backup cfg = off[0], data = off[1], extra = off[2];
        Assert.Equal(("1d 0h", true, GiB, "1.0 GiB", "ok", "restic on the VPS"), (cfg.AgeText, cfg.Ok, cfg.Size, cfg.SizeText, cfg.Level, cfg.Detail));
        Assert.Equal(("5d 0h", true, "2.0 TiB", "warn"), (data.AgeText, data.Ok, data.SizeText, data.Level));
        Assert.Equal((null, "—", false, null, "", "err"), (extra.AgeS, extra.AgeText, extra.Ok, extra.Size, extra.SizeText, extra.Level));

        Dictionary<string, object> c = Canned();
        c["nas_offsite_backup_status"] = Vec((L(("exported_job", "configs")), 1));
        Backup data2 = Rows(Dash(c), "NAS → VPS (offsite)").First(b => b.Name == "NAS data");
        Assert.Equal((null, "warn"), (data2.Ok, data2.Level));
    }

    [Fact]
    public void Pfsense_cases()
    {
        Backup row = Rows(Dash(), "pfSense").Single();
        Assert.Equal("config backup", row.Name);
        Assert.InRange(row.AgeS!.Value, 2 * Day - 60, 2 * Day + 60);
        Assert.Equal((true, "ok", ""), (row.Ok, row.Level, row.Detail));

        Dictionary<string, object> c = Canned();
        c["pfsense_backup_last_success"] = Vec((L(), 0));
        c["pfsense_backup_status"] = Vec((L(), 0));
        row = Rows(Dash(c), "pfSense").Single();
        Assert.Null(row.AgeS);
        Assert.Equal(("—", false, "err"), (row.AgeText, row.Ok, row.Level));
        Assert.Contains("no backup file", row.Detail);

        c = Canned();
        c["pfsense_backup_last_success"] = Vec((L(), _now - Day));
        c["pfsense_backup_status"] = Vec((L(), 0));
        row = Rows(Dash(c), "pfSense").Single();
        Assert.InRange(row.AgeS!.Value, Day - 60, Day + 60);
        Assert.Equal((false, "err"), (row.Ok, row.Level));
        Assert.Contains("FAILED", row.Detail);

        c = Canned();
        c.Remove("pfsense_backup_last_success");
        c.Remove("pfsense_backup_status");
        row = Rows(Dash(c), "pfSense").Single();
        Assert.Equal((null, false, "err"), (row.AgeS, row.Ok, row.Level));
    }

    [Fact]
    public void Network_icmp_and_http()
    {
        Dashboard dash = Dash();
        Assert.Equal(
            [("icmp", "10.0.0.1"), ("icmp", "192.168.8.1"), ("icmp", "nas-aku"), ("http", "https://bad.akunito.com"), ("http", "https://plane.akunito.com"), ("http", "https://slow.akunito.com")],
            dash.Network.Select(x => (x.Kind, x.Instance)).ToArray());
        Dictionary<string, Probe> by = dash.Network.ToDictionary(x => x.Instance);
        Probe lan = by["192.168.8.1"];
        Assert.Equal((true, 50.0, "50.0 ms", "ok"), (lan.Up, lan.RttMs, lan.Text, lan.Level));
        Assert.Equal([50.0, null, 60.0], lan.Series);
        Assert.Equal((100.0, "warn"), (by["10.0.0.1"].RttMs, by["10.0.0.1"].Level));
        Assert.Empty(by["10.0.0.1"].Series);
        Probe down = by["nas-aku"];
        Assert.Equal((false, null, "—", "err"), (down.Up, down.RttMs, down.Text, down.Level));
        Probe bad = by["https://bad.akunito.com"];
        Assert.Equal((false, 502.0, 0.1, "HTTP 502 · 0.10 s", "err"), (bad.Up, bad.Code, bad.DurationS, bad.Text, bad.Level));
        Assert.Empty(bad.Series);
        Assert.Equal(("HTTP 200 · 0.30 s", "ok"), (by["https://plane.akunito.com"].Text, by["https://plane.akunito.com"].Level));
        Assert.Equal(("HTTP 200 · 1.50 s", "warn"), (by["https://slow.akunito.com"].Text, by["https://slow.akunito.com"].Level));

        Dictionary<string, object> c = Canned();
        c["probe_icmp_duration_seconds{phase=\"rtt\"}"] = Vec((L(("instance", "192.168.8.1")), 0.05), (L(("instance", "nas-aku")), 0.5));
        c["probe_http_status_code"] = Vec((L(("instance", "https://plane.akunito.com")), 200));
        Dictionary<string, Probe> by2 = Dash(c).Network.ToDictionary(x => x.Instance);
        Assert.Equal((500.0, "err"), (by2["nas-aku"].RttMs, by2["nas-aku"].Level));
        Assert.Equal("1.50 s", by2["https://slow.akunito.com"].Text);
    }

    [Fact]
    public void Targets_and_summary()
    {
        Dashboard dash = Dash();
        Assert.Equal(
            [("node", "desk:9100", false, "err"), ("blackbox_icmp", "nas-aku", true, "ok"), ("node", "monitoring:9100", true, "ok"), ("node", "nas:9100", true, "ok")],
            dash.Targets.Select(t => (t.Job, t.Instance, t.Up, t.Level)).ToArray());
        Assert.Equal((1, "err", "err", "err", "err"), (dash.TargetsDown, dash.NodesLevel, dash.StorageLevel, dash.BackupsLevel, dash.NetworkLevel));
        Assert.Equal(Prometheus.DefaultGrafana, dash.Grafana);
        Assert.True(dash.HasSummary);

        Dictionary<string, object> c = Canned();
        c["probe_success"] = Vec((L(("instance", "192.168.8.1"), ("job", "blackbox_icmp")), 1), (L(("instance", "https://slow.akunito.com"), ("job", "blackbox_http_2xx")), 1));
        c["up"] = Vec((L(("job", "node"), ("instance", "monitoring:9100")), 1));
        c["nas_zfs_pool_healthy"] = Vec((L(("pool", "ssdpool")), 1));
        c[$"node_filesystem_avail_bytes{{{Prometheus.FsFilter}}}"] = Vec((L(("instance", "monitoring:9100"), ("mountpoint", "/")), 50 * GiB));
        Dashboard calm = Dash(c);
        Assert.Equal("warn", calm.NetworkLevel);
        Assert.Equal("warn", calm.StorageLevel);
        Assert.Equal(0, calm.TargetsDown);
        Assert.Equal("err", calm.NodesLevel);
    }
}
