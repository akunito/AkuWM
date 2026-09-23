using System.Text.Json.Nodes;
using AkuWM.Gui.Services;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace AkuWM.Gui.Shell;

/// <summary>
/// A list of configuration items on the left, the selected one's form on the
/// right: rules, shortcuts, startup entries and tools are all this. The form
/// edits a draft copy; Save writes the layer the item lives in and asks the
/// daemon to reload.
/// </summary>
public abstract class ItemSection : Section
{
    private static readonly string[] Layers = ["common", "profile"];

    private readonly ListBox _list = new() { Background = null };
    private readonly ContentControl _editor = new();
    private readonly TextBox _search = new() { PlaceholderText = "filter", Width = 180 };
    private readonly TextBlock _count = Ui.Dim(string.Empty);
    private List<(JsonObject Item, string Layer)> _items = [];
    private JsonObject? _draft;
    private string _layer = "common";
    private string? _selectedId;

    protected ItemSection(AppServices services)
        : base(services)
    {
    }

    /// <summary>The key in the file: <c>rules</c>, <c>shortcuts</c>...</summary>
    protected abstract string SectionKey { get; }

    protected abstract string IdPrefix { get; }

    /// <summary>Whether a save also re-renders the AutoHotkey bindings.</summary>
    protected virtual bool ReloadBindings => false;

    protected abstract Control Row(JsonObject item, string layer);

    protected abstract Control Form(JsonObject draft);

    protected abstract JsonObject NewItem();

    /// <summary>Extra buttons under the form, for the draft as it is.</summary>
    protected virtual IEnumerable<Control> Extras(JsonObject draft) => [];

    /// <summary>Extra controls above the list.</summary>
    protected virtual IEnumerable<Control> Toolbar() => [];

    public IReadOnlyList<(JsonObject Item, string Layer)> Items => _items;

    public JsonObject? Draft => _draft;

    public string Layer => _layer;

    protected override Control Build()
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("340,16,*") };
        var toolbar = Ui.Row(8, Ui.Button("New", () => Edit(NewItem(), "common"), "accent"), _search);
        foreach (Control extra in Toolbar())
        {
            toolbar.Children.Add(extra);
        }

        _search.TextChanged += (_, _) => FillList();
        _list.SelectionChanged += (_, _) =>
        {
            if (_list.SelectedItem is ListBoxItem { Tag: int index } && index < _items.Count)
            {
                (JsonObject item, string layer) = _items[index];
                _selectedId = ConfigService.IdOf(item);
                Edit(ConfigService.Clone(item), layer);
            }
        };
        var left = new DockPanel();
        DockPanel.SetDock(toolbar, Dock.Top);
        DockPanel.SetDock(_count, Dock.Bottom);
        left.Children.Add(toolbar);
        left.Children.Add(_count);
        left.Children.Add(new ScrollViewer { Content = _list, Margin = new Thickness(0, 10, 0, 6) });
        grid.Children.Add(left);
        _editor.Content = Ui.Empty("Pick an item, or make a new one.");
        var right = Ui.Scroll(_editor);
        Grid.SetColumn(right, 2);
        grid.Children.Add(right);
        return grid;
    }

    public override void Refresh()
    {
        _items = Services.Config.Effective(SectionKey);
        FillList();
    }

    public override void Select(string id)
    {
        _selectedId = id;
        FillList();
    }

    private void FillList()
    {
        string filter = _search.Text ?? string.Empty;
        _list.Items.Clear();
        int shown = 0;
        ListBoxItem? select = null;
        for (int i = 0; i < _items.Count; i++)
        {
            (JsonObject item, string layer) = _items[i];
            if (filter.Length > 0 && !item.ToJsonString().Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var row = new ListBoxItem { Content = Row(item, layer), Tag = i };
            _list.Items.Add(row);
            shown++;
            if (_selectedId is not null && ConfigService.IdOf(item) == _selectedId)
            {
                select = row;
            }
        }

        _count.Text = $"{shown} of {_items.Count}";
        if (select is not null)
        {
            _list.SelectedItem = select;
        }
    }

    public void Edit(JsonObject draft, string layer)
    {
        _draft = draft;
        _layer = layer;
        var layerBox = new ComboBox { ItemsSource = Layers, SelectedIndex = layer == "profile" ? 1 : 0, Width = 120 };
        layerBox.SelectionChanged += (_, _) => _layer = layerBox.SelectedItem as string ?? "common";
        var buttons = Ui.Row(8,
            Ui.Button("Save and apply", () => Save(), "accent"),
            Ui.Button("Delete", () => Delete(), "danger"),
            Ui.Field("layer", layerBox));
        buttons.VerticalAlignment = VerticalAlignment.Bottom;
        foreach (Control extra in Extras(draft))
        {
            buttons.Children.Add(extra);
        }

        string id = ConfigService.IdOf(draft) ?? "(new)";
        var col = Ui.Col(14, Form(draft), buttons, Ui.Dim($"id {id} · {Services.Config.FileOf(layer)}"));
        col.Margin = new Thickness(0, 0, 8, 24);
        _editor.Content = col;
    }

    public string? Save()
    {
        if (_draft is null)
        {
            return "nothing to save";
        }

        string? error = Services.Config.Save(_layer, SectionKey, _draft, IdPrefix);
        if (error is not null)
        {
            Toast(error, error: true);
            return error;
        }

        _selectedId = ConfigService.IdOf(_draft);
        string? reload = ConfigService.Reload(Services.Daemon, ReloadBindings);
        Toast(reload ?? "saved and applied", reload is not null);
        Refresh();
        return null;
    }

    public string? Delete()
    {
        if (_draft is null || ConfigService.IdOf(_draft) is not { } id)
        {
            _editor.Content = Ui.Empty("Pick an item, or make a new one.");
            return null;
        }

        string? error = Services.Config.Remove(_layer, SectionKey, id);
        if (error is not null)
        {
            Toast(error, error: true);
            return error;
        }

        _selectedId = null;
        _draft = null;
        _editor.Content = Ui.Empty("Deleted.");
        string? reload = ConfigService.Reload(Services.Daemon, ReloadBindings);
        Toast(reload ?? "deleted and applied", reload is not null);
        Refresh();
        return null;
    }

    protected void ShowInEditor(Control content) => _editor.Content = content;

    protected static Border LayerChip(string layer) =>
        Ui.Chip(layer, layer == "profile" ? Palette.Iris : Palette.Foam);
}
