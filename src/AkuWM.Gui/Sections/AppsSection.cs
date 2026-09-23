using System.Text.Json.Nodes;
using AkuWM.Gui.Services;
using AkuWM.Gui.Shell;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace AkuWM.Gui.Sections;

/// <summary>The winget catalogue of the dotfiles against what winget says is installed.</summary>
public sealed class AppsSection : Section
{
    private readonly StackPanel _rows = new() { Spacing = 3 };
    private readonly TextBlock _note = Ui.Dim(string.Empty);
    private WingetTable? _installed;

    public AppsSection(AppServices services)
        : base(services)
    {
    }

    public override string Key => "apps";

    public override string Title => "Apps";

    public override string Blurb => "the winget catalogue: installed or not, install, upgrade";

    public override string Glyph => "▣";

    public string CataloguePath
    {
        get
        {
            string? relative = Services.Config.Load().Effective.Apps?.Catalogue;
            return Path.GetFullPath(Path.Combine(Services.Config.Paths.ConfigDir, relative ?? "../winget-packages.json"));
        }
    }

    protected override Control Build()
    {
        var bar = Ui.Row(12, Ui.Button("Ask winget what is installed", AskWinget, "accent"), Ui.Button("Upgrade everything", () => Start("winget upgrade --all --accept-package-agreements --accept-source-agreements")), _note);
        bar.Margin = new Thickness(0, 0, 0, 10);
        var dock = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        dock.Children.Add(bar);
        dock.Children.Add(Ui.Scroll(Ui.Card(_rows)));
        return dock;
    }

    public override void Refresh()
    {
        _rows.Children.Clear();
        List<string> ids;
        try
        {
            ids = Catalogue(File.ReadAllText(CataloguePath));
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException)
        {
            _rows.Children.Add(Ui.Dim($"no catalogue: {ex.Message}"));
            return;
        }

        int installed = 0;
        foreach (string id in ids)
        {
            WingetRow? row = _installed?.Find(id);
            bool have = row is not null;
            installed += have ? 1 : 0;
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,110,110,Auto") };
            var name = Ui.Mono(id);
            grid.Children.Add(name);
            var version = Ui.Dim(row?.Version ?? string.Empty);
            Grid.SetColumn(version, 1);
            grid.Children.Add(version);
            Control status = _installed is null ? Ui.Dim("?") : have ? Ui.Chip(row!.Available.Length > 0 ? "upgrade " + row.Available : "installed", row.Available.Length > 0 ? Palette.Gold : Palette.Foam) : Ui.Chip("missing", Palette.Love);
            Grid.SetColumn(status, 2);
            grid.Children.Add(status);
            var action = have
                ? Ui.Button("Upgrade", () => Start($"winget upgrade --id {id} -e --accept-package-agreements --accept-source-agreements"))
                : Ui.Button("Install", () => Start($"winget install --id {id} -e --accept-package-agreements --accept-source-agreements"));
            Grid.SetColumn(action, 3);
            grid.Children.Add(action);
            _rows.Children.Add(grid);
        }

        _note.Text = _installed is null ? $"{ids.Count} in the catalogue" : $"{installed} of {ids.Count} installed · {_installed.Rows.Count} packages known to winget";
    }

    private void AskWinget()
    {
        _note.Text = "asking winget (takes a few seconds)…";
        Func<string, string, int, string?> run = Services.Run;
        Task.Run(() => run("winget", "list --accept-source-agreements --disable-interactivity", 90000)).ContinueWith(t =>
        {
            string? output = t.Status == TaskStatus.RanToCompletion ? t.Result : null;
            if (output is null)
            {
                _note.Text = "winget is not there, or did not answer";
                return;
            }

            _installed = WingetTable.Parse(output);
            Refresh();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void Start(string wingetCommand)
    {
        string? error = Services.Start("cmd.exe /k " + wingetCommand);
        Toast(error ?? "winget opened in a console", error is not null);
    }

    public static List<string> Catalogue(string json)
    {
        var ids = new List<string>();
        if (JsonNode.Parse(json) is JsonObject root && root["Sources"] is JsonArray sources)
        {
            foreach (JsonNode? source in sources)
            {
                if (source is JsonObject s && s["Packages"] is JsonArray packages)
                {
                    foreach (JsonNode? p in packages)
                    {
                        if (p is JsonObject package && package["PackageIdentifier"]?.ToString() is { Length: > 0 } id)
                        {
                            ids.Add(id);
                        }
                    }
                }
            }
        }

        ids.Sort(StringComparer.OrdinalIgnoreCase);
        return ids;
    }

    public void UseInstalled(WingetTable table)
    {
        _installed = table;
        Dispatcher.UIThread.Post(Refresh);
    }
}

public sealed record WingetRow(string Name, string Id, string Version, string Available);

/// <summary>
/// <c>winget list</c> prints a table whose columns are aligned by the header
/// line; the header's column starts are the only reliable way to cut the rows,
/// because names and versions carry spaces.
/// </summary>
public sealed class WingetTable
{
    public List<WingetRow> Rows { get; } = [];

    public WingetRow? Find(string id) => Rows.Find(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase));

    public static WingetTable Parse(string output)
    {
        var table = new WingetTable();
        string[] lines = output.Replace("\r", string.Empty).Split('\n');
        int header = Array.FindIndex(lines, l => l.Contains(" Id ", StringComparison.Ordinal) && l.Contains("Version", StringComparison.Ordinal));
        if (header < 0)
        {
            return table;
        }

        string head = lines[header];
        int idAt = head.IndexOf(" Id ", StringComparison.Ordinal) + 1;
        int versionAt = head.IndexOf("Version", idAt, StringComparison.Ordinal);
        int availableAt = head.IndexOf("Available", versionAt, StringComparison.Ordinal);
        int sourceAt = head.IndexOf("Source", Math.Max(versionAt, availableAt), StringComparison.Ordinal);
        int versionEnd = availableAt > 0 ? availableAt : sourceAt > 0 ? sourceAt : int.MaxValue;
        int availableEnd = sourceAt > 0 ? sourceAt : int.MaxValue;
        for (int i = header + 2; i < lines.Length; i++)
        {
            string line = lines[i];
            if (line.Length <= idAt)
            {
                continue;
            }

            string name = line[..idAt].Trim();
            string id = Cut(line, idAt, versionAt).Trim();
            string version = Cut(line, versionAt, versionEnd).Trim();
            string available = availableAt > 0 ? Cut(line, availableAt, availableEnd).Trim() : string.Empty;
            if (id.Length > 0)
            {
                table.Rows.Add(new WingetRow(name, id, version, available));
            }
        }

        return table;
    }

    private static string Cut(string line, int from, int to)
    {
        if (from < 0 || from >= line.Length)
        {
            return string.Empty;
        }

        return to == int.MaxValue || to >= line.Length ? line[from..] : line[from..to];
    }
}
