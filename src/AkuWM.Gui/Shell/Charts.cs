using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;

namespace AkuWM.Gui.Shell;

/// <summary>Level-coloured gauges and sparklines, drawn from the palette.</summary>
public static class Charts
{
    public static IBrush LevelBrush(string level) => level switch
    {
        "ok" => Palette.FoamBrush,
        "warn" => Palette.GoldBrush,
        "err" => Palette.LoveBrush,
        _ => Palette.IrisBrush,
    };

    public static Color LevelColour(string level) => level switch
    {
        "ok" => Palette.Foam,
        "warn" => Palette.Gold,
        "err" => Palette.Love,
        _ => Palette.Iris,
    };

    /// <summary>title ... value, and a bar filled to pct and coloured by level.</summary>
    public static Control Gauge(string title, double? pct, string value, string level, string? subtitle = null)
    {
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var t = Ui.Text(title);
        t.TextTrimming = TextTrimming.CharacterEllipsis;
        t.TextWrapping = TextWrapping.NoWrap;
        var v = new TextBlock { Text = value, Foreground = level.Length > 0 ? LevelBrush(level) : Palette.TextBrush, FontWeight = FontWeight.SemiBold };
        Grid.SetColumn(v, 1);
        head.Children.Add(t);
        head.Children.Add(v);
        var track = new Border { Height = 7, CornerRadius = new CornerRadius(3.5), Background = new SolidColorBrush(Palette.Text, 0.12) };
        var fill = new Border { Height = 7, CornerRadius = new CornerRadius(3.5), Background = LevelBrush(level), HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left };
        var stack = new Grid();
        stack.Children.Add(track);
        stack.Children.Add(fill);
        double clamped = pct is null ? 0 : Math.Clamp(pct.Value, 0, 100);
        stack.SizeChanged += (_, e) => fill.Width = pct is null || clamped == 0 ? 0 : Math.Max(7, e.NewSize.Width * clamped / 100);
        var col = Ui.Col(3, head, stack);
        if (subtitle is not null)
        {
            col.Children.Add(Ui.Dim(subtitle));
        }

        return col;
    }

    /// <summary>A filled line over a short series; <paramref name="ymax"/> pins the scale so charts of one kind compare.</summary>
    public static Control Sparkline(IReadOnlyList<double?> series, string level = "", double height = 38, double? ymax = null)
    {
        var canvas = new Canvas { Height = height, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch, ClipToBounds = true };
        void Draw(double w)
        {
            canvas.Children.Clear();
            var points = new List<(int I, double V)>();
            for (int i = 0; i < series.Count; i++)
            {
                if (series[i] is { } v)
                {
                    points.Add((i, v));
                }
            }

            if (w <= 0 || points.Count < 2)
            {
                canvas.Children.Add(new TextBlock { Text = "no data", Foreground = Palette.MutedBrush, FontSize = 11, [Canvas.LeftProperty] = 4.0, [Canvas.TopProperty] = height / 2 - 8 });
                return;
            }

            double hi = ymax ?? points.Max(p => p.V);
            if (hi <= 0)
            {
                hi = 1;
            }

            const double pad = 2;
            double X(int i) => pad + (w - 2 * pad) * i / Math.Max(1, series.Count - 1);
            double Y(double v) => height - pad - (height - 2 * pad) * Math.Min(v, hi) / hi;
            foreach (double frac in new[] { 0.25, 0.5, 0.75 })
            {
                canvas.Children.Add(new Line { StartPoint = new Point(0, height * frac), EndPoint = new Point(w, height * frac), Stroke = new SolidColorBrush(Palette.Text, 0.08), StrokeThickness = 1 });
            }

            Color colour = LevelColour(level);
            var area = new Polygon { Fill = new SolidColorBrush(colour, 0.18) };
            area.Points.Add(new Point(X(points[0].I), height - pad));
            var line = new Polyline { Stroke = new SolidColorBrush(colour), StrokeThickness = 1.6 };
            foreach ((int i, double v) in points)
            {
                area.Points.Add(new Point(X(i), Y(v)));
                line.Points.Add(new Point(X(i), Y(v)));
            }

            area.Points.Add(new Point(X(points[^1].I), height - pad));
            canvas.Children.Add(area);
            canvas.Children.Add(line);
        }

        canvas.SizeChanged += (_, e) => Draw(e.NewSize.Width);
        return canvas;
    }

    /// <summary>A rounded card with a title row and an optional level chip.</summary>
    public static (Border Card, StackPanel Body) Card(string title, string? subtitle = null, string level = "")
    {
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var t = Ui.Text(title);
        t.FontWeight = FontWeight.SemiBold;
        head.Children.Add(t);
        if (subtitle is not null)
        {
            Control s = level.Length > 0 ? Ui.Chip(subtitle, LevelColour(level)) : Ui.Dim(subtitle);
            Grid.SetColumn(s, 1);
            head.Children.Add(s);
        }

        var body = new StackPanel { Spacing = 8 };
        var col = Ui.Col(8, head, body);
        return (Ui.Card(col), body);
    }
}
