using System.Text.Json.Nodes;
using AkuWM.Core.Ipc;
using AkuWM.Gui.Services;
using AkuWM.Gui.Shell;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace AkuWM.Gui.Sections;

/// <summary>The live tree: monitors, workspaces, windows. Read every two seconds while shown.</summary>
public sealed class WindowsSection : Section
{
    private readonly StackPanel _tree = new() { Spacing = 12 };
    private readonly TextBlock _note = Ui.Dim(string.Empty);
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private bool _reading;

    public WindowsSection(AppServices services)
        : base(services)
    {
        _timer.Tick += (_, _) => Refresh();
    }

    public override string Key => "windows";

    public override string Title => "Windows";

    public override string Blurb => "monitors, workspaces and windows as AkuWM holds them; click to focus";

    public override string Glyph => "▦";

    protected override Control Build() => Ui.Scroll(Ui.Col(8, _note, _tree));

    public override void Shown()
    {
        Refresh();
        _timer.Start();
    }

    public override void Hidden() => _timer.Stop();

    public override void Refresh()
    {
        if (_reading)
        {
            return;
        }

        _reading = true;
        IDaemon daemon = Services.Daemon;
        Task.Run(() => daemon.Send("compat query monitors")).ContinueWith(t =>
        {
            _reading = false;
            if (t.Status != TaskStatus.RanToCompletion || !t.Result.Success)
            {
                _note.Text = t.Status == TaskStatus.RanToCompletion ? t.Result.Error ?? "AkuWM did not answer" : "AkuWM did not answer";
                return;
            }

            _note.Text = $"read {DateTime.Now:HH:mm:ss}";
            Render(Monitors(t.Result.Data));
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>The compat envelope: <c>data.monitors</c> under <c>data</c>, or the bare list.</summary>
    public static JsonArray Monitors(JsonNode? data)
    {
        JsonNode? inner = data is JsonObject o && o["data"] is JsonNode d ? d : data;
        return inner is JsonObject m && m["monitors"] is JsonArray a ? a : inner as JsonArray ?? [];
    }

    public void Render(JsonArray monitors)
    {
        _tree.Children.Clear();
        foreach (JsonNode? node in monitors)
        {
            if (node is not JsonObject monitor)
            {
                continue;
            }

            var col = new StackPanel { Spacing = 6 };
            string device = Ui.Str(monitor, "deviceName");
            col.Children.Add(Ui.Row(8, Ui.Text($"{device}", "h2"), Ui.Dim($"{Num(monitor, "width")}×{Num(monitor, "height")} at {Num(monitor, "x")},{Num(monitor, "y")} · {Num(monitor, "scaleFactor")}×")));
            if (monitor["children"] is JsonArray workspaces)
            {
                foreach (JsonNode? wsNode in workspaces)
                {
                    if (wsNode is JsonObject ws)
                    {
                        col.Children.Add(Workspace(ws));
                    }
                }
            }

            _tree.Children.Add(Ui.Card(col));
        }

        if (_tree.Children.Count == 0)
        {
            _tree.Children.Add(Ui.Empty("no monitors: AkuWM is not running"));
        }
    }

    private Control Workspace(JsonObject ws)
    {
        bool displayed = Ui.Flag(ws, "isDisplayed", false);
        bool focused = Ui.Flag(ws, "hasFocus", false);
        var header = Ui.Row(8, Ui.Chip(Ui.Str(ws, "name"), focused ? Palette.Iris : displayed ? Palette.Foam : Palette.Muted), Ui.Dim(Ui.Str(ws, "tilingDirection")));
        var rows = new StackPanel { Spacing = 2, Margin = new Thickness(12, 0, 0, 0) };
        if (ws["children"] is JsonArray windows)
        {
            foreach (JsonNode? node in windows)
            {
                if (node is JsonObject window)
                {
                    rows.Children.Add(WindowRow(window));
                }
            }
        }

        if (rows.Children.Count == 0)
        {
            rows.Children.Add(Ui.Dim("empty"));
        }

        return Ui.Col(4, header, rows);
    }

    private Control WindowRow(JsonObject window)
    {
        string id = Ui.Str(window, "id");
        string state = window["state"] is JsonObject s ? Ui.Str(s, "type") : string.Empty;
        bool focus = Ui.Flag(window, "hasFocus", false);
        var title = Ui.Text(Ui.Str(window, "title"));
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        title.TextWrapping = TextWrapping.NoWrap;
        title.MaxWidth = 520;
        if (focus)
        {
            title.Foreground = Palette.IrisBrush;
            title.FontWeight = FontWeight.SemiBold;
        }

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var left = Ui.Row(8, Ui.Chip(state, state == "floating" ? Palette.Gold : state == "fullscreen" ? Palette.Love : state == "minimized" ? Palette.Muted : Palette.Pine), title, Ui.Dim(Ui.Str(window, "processName")));
        var focusButton = new Button { Content = left, Background = Brushes.Transparent, Padding = new Thickness(4, 2), HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch, HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Left };
        focusButton.Click += (_, _) => Command($"focus --container-id {id}");
        grid.Children.Add(focusButton);
        var actions = Ui.Row(4,
            Ui.Button(state == "floating" ? "tile" : "float", () => Command($"toggle-floating --id {id}")),
            Ui.Button("close", () => Command($"close --id {id}")));
        Grid.SetColumn(actions, 1);
        grid.Children.Add(actions);
        return grid;
    }

    private void Command(string command)
    {
        CommandResponse reply = Services.Daemon.Send("compat command " + command);
        if (!reply.Success)
        {
            Toast(reply.Error ?? "refused", error: true);
        }

        Refresh();
    }

    private static string Num(JsonObject o, string key) => o[key]?.ToString() ?? "?";
}
