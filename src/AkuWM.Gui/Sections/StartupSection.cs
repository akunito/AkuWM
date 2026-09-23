using System.Text.Json.Nodes;
using AkuWM.Core.Config;
using AkuWM.Gui.Services;
using AkuWM.Gui.Shell;
using Avalonia.Controls;

namespace AkuWM.Gui.Sections;

public sealed class StartupSection : ItemSection
{
    public StartupSection(AppServices services)
        : base(services)
    {
    }

    public override string Key => "startup";

    public override string Title => "Startup";

    public override string Blurb => "what AkuWM launches when it starts, in this order";

    public override string Glyph => "▶";

    protected override string SectionKey => "startup";

    protected override string IdPrefix => "u";

    protected override Control Row(JsonObject item, string layer)
    {
        string after = Ui.Str(item, "after");
        int delay = Ui.Int(item, "delay_ms") ?? 0;
        var chips = new List<Control> { LayerChip(layer), Ui.Chip(after.Length == 0 ? "now" : after, after == "ipc" ? Palette.Foam : Palette.Gold) };
        if (delay > 0)
        {
            chips.Add(Ui.Chip($"+{delay} ms", Palette.Subtle));
        }

        return Ui.ItemRow(Ui.Str(item, "name"), Ui.Str(item, "command"), Ui.Flag(item, "enabled"), [.. chips]);
    }

    protected override JsonObject NewItem() => new() { ["name"] = string.Empty, ["command"] = string.Empty, ["after"] = "now", ["delay_ms"] = 0, ["enabled"] = true };

    protected override Control Form(JsonObject draft) => Ui.Col(14,
        Ui.Row(12, Ui.Field("name", Wide(Ui.Text(draft, "name", "a name"))), Ui.Field("enabled", Ui.Check(draft, "enabled", "on"))),
        Ui.Field("command", Ui.Text(draft, "command", "an exe, a .lnk, %VAR% expanded", mono: true)),
        Ui.Row(12,
            Ui.Field("after", Ui.Combo(draft, "after", ConfigDefaults.StartupPhases, "now"), "now = as AkuWM starts; ipc = once the pipe and 6123 answer (the bar)"),
            Ui.Field("delay (ms)", Ui.Number(draft, "delay_ms", 0, 600000, 0))),
        Ui.Field("notes", Ui.Notes(draft)),
        Ui.Dim("Takes effect on AkuWM's next start; 'Run now' starts it this moment."));

    protected override IEnumerable<Control> Extras(JsonObject draft)
    {
        yield return Ui.Button("Run now", () =>
        {
            string command = Ui.Str(draft, "command");
            string? error = command.Length == 0 ? "no command" : Services.Start(command);
            Toast(error ?? $"started {Ui.Str(draft, "name")}", error is not null);
        });
        yield return Ui.Button("▲", () => Move(draft, -1));
        yield return Ui.Button("▼", () => Move(draft, +1));
    }

    /// <summary>Order is the file's order within the item's layer.</summary>
    public string? Move(JsonObject draft, int by)
    {
        if (ConfigService.IdOf(draft) is not { } id)
        {
            return "save it first";
        }

        var ids = new List<string>();
        foreach (JsonObject item in Services.Config.Items(Layer, SectionKey))
        {
            if (ConfigService.IdOf(item) is { } other)
            {
                ids.Add(other);
            }
        }

        int at = ids.IndexOf(id);
        int to = at + by;
        if (at < 0 || to < 0 || to >= ids.Count)
        {
            return null;
        }

        (ids[at], ids[to]) = (ids[to], ids[at]);
        string? error = Services.Config.Reorder(Layer, SectionKey, ids);
        if (error is not null)
        {
            Toast(error, error: true);
            return error;
        }

        Select(id);
        Refresh();
        return null;
    }

    private static TextBox Wide(TextBox box)
    {
        box.Width = 320;
        return box;
    }
}
