using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AkuWM.Core.Bindings;
using AkuWM.Core.Config;
using AkuWM.Gui.Services;
using AkuWM.Gui.Shell;
using Avalonia.Controls;

namespace AkuWM.Gui.Sections;

public sealed class ShortcutsSection : ItemSection
{
    private Dictionary<string, List<string>> _conflicts = new(StringComparer.Ordinal);

    public ShortcutsSection(AppServices services)
        : base(services)
    {
    }

    public override string Key => "shortcuts";

    public override string Title => "Shortcuts";

    public override string Blurb => "the chords the AutoHotkey script binds, live after apply";

    public override string Glyph => "⌘";

    protected override string SectionKey => "shortcuts";

    protected override string IdPrefix => "k";

    protected override bool ReloadBindings => true;

    public override void Refresh()
    {
        base.Refresh();
        _conflicts = Conflicts(Items.Select(i => i.Item).ToList());
    }

    protected override IEnumerable<Control> Toolbar()
    {
        yield return Ui.Button("Overview", () => ShowInEditor(Overview()));
    }

    protected override Control Row(JsonObject item, string layer)
    {
        string keys = Ui.Str(item, "keys");
        string kind = Ui.Str(item, "kind");
        var chips = new List<Control> { LayerChip(layer), Ui.Chip(kind, kind == "wm" ? Palette.Foam : kind == "app" ? Palette.Gold : Palette.Rose) };
        string category = Ui.Str(item, "category");
        if (category.Length > 0)
        {
            chips.Add(Ui.Chip(category, Palette.Subtle));
        }

        string? chord = Bindings.ChordOf(keys);
        if (chord is not null && _conflicts.TryGetValue(chord, out List<string>? ids) && ids.Count > 1 && Ui.Flag(item, "enabled"))
        {
            chips.Add(Ui.Chip("conflict", Palette.Love));
        }

        string name = Ui.Str(item, "name");
        string sub = name.Length > 0 ? name : Ui.Str(item, "command");
        return Ui.ItemRow(keys, sub, Ui.Flag(item, "enabled"), [.. chips]);
    }

    protected override JsonObject NewItem() => new() { ["keys"] = "Hyper+", ["kind"] = "exec", ["command"] = string.Empty, ["enabled"] = true };

    protected override Control Form(JsonObject draft)
    {
        var when = draft["when"] as JsonObject ?? new JsonObject();
        draft["when"] = when;
        var chordInfo = Ui.Dim(ChordInfo(draft));
        TextBox keys = Ui.Text(draft, "keys", "Hyper+Shift+X", mono: true);
        keys.Width = 220;
        keys.TextChanged += (_, _) => chordInfo.Text = ChordInfo(draft);
        return Ui.Col(14,
            Ui.Row(12, Ui.Field("keys", Ui.Col(2, keys, chordInfo)), Ui.Field("kind", Ui.Combo(draft, "kind", ConfigDefaults.ShortcutKinds, "exec"), "app = raise/launch a process · wm = an AkuWM command · exec = a program · send = inject a chord"), Ui.Field("enabled", Ui.Check(draft, "enabled", "on"))),
            Ui.Field("command", Ui.Text(draft, "command", "the program, or the wm command (workspace next)", mono: true)),
            Ui.Row(12, Ui.Field("app (process to raise)", Wide(Ui.Text(draft, "app", "Telegram.exe"))), Ui.Field("only in (process)", Wide(Ui.Text(when, "process", "WindowsTerminal")))),
            Ui.Row(12, Ui.Field("name", Wide(Ui.Text(draft, "name", "what it does"))), Ui.Field("category", Wide(Ui.Text(draft, "category", "Apps, Workspaces...")))),
            Ui.Field("notes", Ui.Notes(draft)));
    }

    private string ChordInfo(JsonObject draft)
    {
        string keys = Ui.Str(draft, "keys");
        string? chord = Bindings.ChordOf(keys);
        if (chord is null)
        {
            return "not a chord yet (Hyper, Ctrl, Alt, Win, Shift + a key)";
        }

        string mine = ConfigService.IdOf(draft) ?? string.Empty;
        if (_conflicts.TryGetValue(chord, out List<string>? ids) && ids.Any(id => id != mine))
        {
            return $"AutoHotkey {chord} · also bound by {string.Join(", ", ids.Where(id => id != mine))}";
        }

        return $"AutoHotkey {chord}";
    }

