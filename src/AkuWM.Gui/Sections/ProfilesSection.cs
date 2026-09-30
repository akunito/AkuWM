using System.Text.Json.Nodes;
using AkuWM.Core.Git;
using AkuWM.Core.Profiles;
using AkuWM.Gui.Services;
using AkuWM.Gui.Shell;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace AkuWM.Gui.Sections;

/// <summary>
/// This machine's layer against another's, section by section; tick items
/// and pull them here or push them there. Every copy snapshots first; the
/// snapshots list restores whole or per section.
/// </summary>
public sealed class ProfilesSection : Section
{
    private readonly ComboBox _section = new() { ItemsSource = Profiles.Sections, SelectedIndex = 2, Width = 140 };
    private readonly ComboBox _other = new() { Width = 180 };
    private readonly TextBlock _info = Ui.Dim(string.Empty);
    private readonly StackPanel _rows = new() { Spacing = 2 };
    private readonly StackPanel _snapshots = new() { Spacing = 4 };
    private readonly List<(CheckBox Box, string Id, string Side)> _checks = [];
    private bool _filling;

    public ProfilesSection(AppServices services)
        : base(services)
    {
        _section.SelectionChanged += (_, _) => { if (!_filling) { Refresh(); } };
        _other.SelectionChanged += (_, _) => { if (!_filling) { FillRows(); } };
    }

    public override string Key => "profiles";

    public override string Title => "Profiles";

    public override string Blurb => "compare and copy configuration between machines; snapshots";

    public override string Glyph => "⇄";

    public Profiles Profiles => new(Services.Config.Paths);

    public string Me => Services.Config.Paths.Profile;

    public string SelectedSection => _section.SelectedItem as string ?? "shortcuts";

    public string? SelectedOther => _other.SelectedItem is string s && !s.StartsWith('(') ? s : null;

    protected override Control Build()
    {
        var bar = Ui.Row(10,
            Ui.Field("section", _section),
            Ui.Field("other layer", _other),
            Ui.Button("Snapshot now", () => SnapshotNow(), "accent"),
            Ui.Button("Select all", () => SelectAll(true)),
            Ui.Button("None", () => SelectAll(false)));
        bar.VerticalAlignment = VerticalAlignment.Bottom;
        var actions = Ui.Row(8,
            Ui.Button("Pull selected → here", () => Pull(), "accent"),
            Ui.Button("Push selected → other", () => Push()));
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,16,380") };
        var left = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        DockPanel.SetDock(_info, Dock.Top);
        DockPanel.SetDock(actions, Dock.Bottom);
        _info.Margin = new Thickness(0, 8, 0, 8);
        actions.Margin = new Thickness(0, 8, 0, 0);
        left.Children.Add(bar);
        left.Children.Add(_info);
        left.Children.Add(actions);
        left.Children.Add(Ui.Scroll(_rows));
        grid.Children.Add(left);
        var right = Ui.Scroll(Ui.Col(8, Ui.Text("Snapshots", "h2"), Ui.Dim("one is taken before every copy or restore"), _snapshots));
        Grid.SetColumn(right, 2);
        grid.Children.Add(right);
        return grid;
    }

    public override void Refresh()
    {
        _filling = true;
        string? current = SelectedOther;
        List<string> names = Profiles.Layers().Keys.Where(n => n != Me).ToList();
        _other.ItemsSource = names.Count > 0 ? names : ["(no other layer)"];
        _other.SelectedIndex = Math.Max(0, current is null ? 0 : names.IndexOf(current));
        _filling = false;
        FillRows();
        FillSnapshots();
    }

    public override void Select(string id)
    {
        if (Array.IndexOf(Profiles.Sections, id) >= 0)
        {
            _section.SelectedItem = id;
        }
    }

    private void FillRows()
    {
        _rows.Children.Clear();
        _checks.Clear();
        string? other = SelectedOther;
        string section = SelectedSection;
        if (other is null)
        {
            _info.Text = "No other layer in the config directory yet.";
            return;
        }

        SectionDiff d = Profiles.Diff(other, Me, section);
        int shared = Profiles.Items(Profiles.Layer("common"), section).Count;
        _info.Text = $"{section}: {d.OnlyA.Count} only in {other} · {d.OnlyB.Count} only in {Me} · {d.Changed.Count} differ · {d.Same.Count} same · {shared} shared in common.json";
        if (d.IsEmpty)
        {
            _rows.Children.Add(Ui.Empty($"Neither {other} nor {Me} has machine-specific {section}: all {shared} live in common.json, shared by every machine."));
            return;
        }

        Group($"only in {other}", d.OnlyA, other, Palette.Iris);
        Group($"differ ({other} version shown)", d.Changed.Select(c => c.A).ToList(), other, Palette.Gold);
        Group($"only in {Me}", d.OnlyB, Me, Palette.Foam);
        Group("identical in both", d.Same, "both", Palette.Subtle);
    }

