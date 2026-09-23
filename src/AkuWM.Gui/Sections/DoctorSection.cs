using System.Text.Json.Nodes;
using AkuWM.Core.Ipc;
using AkuWM.Gui.Services;
using AkuWM.Gui.Shell;
using Avalonia.Controls;

namespace AkuWM.Gui.Sections;

public sealed class DoctorSection : Section
{
    private readonly StackPanel _checks = new() { Spacing = 4 };
    private readonly TextBlock _summary = Ui.Text(string.Empty, "h2");

    public DoctorSection(AppServices services)
        : base(services)
    {
    }

    public override string Key => "doctor";

    public override string Title => "Doctor";

    public override string Blurb => "what AkuWM checks about itself and the desk";

    public override string Glyph => "✚";

    protected override Control Build() => Ui.Scroll(Ui.Col(10, _summary, Ui.Card(_checks)));

    public override void Refresh()
    {
        _checks.Children.Clear();
        CommandResponse reply = Services.Daemon.Send("doctor");
        if (!reply.Success || reply.Data is not JsonObject data)
        {
            _summary.Text = reply.Error ?? "AkuWM is not running";
            _checks.Children.Add(Ui.Dim("start it with `akuwm daemon` (the Startup shortcut does) and refresh"));
            return;
        }

        _summary.Text = $"{(Ui.Flag(data, "ok", false) ? "healthy" : "problems")} · {data["failures"]} failures · {data["warnings"]} warnings";
        if (data["checks"] is JsonArray checks)
        {
            foreach (JsonNode? node in checks)
            {
                if (node is not JsonObject check)
                {
                    continue;
                }

                string status = Ui.Str(check, "status");
                var colour = status switch
                {
                    "ok" => Palette.Foam,
                    "warn" or "warning" => Palette.Gold,
                    "fail" or "error" => Palette.Love,
                    _ => Palette.Subtle,
                };
                var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("70,220,*") };
                var chip = Ui.Chip(status, colour);
                var name = Ui.Text(Ui.Str(check, "name"));
                Grid.SetColumn(name, 1);
                var detail = Ui.Mono(Ui.Str(check, "detail"));
                detail.Classes.Add("dim");
                Grid.SetColumn(detail, 2);
                grid.Children.Add(chip);
                grid.Children.Add(name);
                grid.Children.Add(detail);
                _checks.Children.Add(grid);
            }
        }
    }
}
