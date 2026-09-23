using System.Text.Json;
using System.Text.Json.Nodes;
using AkuWM.Core.Config;
using AkuWM.Core.Ipc;
using AkuWM.Core.Matching;
using AkuWM.Gui.Services;
using AkuWM.Gui.Shell;
using Avalonia;
using Avalonia.Controls;

namespace AkuWM.Gui.Sections;

public sealed class RulesSection : ItemSection
{
    private readonly RuleMatcher _matcher = new();

    public RulesSection(AppServices services)
        : base(services)
    {
    }

    public override string Key => "rules";

    public override string Title => "Rules";

    public override string Blurb => "what happens to a window when it appears";

    public override string Glyph => "≡";

    protected override string SectionKey => "rules";

    protected override string IdPrefix => "r";

    protected override Control Row(JsonObject item, string layer)
    {
        var chips = new List<Control> { LayerChip(layer) };
        if (item["actions"] is JsonArray actions)
        {
            foreach (JsonNode? action in actions)
            {
                if (action is JsonValue v && chips.Count < 4)
                {
                    chips.Add(Ui.Chip(v.ToString(), Palette.Gold));
                }
            }
        }

        return Ui.ItemRow(Ui.Str(item, "name"), MatchSummary(item), Ui.Flag(item, "enabled"), [.. chips]);
    }

    public static string MatchSummary(JsonObject item)
    {
        if (item["match"] is not JsonArray alternatives || alternatives.Count == 0)
        {
            return "(matches nothing)";
        }

        var parts = new List<string>();
        foreach (JsonNode? node in alternatives)
        {
            if (node is JsonObject alt)
            {
                var one = new List<string>(3);
                foreach (string key in (string[])["process", "class", "title"])
                {
                    string value = Ui.Str(alt, key);
                    if (value.Length > 0)
                    {
                        one.Add($"{key}={value}");
                    }
                }

                parts.Add(string.Join(' ', one));
            }
        }

        return string.Join(" | ", parts);
    }

    protected override JsonObject NewItem() => new()
    {
        ["name"] = string.Empty,
        ["enabled"] = true,
        ["match"] = new JsonArray(new JsonObject()),
        ["actions"] = new JsonArray(),
    };

    protected override Control Form(JsonObject draft)
    {
        if (draft["match"] is not JsonArray)
        {
            draft["match"] = new JsonArray();
        }

        if (draft["actions"] is not JsonArray)
        {
            draft["actions"] = new JsonArray();
        }

        var alternatives = new StackPanel { Spacing = 6 };
        void FillAlternatives()
        {
            alternatives.Children.Clear();
            var array = (JsonArray)draft["match"]!;
            for (int i = 0; i < array.Count; i++)
            {
                if (array[i] is not JsonObject alt)
                {
                    continue;
                }

                int index = i;
                var row = Ui.Row(6,
                    Ui.Text(alt, "process", "process (Telegram, re:^Zen)", mono: true),
                    Ui.Text(alt, "class", "class", mono: true),
                    Ui.Text(alt, "title", "title", mono: true),
                    Ui.Button("×", () => { array.RemoveAt(index); FillAlternatives(); }));
                foreach (Control c in row.Children)
                {
                    if (c is TextBox box)
                    {
                        box.Width = 200;
                    }
                }

                alternatives.Children.Add(row);
            }
        }

        FillAlternatives();

        var picker = new Expander { Header = "pick from the open windows", IsExpanded = false };
        picker.Expanding += (_, _) => picker.Content = WindowPicker(alt =>
        {
            ((JsonArray)draft["match"]!).Add(alt);
            FillAlternatives();
            picker.IsExpanded = false;
        });

        var actions = new WrapPanel();
        foreach (string action in ConfigDefaults.RuleActions)
        {
            var array = (JsonArray)draft["actions"]!;
            var box = new CheckBox { Content = action, IsChecked = Has(array, action), Margin = new Thickness(0, 0, 14, 0) };
            box.IsCheckedChanged += (_, _) =>
            {
                for (int i = array.Count - 1; i >= 0; i--)
                {
                    if (array[i] is JsonValue v && v.ToString() == action)
                    {
                        array.RemoveAt(i);
                    }
                }

                if (box.IsChecked == true)
                {
                    array.Add(action);
                }
            };
            actions.Children.Add(box);
        }

        var target = draft["target"] as JsonObject ?? new JsonObject();
        draft["target"] = target;
        var roles = new List<string> { string.Empty };
        foreach (MonitorConfig monitor in Services.Config.Load().Effective.Monitors ?? [])
        {
            if (monitor.Id is { Length: > 0 } role)
            {
                roles.Add(role);
            }
        }

        var result = Ui.Dim("not tested yet");
        var test = Ui.Button("Test against the open windows", () => result.Text = TestAgainstOpenWindows(draft));

        return Ui.Col(14,
            Ui.Row(12, Ui.Field("name", WithWidth(Ui.Text(draft, "name", "a name for the list"), 320)), Ui.Field("enabled", Ui.Check(draft, "enabled", "on"))),
            Ui.Field("match (any alternative)", Ui.Col(6, alternatives, Ui.Row(8, Ui.Button("Add an alternative", () => { ((JsonArray)draft["match"]!).Add(new JsonObject()); FillAlternatives(); }), picker)),
                "process / class / title, plain text (contains) or re:<regex>"),
            Ui.Field("actions", actions),
            Ui.Field("target", Ui.Row(12,
                Ui.Field("monitor", WithWidth(Ui.Combo(target, "monitor", [.. roles]), 160)),
                Ui.Field("slot", Ui.Number(target, "slot", 1, 10)),
                Ui.Field("workspace", WithWidth(Ui.Text(target, "workspace", "name"), 120)))),
            Ui.Field("notes", Ui.Notes(draft)),
            Ui.Card(Ui.Col(8, test, result)));
    }