    private void Group(string title, List<JsonObject> items, string side, Avalonia.Media.Color colour)
    {
        if (items.Count == 0)
        {
            return;
        }

        var header = Ui.Text(title.ToUpperInvariant(), "h2");
        header.Margin = new Thickness(0, 10, 0, 2);
        _rows.Children.Add(header);
        foreach (JsonObject item in items)
        {
            var box = new CheckBox { VerticalAlignment = VerticalAlignment.Center };
            string id = Profiles.IdOf(item) ?? "?";
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
            row.Children.Add(box);
            var text = Ui.Col(0, Ui.Text(Profiles.Label(SelectedSection, item)), Ui.Dim("id " + id));
            Grid.SetColumn(text, 1);
            row.Children.Add(text);
            var chip = Ui.Chip(side, colour);
            Grid.SetColumn(chip, 2);
            row.Children.Add(chip);
            _rows.Children.Add(row);
            _checks.Add((box, id, side));
        }
    }

    private void SelectAll(bool on)
    {
        foreach ((CheckBox box, _, _) in _checks)
        {
            box.IsChecked = on;
        }
    }

    public List<string> Chosen(params string[] sides) => _checks.Where(c => c.Box.IsChecked == true && sides.Contains(c.Side)).Select(c => c.Id).ToList();

    public string? Pull() => SelectedOther is { } other ? Copy(other, Me, Chosen(other)) : "no other layer";

    public string? Push() => SelectedOther is { } other ? Copy(Me, other, Chosen(Me, "both")) : "no other layer";

    /// <summary>Snapshot, copy, reload the daemon when this machine's layer changed, commit. Null when done.</summary>
    public string? Copy(string from, string to, List<string> ids)
    {
        if (ids.Count == 0)
        {
            Toast("Select items first", error: true);
            return "nothing selected";
        }

        string section = SelectedSection;
        CopyResult result;
        try
        {
            result = Profiles.CopyItems(from, to, section, ids);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or Core.Config.ConfigException)
        {
            Toast(ex.Message, error: true);
            return ex.Message;
        }

        string? reload = to == Me || to == "common" ? ConfigService.Reload(Services.Daemon, section == "shortcuts") : null;
        string? committed = CommitState($"copy {ids.Count} {section} from {from} to {to}");
        Toast($"Copied {result.Copied.Count} {section} item(s) {from} → {to} · snapshot {result.Snapshot}" + (reload is not null ? " · " + reload : string.Empty) + (committed is not null ? " · " + committed : string.Empty), reload is not null);
        Refresh();
        return null;
    }

    public string SnapshotNow()
    {
        string id = Profiles.Snapshot("manual");
        CommitState("snapshot " + id);
        Toast("Snapshot " + id);
        FillSnapshots();
        return id;
    }

    private void FillSnapshots()
    {
        _snapshots.Children.Clear();
        List<SnapshotInfo> snaps = Profiles.Snapshots();
        if (snaps.Count == 0)
        {
            _snapshots.Children.Add(Ui.Dim("none yet"));
            return;
        }

        snaps.Reverse();
        foreach (SnapshotInfo s in snaps)
        {
            int differ = 0;
            foreach (var per in Profiles.DiffSnapshot(s.Id).Values)
            {
                foreach ((int a, int b, int c) in per.Values)
                {
                    differ += a + b + c;
                }
            }

            string id = s.Id;
            var buttons = Ui.Row(4,
                Ui.Button("Restore all", () => Restore(id, null), "danger"),
                Ui.Button($"Restore {SelectedSection}", () => Restore(id, [SelectedSection])));
            _snapshots.Children.Add(Ui.Card(Ui.Col(4, Ui.Mono(id), Ui.Dim($"{s.Reason} · taken on {s.Profile} · {differ} item(s) differ from now"), buttons)));
        }
    }

    public List<string> Restore(string id, IReadOnlyList<string>? sections)
    {
        List<string> touched;
        try
        {
            touched = Profiles.Restore(id, sections: sections);
        }
        catch (Exception ex) when (ex is IOException or Core.Config.ConfigException)
        {
            Toast(ex.Message, error: true);
            return [];
        }

        string? reload = ConfigService.Reload(Services.Daemon, sections is null || sections.Contains("shortcuts"));
        CommitState($"restore {string.Join(", ", touched)} from {id}");
        Toast($"Restored {string.Join(", ", touched)} from {id}" + (reload is not null ? " · " + reload : string.Empty), reload is not null);
        Refresh();
        return touched;
    }

    /// <summary>The layers and the snapshots committed when the config lives in a repository and auto_commit is on.</summary>
    private string? CommitState(string message)
    {
        if (Services.Config.Load().Effective.Settings?.Git?.AutoCommit == false)
        {
            return null;
        }

        try
        {
            var git = new GitSync(Services.Config.Paths);
            string? sha = git.Commit(Profiles.Files(), message);
            return sha is null ? null : "committed " + sha;
        }
        catch (InvalidOperationException ex)
        {
            return "commit failed: " + ex.Message;
        }
    }
}
