using AkuWM.Core.Nodes;
using AkuWM.Gui.Services;
using AkuWM.Gui.Shell;
using Avalonia;
using Avalonia.Controls;

namespace AkuWM.Gui.Sections;

/// <summary>Prometheus through the VPS in one round trip: nodes, storage, backups, network, targets, in tabs with red badges.</summary>
public sealed class MonitoringSection : Section
{
    private readonly TabControl _tabs = new();
    private readonly TextBlock _note = Ui.Dim(string.Empty);
    private readonly StackPanel _nodes = new() { Spacing = 10 };
    private readonly StackPanel _storage = new() { Spacing = 10 };
    private readonly StackPanel _backups = new() { Spacing = 10 };
    private readonly StackPanel _network = new() { Spacing = 10 };
    private readonly StackPanel _targets = new() { Spacing = 3 };
    private readonly Dictionary<string, TabItem> _items = new(StringComparer.Ordinal);
    private bool _loading;

    public MonitoringSection(AppServices services)
        : base(services)
    {
    }

    public override string Key => "monitoring";

    public override string Title => "Monitoring";

    public override string Blurb => "Prometheus over the VPS: nodes, storage, backups, network, targets";

    public override string Glyph => "◔";

    public Dashboard? Last { get; private set; }

    protected override Control Build()
    {
        foreach ((string key, string title, StackPanel body) in new[] { ("nodes", "Nodes", _nodes), ("storage", "Storage", _storage), ("backups", "Backups", _backups), ("network", "Network", _network), ("targets", "Targets", _targets) })
        {
            var item = new TabItem { Header = title, Content = Ui.Scroll(body) };
            _items[key] = item;
            _tabs.Items.Add(item);
        }

        var bar = Ui.Row(10, _note);
        bar.Margin = new Thickness(0, 0, 0, 8);
        var dock = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        dock.Children.Add(bar);
        dock.Children.Add(_tabs);
        return dock;
    }

    public override void Select(string id)
    {
        if (_items.TryGetValue(id, out TabItem? item))
        {
            _tabs.SelectedItem = item;
        }
    }

    public override void Refresh()
    {
        if (_loading)
        {
            return;
        }

        _loading = true;
        _note.Text = "asking Prometheus through the VPS (one ssh round trip)…";
        Core.Config.AkuWmConfig config = Services.Config.Load().Effective;
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Task.Run(() => Prometheus.Build(config, now)).ContinueWith(t =>
        {
            _loading = false;
            if (t.Status != TaskStatus.RanToCompletion)
            {
                _note.Text = t.Exception?.GetBaseException().Message ?? "failed";
                return;
            }

            Render(t.Result);
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    public void Render(Dashboard dash)
    {
        Last = dash;
        _note.Text = dash.Errors.Count > 0 ? string.Join(" · ", dash.Errors) : $"read {DateTimeOffset.FromUnixTimeSeconds(dash.Generated).ToLocalTime():HH:mm:ss} · Grafana: {dash.Grafana}";
        Badge("nodes", dash.Nodes.Count(c => c.Level == "err"));
        Badge("storage", dash.Zfs.Count(z => z.Level == "err") + dash.Nodes.SelectMany(c => c.Fs).Count(f => f.Level == "err"));
        Badge("backups", dash.Backups.Count(b => b.Level == "err"));
        Badge("network", dash.Network.Count(x => x.Level == "err"));
        Badge("targets", dash.TargetsDown);

        _nodes.Children.Clear();
        foreach (NodeCard c in dash.Nodes)
        {
            (Border card, StackPanel body) = Charts.Card(c.Name, c.Up is null ? "?" : c.Up.Value ? "up" : "DOWN", c.Up == false ? "err" : c.Level.Length > 0 ? c.Level : "ok");
            if (c.Lightweight)
            {
                body.Children.Add(Ui.Dim("no node_exporter metrics"));
            }
            else
            {
                body.Children.Add(Charts.Gauge("CPU load", c.LoadPct, c.LoadPct is null ? "—" : $"{c.LoadPct:0}% of {c.Ncpu:0} cores", c.LoadLevel));
                body.Children.Add(Charts.Sparkline(c.LoadSeries, c.LoadLevel, ymax: 100));
                body.Children.Add(Charts.Gauge("memory", c.MemPct, Levels.PctText(c.MemPct), c.MemLevel));
                body.Children.Add(Charts.Sparkline(c.MemSeries, c.MemLevel, ymax: 100));
                foreach (Filesystem f in c.Fs)
                {
                    body.Children.Add(Charts.Gauge(f.Mountpoint, f.UsedPct, Levels.PctText(f.UsedPct), f.Level, f.Text));
                }

                body.Children.Add(Ui.Dim("up " + c.UptimeText));
            }

            _nodes.Children.Add(card);
        }

        _storage.Children.Clear();
        foreach (ZfsPool z in dash.Zfs)
        {
            (Border card, StackPanel body) = Charts.Card("zfs " + z.Pool, z.Healthy ? "healthy" : "DEGRADED", z.Level);
            body.Children.Add(Charts.Gauge("used", z.UsedPct, Levels.PctText(z.UsedPct), z.Level, z.Text));
            _storage.Children.Add(card);
        }

        foreach ((string instance, List<Filesystem> list) in dash.OtherStorage)
        {
            (Border card, StackPanel body) = Charts.Card(instance);
            foreach (Filesystem f in list)
            {
                body.Children.Add(Charts.Gauge(f.Mountpoint, f.UsedPct, Levels.PctText(f.UsedPct), f.Level, f.Text));
            }

            _storage.Children.Add(card);
        }

        _backups.Children.Clear();
        foreach (IGrouping<string, Backup> group in dash.Backups.GroupBy(b => b.Group))
        {
            (Border card, StackPanel body) = Charts.Card(group.Key);
            foreach (Backup b in group)
            {
                double limit = b.Hourly ? Levels.HourlyErrS : Levels.BackupErrS;
                double? pct = b.AgeS is null ? null : Math.Min(100, 100 * b.AgeS.Value / limit);
                body.Children.Add(Charts.Gauge(b.Name, pct, b.AgeText + (b.SizeText.Length > 0 ? " · " + b.SizeText : string.Empty) + (b.Ok == false ? " · FAILED" : string.Empty), b.Level, b.Detail.Length > 0 ? b.Detail : null));
            }

            _backups.Children.Add(card);
        }

        _network.Children.Clear();
        foreach (Probe p in dash.Network)
        {
            (Border card, StackPanel body) = Charts.Card(p.Instance, p.Up ? p.Text : "DOWN", p.Level);
            if (p.Series.Count > 0)
            {
                body.Children.Add(Charts.Sparkline(p.Series, p.Level, height: 28));
            }

            body.Children.Add(Ui.Dim(p.Job));
            _network.Children.Add(card);
        }

        _targets.Children.Clear();
        foreach (Target t in dash.Targets)
        {
            _targets.Children.Add(Ui.Row(8, Ui.Chip(t.Up ? "up" : "down", Charts.LevelColour(t.Level)), Ui.Mono(t.Instance), Ui.Dim(t.Job)));
        }
    }

    private void Badge(string key, int count)
    {
        if (!_items.TryGetValue(key, out TabItem? item))
        {
            return;
        }

        string title = char.ToUpperInvariant(key[0]) + key[1..];
        item.Header = count == 0 ? title : Ui.Row(6, Ui.Text(title), Ui.Chip(count.ToString(), Palette.Love));
    }
}
