using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AkuWM.Core.Config;
using AkuWM.Core.Ipc;
using AkuWM.Gui.Services;
using AkuWM.Gui.Shell;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;

namespace AkuWM.Gui.Sections;

public sealed partial class MonitorsSection : Section
{
    private readonly Canvas _layout = new() { Height = 260 };
    private readonly StackPanel _table = new() { Spacing = 4 };
    private readonly StackPanel _workspaces = new() { Spacing = 4 };
    private readonly TextBlock _note = Ui.Dim(string.Empty);

    public MonitorsSection(AppServices services)
        : base(services)
    {
    }

    public override string Key => "monitors";

    public override string Title => "Monitors";

    public override string Blurb => "roles, identities, the layout and the workspace map";

    public override string Glyph => "▭";

    protected override Control Build() => Ui.Scroll(Ui.Col(14,
        Ui.Card(_layout),
        Ui.Row(10, Ui.Button("Identify: write the EDIDs into the profile", () => Identify(false), "accent"), Ui.Button("Dry run", () => Identify(true)), _note),
        Ui.Field("present", Ui.Card(_table)),
        Ui.Field("workspaces", Ui.Card(_workspaces))));

    public override void Refresh()
    {
        CommandResponse reply = Services.Daemon.Send("monitors list");
        var present = new List<PresentMonitor>();
        if (reply.Success && reply.Data is JsonArray list)
        {
            foreach (JsonNode? node in list)
            {
                if (node is JsonObject m && ParseBounds(Ui.Str(m, "bounds")) is { } bounds)
                {
                    present.Add(new PresentMonitor(Ui.Str(m, "role"), Ui.Str(m, "name"), Ui.Str(m, "hardwareId"), Ui.Str(m, "device"), bounds, Ui.Int(m, "dpi") ?? 96, Ui.Flag(m, "primary", false), Ui.Flag(m, "identified", false)));
                }
            }
        }

        _note.Text = reply.Success ? $"{present.Count} monitors present" : reply.Error ?? "AkuWM is not running";
        DrawLayout(present);
        _table.Children.Clear();
        foreach (PresentMonitor m in present)
        {
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("90,200,120,160,*") };
            Control[] cells =
            [
                Ui.Chip(m.Role.Length == 0 ? "no role" : m.Role, m.Role.Length == 0 ? Palette.Love : Palette.Iris),
                Ui.Text(m.Name),
                Ui.Mono(m.HardwareId),
                Ui.Dim($"{m.Bounds.Width}×{m.Bounds.Height} at {m.Bounds.X},{m.Bounds.Y}"),
                Ui.Dim($"{m.Dpi} dpi{(m.Primary ? " · primary" : string.Empty)}{(m.Identified ? " · identified" : " · by position only")}"),
            ];
            for (int i = 0; i < cells.Length; i++)
            {
                Grid.SetColumn(cells[i], i);
                grid.Children.Add(cells[i]);
            }

            _table.Children.Add(grid);
        }

        if (present.Count == 0)
        {
            _table.Children.Add(Ui.Dim("nothing: AkuWM is not running"));
        }

        AkuWmConfig config = Services.Config.Load().Effective;
        _workspaces.Children.Clear();
        foreach (MonitorConfig monitor in config.Monitors ?? [])
        {
            var names = new List<Control>();
            foreach (WorkspaceConfig ws in config.Workspaces ?? [])
            {
                if (ws.Monitor == monitor.Id && ws.Name is { } name)
                {
                    names.Add(Ui.Chip(name, Palette.Foam));
                }
            }

            var row = Ui.Row(8, Ui.Chip(monitor.Id ?? "?", Palette.Iris), Ui.Dim($"{monitor.Match?.Edid ?? "no edid"} · {monitor.Orientation ?? "horizontal"}"));
            var wrap = new WrapPanel();
            foreach (Control c in names)
            {
                c.Margin = new Thickness(0, 0, 4, 4);
                wrap.Children.Add(c);
            }

            _workspaces.Children.Add(Ui.Col(4, row, wrap));
        }
    }

    private void Identify(bool dryRun)
    {
        CommandResponse reply = Services.Daemon.Send(dryRun ? "monitors identify --dry-run" : "monitors identify");
        Toast(reply.Success ? (dryRun ? "dry run: " : "written: ") + (reply.Data?.ToJsonString() ?? "ok") : reply.Error ?? "refused", !reply.Success);
        if (!dryRun)
        {
            Refresh();
        }
    }

    private void DrawLayout(List<PresentMonitor> present)
    {
        _layout.Children.Clear();
        if (present.Count == 0)
        {
            return;
        }

        int minX = present.Min(m => m.Bounds.X), minY = present.Min(m => m.Bounds.Y);
        int maxX = present.Max(m => m.Bounds.X + m.Bounds.Width), maxY = present.Max(m => m.Bounds.Y + m.Bounds.Height);
        double scale = Math.Min(760.0 / Math.Max(1, maxX - minX), 240.0 / Math.Max(1, maxY - minY));
        foreach (PresentMonitor m in present)
        {
            double x = (m.Bounds.X - minX) * scale, y = (m.Bounds.Y - minY) * scale;
            var box = new Border
            {
                Width = m.Bounds.Width * scale - 4,
                Height = m.Bounds.Height * scale - 4,
                Background = new SolidColorBrush(m.Primary ? Palette.Iris : Palette.Foam, 0.15),
                BorderBrush = m.Primary ? Palette.IrisBrush : Palette.FoamBrush,
                BorderThickness = new Thickness(1.5),
                CornerRadius = new CornerRadius(4),
                Child = Ui.Col(0, Ui.Text(m.Role.Length == 0 ? "?" : m.Role, "h2"), Ui.Dim(m.Name), Ui.Dim($"{m.Bounds.Width}×{m.Bounds.Height}")),
                Padding = new Thickness(8),
            };
            Canvas.SetLeft(box, x + 2);
            Canvas.SetTop(box, y + 2);
            _layout.Children.Add(box);
        }
    }

    /// <summary>Four integers in the order Rect prints them: x, y, width, height.</summary>
    public static Core.Model.Rect? ParseBounds(string text)
    {
        MatchCollection numbers = Integers().Matches(text);
        return numbers.Count >= 4
            ? new Core.Model.Rect(int.Parse(numbers[0].Value), int.Parse(numbers[1].Value), int.Parse(numbers[2].Value), int.Parse(numbers[3].Value))
            : null;
    }

    [GeneratedRegex("-?\\d+")]
    private static partial Regex Integers();

    public sealed record PresentMonitor(string Role, string Name, string HardwareId, string Device, Core.Model.Rect Bounds, int Dpi, bool Primary, bool Identified);
}
