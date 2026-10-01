using AkuWM.Core.Config;
using AkuWM.Core.Nodes;
using AkuWM.Gui.Services;
using AkuWM.Gui.Shell;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace AkuWM.Gui.Sections;

/// <summary>Containers per node and daemon over ssh: state, health, limits, live usage, logs, and the compose-aware actions.</summary>
public sealed class DockerSection : Section
{
    private readonly ComboBox _node = new() { Width = 200 };
    private readonly ListBox _list = new() { Background = null };
    private readonly ContentControl _detail = new();
    private readonly TextBlock _note = Ui.Dim(string.Empty);
    private List<Container> _containers = [];
    private bool _loading;

    public DockerSection(AppServices services)
        : base(services)
    {
        _node.SelectionChanged += (_, _) => Load();
        _list.SelectionChanged += (_, _) =>
        {
            if (_list.SelectedItem is ListBoxItem { Tag: Container c })
            {
                ShowDetail(c);
            }
        };
    }

    public override string Key => "docker";

    public override string Title => "Docker";

    public override string Blurb => "containers on the nodes, over ssh";

    public override string Glyph => "▤";

    public IReadOnlyList<Container> Containers => _containers;

    /// <summary>"node/daemon" choices, from the enabled nodes with daemons.</summary>
    public List<string> Targets()
    {
        var targets = new List<string>();
        foreach (NodeConfig n in (Services.Config.Load().Effective.Nodes ?? []).Where(n => n.Enabled != false).OrderBy(n => n.Order ?? 100))
        {
            foreach (string d in n.Daemons ?? [])
            {
                targets.Add($"{n.Id}/{d}");
            }
        }

        return targets;
    }

