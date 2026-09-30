using System.Globalization;
using System.Text.Json.Nodes;
using AkuWM.Core.Config;

namespace AkuWM.Core.Nodes;

public sealed record QuerySpec(string Q, int Range = 0, int Step = 300);

public sealed record Filesystem(string Mountpoint, double Size, double Avail, double UsedPct, string Level, string Text);

public sealed class NodeCard
{
    public string Node { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Instance { get; init; } = string.Empty;
    public bool? Up { get; init; }
    public string UpLevel { get; init; } = string.Empty;
    public double? Load1 { get; init; }
    public double? Ncpu { get; init; }
    public double? LoadPct { get; init; }
    public string LoadLevel { get; init; } = string.Empty;
    public double? MemPct { get; init; }
    public string MemLevel { get; init; } = string.Empty;
    public double? UptimeS { get; init; }
    public string UptimeText { get; init; } = string.Empty;
    public List<double?> LoadSeries { get; init; } = [];
    public List<double?> MemSeries { get; init; } = [];
    public List<Filesystem> Fs { get; set; } = [];
    public bool Lightweight { get; init; }
    public string Level { get; set; } = string.Empty;
}

public sealed record ZfsPool(string Pool, bool Healthy, double? Size, double? Alloc, double? UsedPct, string Level, string Text);

public sealed record Backup(string Group, string Name, double? AgeS, string AgeText, bool? Ok, double? Size, string SizeText, string Level, bool Hourly, string Detail);

public sealed record Probe(string Instance, string Job, string Kind, bool Up, double? RttMs, double? DurationS, double? Code, string Text, string Level, List<double?> Series);

public sealed record Target(string Job, string Instance, bool Up, string Level);

public sealed class Dashboard
{
    public List<Target> Targets { get; } = [];
    public List<NodeCard> Nodes { get; } = [];
    public List<ZfsPool> Zfs { get; } = [];
    public Dictionary<string, List<Filesystem>> OtherStorage { get; } = new(StringComparer.Ordinal);
    public List<Backup> Backups { get; } = [];
    public List<Probe> Network { get; } = [];
    public List<string> Errors { get; } = [];
    public string Grafana { get; set; } = string.Empty;
    public long Generated { get; set; }
    public int TargetsDown { get; set; }
    public string NodesLevel { get; set; } = string.Empty;
    public string StorageLevel { get; set; } = string.Empty;
    public string BackupsLevel { get; set; } = string.Empty;
    public string NetworkLevel { get; set; } = string.Empty;
    public bool HasSummary { get; set; }
}

/// <summary>
/// Prometheus queried on the node it runs on, over that node's ssh, every
/// query of a dashboard in ONE round trip (a shell line of curls, one JSON
/// per line). The port of sway-apps' monitoring; the query texts are the
/// same so its tests carry over.
/// </summary>
public static class Prometheus
{
    public const string DefaultNode = "VPS_PROD";
    public const string DefaultUrl = "http://localhost:9090";
    public const string DefaultGrafana = "https://grafana.akunito.com";
    public const int SparkRange = 6 * 3600;
    public const int SparkStep = 300;
    public const string FsFilter = "fstype!~\"tmpfs|overlay|squashfs|ramfs|devtmpfs|fuse.*\",mountpoint!~\"/nix/store|/boot.*|/run.*|/var/lib/docker.*\"";

    /// <summary>Joins label values into one key; no label value carries it.</summary>
    private const string Sep = "||";

    private static readonly (string Direction, string Group)[] BackupGroups =
    [
        ("vps_to_nas", "VPS → NAS"),
        ("workstation_to_nas", "Workstations → NAS"),
        ("nas_to_vps", "NAS → VPS (offsite)"),
    ];

    private static readonly Dictionary<string, string> DatasetNames = new(StringComparer.Ordinal)
    {
        ["vps_databases"] = "VPS databases",
        ["vps_services"] = "VPS services",
        ["vps_nextcloud"] = "VPS Nextcloud",
        ["desk_home"] = "DESK home",
        ["x13_home"] = "LAPTOP_X13 home",
        ["deska_home"] = "DESK_A home",
        ["laptopa_home"] = "LAPTOP_A home",
        ["offsite_configs"] = "NAS configs",
        ["offsite_data"] = "NAS data",
    };