    private static TextBox WithWidth(TextBox box, double width)
    {
        box.Width = width;
        return box;
    }

    private static ComboBox WithWidth(ComboBox box, double width)
    {
        box.Width = width;
        return box;
    }

    private static bool Has(JsonArray array, string value)
    {
        foreach (JsonNode? node in array)
        {
            if (node is JsonValue v && v.ToString() == value)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The engine's own matcher over the daemon's own list: what "would this rule fire" means.</summary>
    public string TestAgainstOpenWindows(JsonObject draft)
    {
        RuleConfig? rule;
        try
        {
            rule = JsonSerializer.Deserialize<RuleConfig>(draft.ToJsonString(), ConfigJson.Options);
        }
        catch (JsonException ex)
        {
            return $"the rule does not parse: {ex.Message}";
        }

        if (rule is null)
        {
            return "the rule is empty";
        }

        List<JsonObject> windows = OpenWindows();
        if (windows.Count == 0)
        {
            return Services.Daemon.IsRunning() ? "no windows" : "AkuWM is not running";
        }

        var hits = new List<string>();
        foreach (JsonObject window in windows)
        {
            if (_matcher.Matches(rule, Facts(window)))
            {
                hits.Add($"{Ui.Str(window, "processName")}  [{Ui.Str(window, "className")}]  {Ui.Str(window, "title")}");
            }
        }

        return hits.Count == 0 ? $"matches none of {windows.Count} windows" : $"matches {hits.Count} of {windows.Count}:\n" + string.Join('\n', hits);
    }

    public static WindowFacts Facts(JsonObject window) =>
        new(RuleMatcher.StripExe(Ui.Str(window, "processName")), Ui.Str(window, "className"), Ui.Str(window, "title"));

    public List<JsonObject> OpenWindows()
    {
        var windows = new List<JsonObject>();
        CommandResponse reply = Services.Daemon.Send("query windows --all");
        JsonNode? list = reply.Data is JsonObject o ? o["windows"] : reply.Data;
        if (reply.Success && list is JsonArray array)
        {
            foreach (JsonNode? node in array)
            {
                if (node is JsonObject window && Ui.Str(window, "title").Length > 0)
                {
                    windows.Add(window);
                }
            }
        }

        return windows;
    }

    private Control WindowPicker(Action<JsonObject> use)
    {
        List<JsonObject> windows = OpenWindows();
        if (windows.Count == 0)
        {
            return Ui.Dim("no windows to pick from");
        }

        var col = new StackPanel { Spacing = 4 };
        foreach (JsonObject window in windows)
        {
            string process = RuleMatcher.StripExe(Ui.Str(window, "processName"));
            string cls = Ui.Str(window, "className");
            string title = Ui.Str(window, "title");
            col.Children.Add(Ui.Row(8,
                Ui.Button("process", () => use(new JsonObject { ["process"] = process })),
                Ui.Button("class", () => use(new JsonObject { ["class"] = cls })),
                Ui.Button("title", () => use(new JsonObject { ["title"] = title })),
                Ui.Mono($"{process}  [{cls}]  {title}")));
        }

        return Ui.Scroll(col);
    }

    /// <summary>The smoke's proof that an edit made here is live: save, seen by the daemon, removed, gone.</summary>
    public IEnumerable<string> RoundTrip()
    {
        const string name = "akuwm-gui-smoke";
        var rule = new JsonObject
        {
            ["name"] = name,
            ["enabled"] = true,
            ["match"] = new JsonArray(new JsonObject { ["process"] = "akuwm-smoke-never-runs" }),
            ["actions"] = new JsonArray("float"),
            ["notes"] = "written by akuwm-gui --smoke; deleted by it too",
        };
        Edit(rule, "common");
        string? saved = Save();
        yield return (saved is null ? "PASS" : "FAIL") + " rule saved through the editor" + (saved is null ? string.Empty : ": " + saved);
        string id = ConfigService.IdOf(rule) ?? string.Empty;
        bool seen = DaemonHasRule(id);
        yield return (seen ? "PASS" : "FAIL") + $" the daemon lists the rule after the apply ({id})";
        string? removed = Delete();
        yield return (removed is null ? "PASS" : "FAIL") + " rule deleted through the editor" + (removed is null ? string.Empty : ": " + removed);
        yield return (DaemonHasRule(id) ? "FAIL" : "PASS") + " the daemon no longer lists it";
    }

    /// <summary><c>rules list</c> carries ids, not names.</summary>
    private bool DaemonHasRule(string id)
    {
        CommandResponse reply = Services.Daemon.Send("rules list");
        return reply.Success && reply.Data?.ToJsonString().Contains($"\"id\":\"{id}\"", StringComparison.Ordinal) == true;
    }
}