    protected override Control Build()
    {
        var bar = Ui.Row(10, Ui.Field("node / daemon", _node), Ui.Button("Refresh", () => Load()), Ui.Button("Disk usage", ShowDf), _note);
        bar.Margin = new Thickness(0, 0, 0, 10);
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("380,16,*") };
        grid.Children.Add(new ScrollViewer { Content = _list });
        var right = Ui.Scroll(_detail);
        Grid.SetColumn(right, 2);
        grid.Children.Add(right);
        var dock = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        dock.Children.Add(bar);
        dock.Children.Add(grid);
        _detail.Content = Ui.Empty("Pick a container.");
        return dock;
    }

    public override void Shown()
    {
        string? current = _node.SelectedItem as string;
        List<string> targets = Targets();
        _node.ItemsSource = targets.Count > 0 ? targets : ["(no node has a docker daemon)"];
        _node.SelectedIndex = Math.Max(0, current is null ? 0 : targets.IndexOf(current));
    }

    private (NodeConfig Node, string Daemon)? Selected()
    {
        if (_node.SelectedItem is not string target || target.StartsWith('('))
        {
            return null;
        }

        string[] parts = target.Split('/');
        NodeConfig? node = (Services.Config.Load().Effective.Nodes ?? []).FirstOrDefault(n => n.Id == parts[0]);
        return node is null ? null : (node, parts[1]);
    }

    /// <summary>The status line under the toolbar.</summary>
    public string NoteText => _note.Text ?? string.Empty;

    private Task? _inflight;

    /// <returns>The render that follows the load, so a test can await it: the one in flight when a load is already running (Shown starts one), completed at once when nothing is selected.</returns>
    public Task Load()
    {
        if (_loading)
        {
            return _inflight ?? Task.CompletedTask;
        }

        if (Selected() is not (NodeConfig node, string daemon))
        {
            return Task.CompletedTask;
        }

        _loading = true;
        _note.Text = $"reading {node.Id}/{daemon}…";
        return _inflight = Task.Run(() => Docker.Containers(node, daemon)).ContinueWith(t =>
        {
            _loading = false;
            if (t.Status != TaskStatus.RanToCompletion)
            {
                _note.Text = t.Exception?.GetBaseException().Message ?? "failed";
                return;
            }

            _containers = t.Result;
            _note.Text = $"{_containers.Count} container(s) · {_containers.Count(c => c.State == "running")} running · {DateTime.Now:HH:mm:ss}";
            Render();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    public void Render()
    {
        _list.Items.Clear();
        foreach (IGrouping<string, Container> group in _containers.OrderBy(c => c.Project, StringComparer.Ordinal).ThenBy(c => c.Name, StringComparer.Ordinal).GroupBy(c => c.Project))
        {
            _list.Items.Add(new ListBoxItem { Content = Ui.Text(group.Key.Length == 0 ? "(no compose project)" : group.Key, "h2"), IsEnabled = false });
            foreach (Container c in group)
            {
                var chips = new List<Control> { Ui.Chip(c.State, c.State == "running" ? Palette.Foam : Palette.Love) };
                if (c.Health.Length > 0)
                {
                    chips.Add(Ui.Chip(c.Health, c.Health == "healthy" ? Palette.Foam : Palette.Gold));
                }

                if (c.CpuPct.Length > 0)
                {
                    chips.Add(Ui.Chip(c.CpuPct, Palette.Subtle));
                }

                if (c.MemPct.Length > 0)
                {
                    chips.Add(Ui.Chip(c.MemPct, Charts.LevelColour(Levels.Pct(Levels.ParsePct(c.MemPct)))));
                }

                _list.Items.Add(new ListBoxItem { Content = Ui.ItemRow(c.Name, c.Image, c.State == "running", [.. chips]), Tag = c });
            }
        }
    }

    private void ShowDetail(Container c)
    {
        if (Selected() is not (NodeConfig node, string daemon))
        {
            return;
        }

        var facts = new StackPanel { Spacing = 4 };
        foreach ((string k, string v) in new[]
        {
            ("id", c.Id), ("image", c.Image), ("status", c.Status), ("created", c.Created), ("ports", c.Ports),
            ("compose", c.Project.Length > 0 ? $"{c.Project} / {c.Service} · {c.WorkingDir} · {c.ConfigFiles}" : "not a compose service"),
            ("restart", c.RestartPolicy), ("limits", $"cpu {(c.CpuLimit > 0 ? c.CpuLimit.ToString("0.##") : "none")} · mem {(c.MemLimit > 0 ? Prometheus.Fmt(c.MemLimit, "bytes") : "none")}"),
            ("usage", $"cpu {c.CpuPct} · mem {c.MemUsage} ({c.MemPct}) · net {c.NetIo} · block {c.BlockIo} · pids {c.Pids}"),
        })
        {
            facts.Children.Add(Ui.Row(8, Ui.Text(k, "h2"), Ui.Mono(v)));
        }

        var mounts = new StackPanel { Spacing = 2 };
        foreach (Mount m in c.Mounts)
        {
            mounts.Children.Add(Ui.Mono($"{m.Type} {m.Source} → {m.Destination} ({m.Rw})"));
        }

        var logs = new TextBox { IsReadOnly = true, AcceptsReturn = true, FontFamily = new FontFamily("Cascadia Mono,Consolas,monospace"), FontSize = 11, MinHeight = 240, TextWrapping = TextWrapping.NoWrap };
        void LoadLogs() => Task.Run(() => Docker.Logs(node, daemon, c.Name, 300)).ContinueWith(t => logs.Text = t.Status == TaskStatus.RanToCompletion ? t.Result : t.Exception?.GetBaseException().Message, TaskScheduler.FromCurrentSynchronizationContext());
        var actions = new WrapPanel();
        foreach (string what in Docker.Actions)
        {
            bool dangerous = what is "down" or "recreate";
            var button = Ui.Button(what, () => Act(node, daemon, c, what), dangerous ? "danger" : "");
            button.Margin = new Thickness(0, 0, 6, 6);
            actions.Children.Add(button);
        }

        _detail.Content = Ui.Col(12,
            Ui.Text(c.Name, "h1"),
            Ui.Card(facts),
            Ui.Field("mounts", c.Mounts.Count > 0 ? mounts : Ui.Dim("none")),
            Ui.Field("actions", actions),
            Ui.Row(8, Ui.Text("logs (last 300 lines)", "h2"), Ui.Button("Load", LoadLogs)),
            logs);
    }

    private void Act(NodeConfig node, string daemon, Container c, string what)
    {
        _note.Text = $"{what} {c.Name}…";
        Task.Run(() => Docker.Action(node, daemon, c, what)).ContinueWith(t =>
        {
            bool ok = t.Status == TaskStatus.RanToCompletion;
            Toast(ok ? $"{what} {c.Name}: done" : t.Exception?.GetBaseException().Message ?? "failed", !ok);
            Load();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void ShowDf()
    {
        if (Selected() is not (NodeConfig node, string daemon))
        {
            return;
        }

        _note.Text = "docker system df…";
        Task.Run(() => Docker.Usage(node, daemon)).ContinueWith(t =>
        {
            if (t.Status != TaskStatus.RanToCompletion)
            {
                _note.Text = t.Exception?.GetBaseException().Message ?? "failed";
                return;
            }

            DiskUsage d = t.Result;
            _note.Text = d.Error ?? string.Empty;
            var rows = new StackPanel { Spacing = 3 };
            foreach (DfRow r in d.Summary)
            {
                rows.Children.Add(Ui.Mono($"{r.Type,-14} {r.TotalCount,4} total {r.Active,4} active  {r.Size,10}  reclaimable {r.Reclaimable}"));
            }

            var volumes = new StackPanel { Spacing = 2 };
            foreach (Volume v in d.Volumes)
            {
                volumes.Children.Add(Ui.Mono($"{v.Size,10}  {v.Name}  {(v.Links.Length > 0 ? "links " + v.Links : string.Empty)}"));
            }

            _detail.Content = Ui.Col(12, Ui.Text($"Disk usage on {node.Id}/{daemon}", "h1"), Ui.Card(rows), Ui.Field("volumes", volumes));
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }
}