    public static NodeConfig? PrometheusNode(AkuWmConfig config)
    {
        string id = config.Settings?.Monitoring?.PrometheusNode ?? DefaultNode;
        return (config.Nodes ?? []).FirstOrDefault(n => n.Id == id);
    }

    public static string Url(AkuWmConfig config) => config.Settings?.Monitoring?.PrometheusUrl ?? DefaultUrl;

    /// <summary>The shell line: one curl per spec, a newline after each so the replies line up.</summary>
    public static string Command(string url, IReadOnlyList<QuerySpec> specs, long now)
    {
        var parts = new List<string>(specs.Count);
        foreach (QuerySpec spec in specs)
        {
            string query = NodeShell.Quote("query=" + spec.Q);
            parts.Add(spec.Range > 0
                ? $"curl -s -m 20 {url}/api/v1/query_range --data-urlencode {query} --data-urlencode start={now - spec.Range} --data-urlencode end={now} --data-urlencode step={spec.Step}; echo"
                : $"curl -s -m 15 {url}/api/v1/query --data-urlencode {query}; echo");
        }

        return string.Join(" ; ", parts);
    }

    /// <summary>The <c>result</c> list per spec, an empty one where the line was not a success.</summary>
    public static List<JsonArray> Batch(AkuWmConfig config, IReadOnlyList<QuerySpec> specs, long now, int timeout = 60)
    {
        NodeConfig node = PrometheusNode(config) ?? throw new NodeException($"node {config.Settings?.Monitoring?.PrometheusNode ?? DefaultNode} (Prometheus host) is not defined");
        string output = NodeShell.Run(node, Command(Url(config), specs, now), timeout).Out;
        return Parse(output, specs.Count);
    }

    public static List<JsonArray> Parse(string output, int count)
    {
        var results = new List<JsonArray>(count);
        foreach (string raw in output.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            try
            {
                JsonNode? data = JsonNode.Parse(line);
                results.Add(data?["status"]?.ToString() == "success" && data["data"]?["result"] is JsonArray result ? result : []);
            }
            catch (System.Text.Json.JsonException)
            {
                results.Add([]);
            }
        }

        while (results.Count < count)
        {
            results.Add([]);
        }

        return results;
    }

