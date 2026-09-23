using System.Text.Json.Nodes;
using AkuWM.Gui.Services;
using AkuWM.Gui.Shell;
using Avalonia;
using Avalonia.Controls;

namespace AkuWM.Gui.Sections;

public sealed class ToolsSection : ItemSection
{
    private readonly WrapPanel _launchers = new();

    public ToolsSection(AppServices services)
        : base(services)
    {
    }

    public override string Key => "tools";

    public override string Title => "Tools";

    public override string Blurb => "a grid of things to launch";

    public override string Glyph => "⚙";

    protected override string SectionKey => "tools";

    protected override string IdPrefix => "t";

    public int Launchers => _launchers.Children.Count;

    protected override Control Build()
    {
        var grid = new DockPanel();
        var top = Ui.Card(_launchers);
        top.Margin = new Thickness(0, 0, 0, 14);
        DockPanel.SetDock(top, Dock.Top);
        grid.Children.Add(top);
        grid.Children.Add(base.Build());
        return grid;
    }

    public override void Refresh()
    {
        base.Refresh();
        _launchers.Children.Clear();
        foreach ((JsonObject item, _) in Items)
        {
            if (!Ui.Flag(item, "enabled"))
            {
                continue;
            }

            string command = Ui.Str(item, "command");
            string icon = Ui.Str(item, "icon");
            string name = Ui.Str(item, "name");
            var button = Ui.Button((icon.Length > 0 ? icon + "  " : string.Empty) + name, () =>
            {
                string? error = Services.Start(command);
                Toast(error ?? $"started {name}", error is not null);
            });
            button.Margin = new Thickness(0, 0, 8, 8);
            button.MinWidth = 140;
            _launchers.Children.Add(button);
        }

        if (_launchers.Children.Count == 0)
        {
            _launchers.Children.Add(Ui.Dim("no tools yet: make one below (a command, ms-settings:display works too)"));
        }
    }

    protected override Control Row(JsonObject item, string layer) =>
        Ui.ItemRow(Ui.Str(item, "name"), Ui.Str(item, "command"), Ui.Flag(item, "enabled"), LayerChip(layer));

    protected override JsonObject NewItem() => new() { ["name"] = string.Empty, ["command"] = string.Empty, ["enabled"] = true };

    protected override Control Form(JsonObject draft) => Ui.Col(14,
        Ui.Row(12, Ui.Field("name", Wide(Ui.Text(draft, "name", "a name"))), Ui.Field("icon", Narrow(Ui.Text(draft, "icon", "one glyph"))), Ui.Field("enabled", Ui.Check(draft, "enabled", "on"))),
        Ui.Field("command", Ui.Text(draft, "command", "an exe, a URI (ms-settings:bluetooth), %VAR% expanded", mono: true)),
        Ui.Field("notes", Ui.Notes(draft)));

    protected override IEnumerable<Control> Extras(JsonObject draft)
    {
        yield return Ui.Button("Run", () =>
        {
            string? error = Services.Start(Ui.Str(draft, "command"));
            Toast(error ?? "started", error is not null);
        });
    }

    private static TextBox Wide(TextBox box)
    {
        box.Width = 320;
        return box;
    }

    private static TextBox Narrow(TextBox box)
    {
        box.Width = 80;
        return box;
    }
}
