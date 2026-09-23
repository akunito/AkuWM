using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace AkuWM.Gui.Shell;

/// <summary>Controls built in code, and form inputs that write straight into a draft <see cref="JsonObject"/>.</summary>
public static class Ui
{
    public static TextBlock Text(string text, params string[] classes)
    {
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        foreach (string c in classes)
        {
            block.Classes.Add(c);
        }

        return block;
    }

    public static TextBlock Dim(string text) => Text(text, "dim");

    public static TextBlock Mono(string text) => Text(text, "mono");

    public static StackPanel Row(double spacing, params Control[] children)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = spacing };
        foreach (Control c in children)
        {
            panel.Children.Add(c);
        }

        return panel;
    }

    public static StackPanel Col(double spacing, params Control[] children)
    {
        var panel = new StackPanel { Orientation = Orientation.Vertical, Spacing = spacing };
        foreach (Control c in children)
        {
            panel.Children.Add(c);
        }

        return panel;
    }

    public static Border Card(Control content, IBrush? background = null) => new()
    {
        Background = background ?? Palette.SurfaceBrush,
        CornerRadius = new CornerRadius(8),
        Padding = new Thickness(14),
        Child = content,
    };

    public static Border Chip(string text, Color colour) => new()
    {
        Background = new SolidColorBrush(colour, 0.18),
        CornerRadius = new CornerRadius(10),
        Padding = new Thickness(8, 1),
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock { Text = text, FontSize = 11, Foreground = new SolidColorBrush(colour) },
    };

    public static Button Button(string text, Action click, params string[] classes)
    {
        var button = new Button { Content = text };
        foreach (string c in classes)
        {
            button.Classes.Add(c);
        }

        button.Click += (_, _) => click();
        return button;
    }

    public static Control Field(string label, Control input, string? hint = null)
    {
        var col = Col(3, Text(label, "h2"), input);
        if (hint is not null)
        {
            col.Children.Add(Dim(hint));
        }

        return col;
    }

    /// <summary>A list row: a title, a monospaced line under it, chips to the right.</summary>
    public static Control ItemRow(string title, string subtitle, bool enabled, params Control[] chips)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var titleBlock = Text(title.Length == 0 ? "(unnamed)" : title);
        titleBlock.FontWeight = FontWeight.SemiBold;
        titleBlock.TextTrimming = TextTrimming.CharacterEllipsis;
        titleBlock.TextWrapping = TextWrapping.NoWrap;
        if (!enabled)
        {
            titleBlock.TextDecorations = TextDecorations.Strikethrough;
            titleBlock.Foreground = Palette.MutedBrush;
        }

        var sub = Mono(subtitle);
        sub.Classes.Add("dim");
        sub.FontSize = 11;
        sub.TextTrimming = TextTrimming.CharacterEllipsis;
        sub.TextWrapping = TextWrapping.NoWrap;
        grid.Children.Add(Col(1, titleBlock, sub));
        var chipRow = Row(4, chips);
        chipRow.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(chipRow, 1);
        grid.Children.Add(chipRow);
        return grid;
    }

    public static TextBox Text(JsonObject draft, string key, string? watermark = null, bool mono = false)
    {
        var box = new TextBox { Text = Str(draft, key), PlaceholderText = watermark };
        if (mono)
        {
            box.FontFamily = new FontFamily("Cascadia Mono,Consolas,JetBrains Mono,monospace");
        }

        box.TextChanged += (_, _) => Put(draft, key, box.Text);
        return box;
    }

    public static TextBox Notes(JsonObject draft)
    {
        TextBox box = Text(draft, "notes", "why this exists, what was measured");
        box.AcceptsReturn = true;
        box.TextWrapping = TextWrapping.Wrap;
        box.MinHeight = 60;
        return box;
    }

    public static CheckBox Check(JsonObject draft, string key, string label, bool fallback = true)
    {
        var box = new CheckBox { Content = label, IsChecked = Flag(draft, key, fallback) };
        box.IsCheckedChanged += (_, _) => draft[key] = box.IsChecked == true;
        return box;
    }

    public static ComboBox Combo(JsonObject draft, string key, string[] options, string? fallback = null)
    {
        string current = Str(draft, key);
        if (current.Length == 0 && fallback is not null)
        {
            current = fallback;
        }

        var combo = new ComboBox { ItemsSource = options, SelectedIndex = Math.Max(0, Array.IndexOf(options, current)) };
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is string chosen)
            {
                draft[key] = chosen;
            }
        };
        return combo;
    }

    public static NumericUpDown Number(JsonObject draft, string key, int min, int max, int? fallback = null)
    {
        int? now = Int(draft, key) ?? fallback;
        var box = new NumericUpDown { Minimum = min, Maximum = max, Increment = 1, FormatString = "0", Value = now, Width = 130 };
        box.ValueChanged += (_, _) =>
        {
            if (box.Value is { } value)
            {
                draft[key] = (int)value;
            }
            else
            {
                draft.Remove(key);
            }
        };
        return box;
    }

    /// <summary>Writes a string, or removes the key when it is empty: the file carries no empty strings.</summary>
    public static void Put(JsonObject draft, string key, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            draft.Remove(key);
        }
        else
        {
            draft[key] = value;
        }
    }

    public static string Str(JsonObject item, string key) => Services.ConfigService.Str(item, key);

    public static bool Flag(JsonObject item, string key, bool fallback = true) => Services.ConfigService.Flag(item, key, fallback);

    public static int? Int(JsonObject item, string key) => Services.ConfigService.Int(item, key);

    public static ScrollViewer Scroll(Control content) => new() { Content = content, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };

    public static Control Empty(string text)
    {
        TextBlock block = Dim(text);
        block.FontSize = 15;
        block.Margin = new Thickness(0, 24);
        block.HorizontalAlignment = HorizontalAlignment.Center;
        return block;
    }
}