    public static double? Scalar(JsonArray result)
    {
        if (result.Count == 0)
        {
            return null;
        }

        return result[0]?["value"] is JsonArray v && v.Count == 2 && double.TryParse(v[1]?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? d : null;
    }

    public static List<double?> Series(JsonArray result)
    {
        var series = new List<double?>();
        if (result.Count == 0 || result[0]?["values"] is not JsonArray values)
        {
            return series;
        }

        foreach (JsonNode? pair in values)
        {
            if (pair is JsonArray p && p.Count == 2 && double.TryParse(p[1]?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double d) && !double.IsNaN(d))
            {
                series.Add(d);
            }
            else
            {
                series.Add(null); // Prometheus "NaN" samples: the chart skips a gap, not a nan
            }
        }

        return series;
    }

    /// <summary>Value by label values.</summary>
    public static Dictionary<string, double> By(JsonArray result, params string[] labels)
    {
        var map = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (JsonNode? r in result)
        {
            if (r?["value"] is JsonArray v && v.Count == 2 && double.TryParse(v[1]?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
            {
                map[Key(r["metric"], labels)] = d;
            }
        }

        return map;
    }

    public static string Key(JsonNode? metric, params string[] labels) =>
        string.Join(Sep, labels.Select(l => metric?[l]?.ToString() ?? string.Empty));

    private static string[] Parts(string key) => key.Split(Sep);

    public static string Fmt(double? value, string unit)
    {
        if (value is null)
        {
            return "—";
        }

        double v = value.Value;
        switch (unit)
        {
            case "bool":
                return v >= 1 ? "UP" : "DOWN";
            case "pct":
                return $"{Math.Round(v):0}%";
            case "dur":
                {
                    long s = (long)v;
                    if (s < 0)
                    {
                        return "—";
                    }

                    long d = s / 86400, r = s % 86400, h = r / 3600, m = r % 3600 / 60;
                    return d > 0 ? $"{d}d {h}h" : h > 0 ? $"{h}h {m}m" : $"{m}m";
                }

            case "bytes":
                {
                    foreach (string u in new[] { "B", "KiB", "MiB", "GiB", "TiB" })
                    {
                        if (v < 1024)
                        {
                            return $"{v.ToString("0.0", CultureInfo.InvariantCulture)} {u}";
                        }

                        v /= 1024;
                    }

                    return $"{v.ToString("0.0", CultureInfo.InvariantCulture)} PiB";
                }

            default:
                return v.ToString("G", CultureInfo.InvariantCulture);
        }
    }

    /// <summary>Everything the Monitoring section shows, one round trip, levels computed here.</summary>
    public static Dashboard Build(AkuWmConfig config, long now)
    {
        var dash = new Dashboard { Grafana = config.Settings?.Monitoring?.GrafanaUrl ?? DefaultGrafana, Generated = now };
        List<NodeConfig> nodes = (config.Nodes ?? []).Where(n => n.Enabled != false && !string.IsNullOrEmpty(n.PrometheusInstance)).OrderBy(n => n.Order ?? 100).ToList();
        var specs = new List<QuerySpec> { new("up") };
        foreach (NodeConfig n in nodes)
        {
            string i = n.PrometheusInstance!;
            string sel = $"instance=~\"{i}(:.*)?\"";
            specs.Add(new($"max(up{{instance=\"{i}\"}})"));
            specs.Add(new($"node_load1{{{sel}}}"));
            specs.Add(new($"count(node_cpu_seconds_total{{{sel},mode=\"idle\"}})"));
            specs.Add(new($"100 * (1 - node_memory_MemAvailable_bytes{{{sel}}} / node_memory_MemTotal_bytes{{{sel}}})"));
            specs.Add(new($"time() - node_boot_time_seconds{{{sel}}}"));
            specs.Add(new($"100 * node_load1{{{sel}}} / scalar(count(node_cpu_seconds_total{{{sel},mode=\"idle\"}}))", SparkRange, SparkStep));
            specs.Add(new($"100 * (1 - node_memory_MemAvailable_bytes{{{sel}}} / node_memory_MemTotal_bytes{{{sel}}})", SparkRange, SparkStep));
        }

        specs.AddRange(
        [
            new($"node_filesystem_size_bytes{{{FsFilter}}}"),
            new($"node_filesystem_avail_bytes{{{FsFilter}}}"),
            new("nas_zfs_pool_healthy"), new("nas_zfs_pool_size_bytes"), new("nas_zfs_pool_allocated_bytes"),
            new("time() - nas_backup_last_success"), new("nas_backup_status"), new("backup_repo_size_bytes"),
            new("time() - nas_offsite_backup_last_success"), new("nas_offsite_backup_status"),
            new("time() - mariadb_backup_daily_last_success_timestamp"), new("mariadb_backup_daily_status"),
            new("time() - mariadb_backup_hourly_last_success_timestamp"), new("mariadb_backup_hourly_status"),
            new("time() - postgresql_backup_daily_last_success_timestamp"), new("postgresql_backup_daily_status"),
            new("time() - postgresql_backup_hourly_last_success_timestamp"), new("postgresql_backup_hourly_status"),
            new("pfsense_backup_last_success"), new("pfsense_backup_status"),
            new("probe_success"), new("probe_icmp_duration_seconds{phase=\"rtt\"}"), new("probe_duration_seconds{job=~\"blackbox_http.*\"}"), new("probe_http_status_code"),
            new("avg by (instance) (probe_icmp_duration_seconds{phase=\"rtt\"}) * 1000", SparkRange, SparkStep),
        ]);

        List<JsonArray> r;
        try
        {
            r = Batch(config, specs, now);
        }
        catch (NodeException ex)
        {
            dash.Errors.Add(ex.Message);
            return dash;
        }

        int k = 0;
        JsonArray Next() => r[k++];

        foreach (JsonNode? x in Next())
        {
            bool up = x?["value"]?[1]?.ToString() == "1";
            dash.Targets.Add(new Target(x?["metric"]?["job"]?.ToString() ?? string.Empty, x?["metric"]?["instance"]?.ToString() ?? string.Empty, up, up ? "ok" : "err"));
        }

        dash.Targets.Sort((a, b) => a.Up != b.Up ? a.Up.CompareTo(b.Up) : string.CompareOrdinal(a.Job, b.Job));

        foreach (NodeConfig n in nodes)
        {
            double? up = Scalar(Next());
            double? load1 = Scalar(Next());
            double? ncpu = Scalar(Next());
            double? mem = Scalar(Next());
            double? upt = Scalar(Next());
            List<double?> loadSeries = Series(Next());
            List<double?> memSeries = Series(Next());
            double? loadPct = load1 is not null && ncpu is > 0 ? 100 * load1 / ncpu : null;
            bool? isUp = up is null ? null : up >= 1;
            dash.Nodes.Add(new NodeCard
            {
                Node = n.Id ?? string.Empty,
                Name = n.Name ?? n.Id ?? string.Empty,
                Instance = n.PrometheusInstance!,
                Up = isUp,
                UpLevel = Levels.Flag(isUp),
                Load1 = load1,
                Ncpu = ncpu,
                LoadPct = loadPct,
                LoadLevel = Levels.Load(loadPct),
                MemPct = mem,
                MemLevel = Levels.Pct(mem),
                UptimeS = upt,
                UptimeText = Fmt(upt, "dur"),
                LoadSeries = loadSeries,
                MemSeries = memSeries,
                Lightweight = load1 is null && mem is null,
            });
        }

        Dictionary<string, double> size = By(Next(), "instance", "mountpoint");
        Dictionary<string, double> avail = By(Next(), "instance", "mountpoint");
        var byInstance = new Dictionary<string, List<Filesystem>>(StringComparer.Ordinal);
        foreach ((string key, double sz) in size.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (sz <= 0 || !avail.TryGetValue(key, out double av))
            {
                continue;
            }

            string[] parts = Parts(key);
            string instance = parts[0].Split(':')[0];
            double pct = 100 * (1 - av / sz);
            if (!byInstance.TryGetValue(instance, out List<Filesystem>? list))
            {
                byInstance[instance] = list = [];
            }

            list.Add(new Filesystem(parts[1], sz, av, pct, Levels.Pct(pct), $"{Fmt(sz - av, "bytes")} / {Fmt(sz, "bytes")} · {Fmt(av, "bytes")} free"));
        }

        foreach (NodeCard card in dash.Nodes)
        {
            card.Fs = byInstance.GetValueOrDefault(card.Instance) ?? [];
            card.Level = Levels.Worst([card.Up == false ? card.UpLevel : string.Empty, card.LoadLevel, card.MemLevel, .. card.Fs.Select(f => f.Level)]);
        }

        var nodeInstances = new HashSet<string>(dash.Nodes.Select(c => c.Instance), StringComparer.Ordinal);
        foreach ((string instance, List<Filesystem> list) in byInstance)
        {
            if (!nodeInstances.Contains(instance))
            {
                dash.OtherStorage[instance] = list;
            }
        }

        Dictionary<string, double> zh = By(Next(), "pool");
        Dictionary<string, double> zs = By(Next(), "pool");
        Dictionary<string, double> za = By(Next(), "pool");
        foreach ((string pool, double healthy) in zh.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            double? sz = zs.TryGetValue(pool, out double s) ? s : null;
            double? al = za.TryGetValue(pool, out double a) ? a : null;
            double? pct = sz is > 0 && al is not null ? 100 * al / sz : null;
            dash.Zfs.Add(new ZfsPool(pool, healthy >= 1, sz, al, pct, Levels.Worst(Levels.Flag(healthy >= 1), Levels.Pct(pct)), sz is not null ? $"{Fmt(al, "bytes")} / {Fmt(sz, "bytes")}" : "—"));
        }

        Dictionary<string, double> ages = By(Next(), "dataset");
        Dictionary<string, double> status = By(Next(), "dataset");
        Dictionary<string, double> sizes = By(Next(), "dataset", "direction");
        Dictionary<string, double> offAge = By(Next(), "exported_job");
        Dictionary<string, double> offStatus = By(Next(), "exported_job");

        void AddBackup(string group, string name, double? ageS, bool? ok, double? sizeB = null, bool hourly = false, string detail = "")
        {
            string level = Levels.Worst(Levels.Age(ageS, hourly), ok == false ? "err" : string.Empty);
            dash.Backups.Add(new Backup(group, name, ageS, Fmt(ageS, "dur"), ok, sizeB, sizeB is not null ? Fmt(sizeB, "bytes") : string.Empty, level, hourly, detail));
        }

        foreach ((string direction, string group) in BackupGroups)
        {
            if (direction == "nas_to_vps")
            {
                var jobs = new SortedSet<string>(offAge.Keys.Concat(offStatus.Keys), StringComparer.Ordinal);
                foreach (string job in jobs)
                {
                    double? sz = sizes.TryGetValue($"offsite_{job}{Sep}{direction}", out double s) ? s : null;
                    bool? ok = offStatus.TryGetValue(job, out double st) ? st >= 1 : null;
                    AddBackup(group, DatasetNames.GetValueOrDefault($"offsite_{job}") ?? job, offAge.TryGetValue(job, out double ag) ? ag : null, ok, sz, detail: "restic on the VPS");
                }

                continue;
            }

            foreach ((string key, double sz) in sizes.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                string[] parts = Parts(key);
                if (parts[1] != direction)
                {
                    continue;
                }

                string ds = parts[0];
                bool? ok = status.TryGetValue(ds, out double st) ? st >= 1 : null;
                AddBackup(group, DatasetNames.GetValueOrDefault(ds) ?? ds, ages.TryGetValue(ds, out double ag) ? ag : null, ok, sz, detail: "restic repo on the NAS");
            }
        }

        foreach ((string label, bool hourly) in new[] { ("MariaDB daily", false), ("MariaDB hourly", true), ("PostgreSQL daily", false), ("PostgreSQL hourly", true) })
        {
            double? a = Scalar(Next());
            double? st = Scalar(Next());
            AddBackup("Databases (VPS)", label, a, st is null ? null : st >= 1, hourly: hourly);
        }

        double? pts = Scalar(Next());
        double? ps = Scalar(Next());
        double? pa = pts is > 0 ? now - pts : null;
        if (pa is null && (ps ?? 0) < 1)
        {
            AddBackup("pfSense", "config backup", null, false, detail: "no backup file found (never ran or unreachable)");
        }
        else
        {
            AddBackup("pfSense", "config backup", pa, ps is null ? null : ps >= 1, detail: (ps ?? 0) >= 1 ? string.Empty : "last pull from pfSense FAILED; age = newest file kept on the VPS");
        }

        Dictionary<string, double> succ = By(Next(), "instance", "job");
        Dictionary<string, double> rtt = By(Next(), "instance");
        Dictionary<string, double> hdur = By(Next(), "instance");
        Dictionary<string, double> hcode = By(Next(), "instance");
        var rttSeries = new Dictionary<string, List<double?>>(StringComparer.Ordinal);
        foreach (JsonNode? x in Next())
        {
            rttSeries[x?["metric"]?["instance"]?.ToString() ?? string.Empty] = Series([x?.DeepClone()]);
        }

        foreach ((string key, double ok) in succ.OrderBy(kv => Parts(kv.Key)[1].Contains("icmp") ? 0 : 1).ThenBy(kv => Parts(kv.Key)[0], StringComparer.Ordinal))
        {
            string[] parts = Parts(key);
            string instance = parts[0], job = parts[1];
            bool up = ok >= 1;
            if (job.Contains("icmp"))
            {
                double? ms = rtt.TryGetValue(instance, out double s) ? s * 1000 : null;
                string level = Levels.Worst(Levels.Flag(up), up ? Levels.Rtt(ms) : string.Empty);
                dash.Network.Add(new Probe(instance, job, "icmp", up, ms, null, null, ms is not null ? $"{ms.Value.ToString("0.0", CultureInfo.InvariantCulture)} ms" : "—", level, rttSeries.GetValueOrDefault(instance) ?? []));
            }
            else
            {
                double? d = hdur.TryGetValue(instance, out double dd) ? dd : null;
                double? code = hcode.TryGetValue(instance, out double c) ? c : null;
                string level = Levels.Worst(Levels.Flag(up), up ? Levels.Http(d) : string.Empty);
                string text = (code is not null ? $"HTTP {(int)code} · " : string.Empty) + (d is not null ? $"{d.Value.ToString("0.00", CultureInfo.InvariantCulture)} s" : "—");
                dash.Network.Add(new Probe(instance, job, "http", up, null, d, code, text, level, []));
            }
        }

        dash.TargetsDown = dash.Targets.Count(t => !t.Up);
        dash.NodesLevel = Levels.Worst([.. dash.Nodes.Select(c => c.Level)]);
        dash.StorageLevel = Levels.Worst([.. dash.Zfs.Select(z => z.Level), .. dash.Nodes.SelectMany(c => c.Fs).Select(f => f.Level)]);
        dash.BackupsLevel = Levels.Worst([.. dash.Backups.Select(b => b.Level)]);
        dash.NetworkLevel = Levels.Worst([.. dash.Network.Select(x => x.Level)]);
        dash.HasSummary = true;
        return dash;
    }
}
