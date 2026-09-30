using System.Text.Json.Nodes;
using AkuWM.Core.Config;
using AkuWM.Core.Nodes;
using AkuWM.Gui.Services;
using AkuWM.Gui.Shell;
using Avalonia.Controls;

namespace AkuWM.Gui.Sections;

/// <summary>The infrastructure nodes: ssh targets, docker daemons, monitoring instance; probe and deploy.</summary>
public sealed class NodesSection : ItemSection
{
    private readonly Dictionary<string, (bool Ok, string Detail)> _probes = new(StringComparer.Ordinal);

    public NodesSection(AppServices services)
        : base(services)
    {
    }

    public override string Key => "nodes";

    public override string Title => "Nodes";

    public override string Blurb => "the machines: ssh, deploy, docker daemons, monitoring targets";

    public override string Glyph => "⌂";

    protected override string SectionKey => "nodes";

    protected override string IdPrefix => "n";

    protected override IEnumerable<Control> Toolbar()
    {
        yield return Ui.Button("Probe all", ProbeAll);
    }

    protected override Control Row(JsonObject item, string layer)
    {
        string id = Ui.Str(item, "id");
        string ssh = Ui.Str(item, "ssh");
        var chips = new List<Control> { LayerChip(layer) };
        if (item["daemons"] is JsonArray daemons)
        {
            foreach (JsonNode? d in daemons)
            {
                chips.Add(Ui.Chip(d?.ToString() ?? string.Empty, Palette.Pine));
            }
        }

        if (Ui.Str(item, "prometheus_instance").Length > 0)
        {
            chips.Add(Ui.Chip("prom", Palette.Rose));
        }

        if (_probes.TryGetValue(id, out (bool Ok, string Detail) probe))
        {
            chips.Add(Ui.Chip(probe.Ok ? "up" : "DOWN", probe.Ok ? Palette.Foam : Palette.Love));
        }

        return Ui.ItemRow(Ui.Str(item, "name").Length > 0 ? Ui.Str(item, "name") : id, ssh.Length > 0 ? ssh : "this machine", Ui.Flag(item, "enabled"), [.. chips]);
    }

    protected override JsonObject NewItem() => new() { ["id"] = string.Empty, ["name"] = string.Empty, ["profile"] = string.Empty, ["ssh"] = string.Empty, ["daemons"] = new JsonArray(), ["enabled"] = true, ["order"] = 100 };

    protected override Control Form(JsonObject draft)
    {
        if (draft["daemons"] is not JsonArray)
        {
            draft["daemons"] = new JsonArray();
        }

        var daemons = new WrapPanel();
        foreach (string daemon in ConfigDefaults.DockerDaemons)
        {
            var array = (JsonArray)draft["daemons"]!;
            var box = new CheckBox { Content = daemon, IsChecked = array.Any(n => n?.ToString() == daemon), Margin = new Avalonia.Thickness(0, 0, 14, 0) };
            box.IsCheckedChanged += (_, _) =>
            {
                for (int i = array.Count - 1; i >= 0; i--)
                {
                    if (array[i]?.ToString() == daemon)
                    {
                        array.RemoveAt(i);
                    }
                }

                if (box.IsChecked == true)
                {
                    array.Add(daemon);
                }
            };
            daemons.Children.Add(box);
        }

        TextBox id = Ui.Text(draft, "id", "VPS_PROD");
        id.Width = 180;
        id.IsEnabled = ConfigService.IdOf(draft) is null;
        return Ui.Col(14,
            Ui.Row(12, Ui.Field("id", id), Ui.Field("name", Wide(Ui.Text(draft, "name", "a name"))), Ui.Field("enabled", Ui.Check(draft, "enabled", "on"))),
            Ui.Row(12, Ui.Field("ssh", Wide(Ui.Text(draft, "ssh", "user@host[:port]; empty = this machine", mono: true))), Ui.Field("profile (deploy.sh --profile)", Wide(Ui.Text(draft, "profile", "VPS_PROD")))),
            Ui.Row(12, Ui.Field("docker daemons", daemons), Ui.Field("sudo for rootful", Ui.Check(draft, "sudo_rootful", "sudo -n docker", fallback: false)), Ui.Field("order", Ui.Number(draft, "order", 0, 1000, 100))),
            Ui.Field("prometheus instance (node_exporter label, no port)", Wide(Ui.Text(draft, "prometheus_instance", "monitoring", mono: true))),
            Ui.Field("notes", Ui.Notes(draft)),
            Ui.Dim(_probes.TryGetValue(ConfigService.IdOf(draft) ?? string.Empty, out (bool Ok, string Detail) p) ? (p.Ok ? "reachable: " : "unreachable: ") + p.Detail : "not probed yet"));
    }

    protected override IEnumerable<Control> Extras(JsonObject draft)
    {
        yield return Ui.Button("Probe", () => Probe(draft));
        yield return Ui.Button("Deploy…", () => Deploy(draft));
    }

    private static TextBox Wide(TextBox box)
    {
        box.Width = 300;
        return box;
    }

    private static NodeConfig ToNode(JsonObject item) =>
        System.Text.Json.JsonSerializer.Deserialize<NodeConfig>(item.ToJsonString(), ConfigJson.Options) ?? new NodeConfig();

    public (bool Ok, string Detail) Probe(JsonObject item)
    {
        NodeConfig node = ToNode(item);
        (bool ok, string detail) = NodeShell.Reachable(node);
        _probes[node.Id ?? string.Empty] = (ok, detail);
        Toast($"{node.Id}: {detail}", !ok);
        Refresh();
        return (ok, detail);
    }

    private void ProbeAll()
    {
        List<JsonObject> items = Items.Select(i => i.Item).Where(i => Ui.Flag(i, "enabled")).ToList();
        Task.Run(() =>
        {
            foreach (JsonObject item in items)
            {
                NodeConfig node = ToNode(item);
                _probes[node.Id ?? string.Empty] = NodeShell.Reachable(node);
            }
        }).ContinueWith(_ => { Toast("probed " + items.Count + " node(s)"); Refresh(); }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>
    /// deploy.sh (install.sh for this machine) in a terminal, from the WSL
    /// checkout: that is where the scripts and the keys are. Push first --
    /// the target resets to origin/main.
    /// </summary>
    public string DeployCommand(JsonObject item)
    {
        string profile = Ui.Str(item, "profile").Length > 0 ? Ui.Str(item, "profile") : Ui.Str(item, "id");
        bool local = Ui.Str(item, "ssh").Length == 0;
        string script = local
            ? $"cd ~/.dotfiles && ./install.sh ~/.dotfiles {profile} -s"
            : $"cd ~/.dotfiles && ./deploy.sh --profile {profile}";
        return $"{script}; echo; echo '--- deploy finished (exit '$?') --- press Enter to close'; read -r _";
    }

    private void Deploy(JsonObject item)
    {
        string inner = DeployCommand(item);
        string? error = Services.Start($"wt.exe --title \"Deploy {Ui.Str(item, "profile")}\" wsl.exe -- bash -lc {Quote(inner)}");
        Toast(error ?? $"deploy of {Ui.Str(item, "profile")} opened in a terminal (push your changes first: the target resets to origin/main)", error is not null);
    }

    private static string Quote(string s) => "\"" + s.Replace("\"", "\\\"") + "\"";
}