    private Control Overview()
    {
        List<JsonObject> items = Items.Select(i => i.Item).ToList();
        var problems = new List<string>();
        Bindings.Render(Typed(items), problems);
        var conflictLines = new StringBuilder();
        foreach ((string chord, List<string> ids) in _conflicts)
        {
            if (ids.Count > 1)
            {
                conflictLines.Append(chord).Append("  ←  ").Append(string.Join(", ", ids)).Append('\n');
            }
        }

        var sheet = new TextBox { Text = CheatSheet(items), IsReadOnly = true, AcceptsReturn = true, FontFamily = new Avalonia.Media.FontFamily("Cascadia Mono,Consolas,monospace"), MinHeight = 320 };
        return Ui.Col(14,
            Ui.Field("conflicts (two enabled shortcuts on one chord)", conflictLines.Length == 0 ? Ui.Dim("none") : Ui.Mono(conflictLines.ToString().TrimEnd())),
            Ui.Field("what the renderer refuses", problems.Count == 0 ? Ui.Dim("nothing") : Ui.Mono(string.Join('\n', problems))),
            Ui.Field("free Hyper+<key>", Ui.Mono(string.Join(' ', FreeHyperKeys(items)))),
            Ui.Field("cheat sheet", sheet));
    }

    public static List<ShortcutConfig> Typed(List<JsonObject> items)
    {
        var typed = new List<ShortcutConfig>(items.Count);
        foreach (JsonObject item in items)
        {
            try
            {
                if (JsonSerializer.Deserialize<ShortcutConfig>(item.ToJsonString(), ConfigJson.Options) is { } s)
                {
                    typed.Add(s);
                }
            }
            catch (JsonException)
            {
                // A half-typed item is not a shortcut yet.
            }
        }

        return typed;
    }

    /// <summary>Chord → ids of the enabled shortcuts bound to it (the AutoHotkey spelling, so Hyper+q and Hyper+Q collide).</summary>
    public static Dictionary<string, List<string>> Conflicts(List<JsonObject> items)
    {
        var byChord = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (JsonObject item in items)
        {
            if (!Ui.Flag(item, "enabled") || Bindings.ChordOf(Ui.Str(item, "keys")) is not { } chord)
            {
                continue;
            }

            string when = Ui.Str(item["when"] as JsonObject ?? new JsonObject(), "process");
            string key = when.Length == 0 ? chord : chord + "@" + when.ToLowerInvariant();
            if (!byChord.TryGetValue(key, out List<string>? ids))
            {
                byChord[key] = ids = [];
            }

            ids.Add(ConfigService.IdOf(item) ?? "(no id)");
        }

        return byChord;
    }

    public static List<string> FreeHyperKeys(List<JsonObject> items)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonObject item in items)
        {
            if (Bindings.ChordOf(Ui.Str(item, "keys")) is { } chord && chord.StartsWith("^!#", StringComparison.Ordinal))
            {
                used.Add(chord);
            }
        }

        var free = new List<string>();
        foreach (char c in "abcdefghijklmnopqrstuvwxyz0123456789")
        {
            if (!used.Contains("^!#" + c))
            {
                free.Add("Hyper+" + char.ToUpperInvariant(c));
            }

            if (!used.Contains("^!#+" + c))
            {
                free.Add("Hyper+Shift+" + char.ToUpperInvariant(c));
            }
        }

        return free;
    }

    public static string CheatSheet(List<JsonObject> items)
    {
        var byCategory = new SortedDictionary<string, List<(string Keys, string What)>>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonObject item in items)
        {
            if (!Ui.Flag(item, "enabled"))
            {
                continue;
            }

            string category = Ui.Str(item, "category");
            if (category.Length == 0)
            {
                category = "Other";
            }

            if (!byCategory.TryGetValue(category, out List<(string, string)>? list))
            {
                byCategory[category] = list = [];
            }

            string what = Ui.Str(item, "name");
            if (what.Length == 0)
            {
                what = Ui.Str(item, "command");
            }

            list.Add((Ui.Str(item, "keys"), what));
        }

        var text = new StringBuilder();
        foreach ((string category, List<(string Keys, string What)> list) in byCategory)
        {
            text.Append("## ").Append(category).Append('\n');
            int width = list.Max(l => l.Keys.Length) + 2;
            foreach ((string keys, string what) in list)
            {
                text.Append(keys.PadRight(width)).Append(what).Append('\n');
            }

            text.Append('\n');
        }

        return text.ToString();
    }

    private static TextBox Wide(TextBox box)
    {
        box.Width = 260;
        return box;
    }
}
