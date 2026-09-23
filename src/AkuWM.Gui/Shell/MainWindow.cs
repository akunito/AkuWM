using System.Text.Json.Nodes;
using AkuWM.Core.Commands;
using AkuWM.Core.Ipc;
using AkuWM.Gui.Sections;
using AkuWM.Gui.Services;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace AkuWM.Gui.Shell;

/// <summary>One window, a sidebar of sections. Hidden on close, never destroyed: Hyper+S toggles it.</summary>
public sealed class MainWindow : Window
{
    private readonly AppServices _services;
    private readonly ListBox _nav = new() { Background = null };
    private readonly ContentControl _content = new();
    private readonly TextBlock _title = Ui.Text(string.Empty, "h1");
    private readonly TextBlock _blurb = Ui.Dim(string.Empty);
    private readonly TextBlock _toast = Ui.Text(string.Empty);
    private readonly Border _toastBar;
    private readonly Ellipse _dot = new() { Width = 8, Height = 8, Fill = Palette.MutedBrush };
    private readonly TextBlock _status = Ui.Dim("looking for AkuWM");
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(4) };
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private bool _navigating;

    public MainWindow(AppServices services)
    {
        _services = services;
        Action<string, bool> before = services.Toast;
        _services.Toast = (text, error) => { before(text, error); Toast(text, error); };
        Title = "AkuWM";
        Width = 1240;
        Height = 820;
        MinWidth = 900;
        MinHeight = 560;
        Background = Palette.BaseBrush;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        try
        {
            Icon = App.Icon();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException)
        {
            // Headless has no image decoder; the icon is cosmetic.
        }

        Sections =
        [
            new RulesSection(services),
            new StartupSection(services),
            new AppsSection(services),
            new WindowsSection(services),
            new ShortcutsSection(services),
            new MonitorsSection(services),
            new ToolsSection(services),
            new LogSection(services),
            new DoctorSection(services),
        ];

        _toastBar = new Border
        {
            Background = Palette.OverlayBrush,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 8),
            Margin = new Thickness(0, 8, 0, 0),
            IsVisible = false,
            Child = _toast,
        };
        _toastTimer.Tick += (_, _) => { _toastBar.IsVisible = false; _toastTimer.Stop(); };
        _statusTimer.Tick += (_, _) => PollDaemon();

        Content = BuildLayout();
        Closing += (_, e) =>
        {
            if (!Quitting)
            {
                e.Cancel = true;
                Hide();
            }
        };
        Opened += (_, _) => _statusTimer.Start();
        ShowSection(Sections[0].Key);
    }

    public IReadOnlyList<Section> Sections { get; }

    public Section? Current { get; private set; }

    public bool Quitting { get; set; }

    public Section SectionOf(string key) => Sections.First(s => s.Key == key);

    private Control BuildLayout()
    {
        var root = new Grid { ColumnDefinitions = new ColumnDefinitions("230,*") };

        var brand = Ui.Row(8, Ui.Text("AkuWM", "h1"), new TextBlock { Text = "●", Foreground = Palette.IrisBrush, VerticalAlignment = VerticalAlignment.Center });
        brand.Margin = new Thickness(16, 18, 16, 6);
        foreach (Section section in Sections)
        {
            var glyph = new TextBlock { Text = section.Glyph, Foreground = Palette.IrisBrush, Width = 22, FontSize = 14, VerticalAlignment = VerticalAlignment.Center };
            var row = Ui.Row(8, glyph, Ui.Col(0, Ui.Text(section.Title), new TextBlock { Text = section.Blurb, FontSize = 10, Foreground = Palette.MutedBrush, TextTrimming = TextTrimming.CharacterEllipsis }));
            _nav.Items.Add(new ListBoxItem { Content = row, Tag = section.Key, Margin = new Thickness(8, 1) });
        }

        _nav.SelectionChanged += (_, _) =>
        {
            if (!_navigating && _nav.SelectedItem is ListBoxItem { Tag: string key })
            {
                ShowSection(key);
            }
        };

        var footer = Ui.Col(4,
            Ui.Row(6, _dot, _status),
            Ui.Dim($"profile {_services.Config.Paths.Profile} · {Build.Version}"));
        footer.Margin = new Thickness(16, 8, 16, 14);
        _dot.VerticalAlignment = VerticalAlignment.Center;

        var side = new DockPanel { Background = Palette.SurfaceBrush };
        DockPanel.SetDock(brand, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        side.Children.Add(brand);
        side.Children.Add(footer);
        side.Children.Add(_nav);
        root.Children.Add(side);

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 0, 0, 14) };
        header.Children.Add(Ui.Col(2, _title, _blurb));
        var refresh = Ui.Button("Refresh", () => Current?.Refresh());
        refresh.VerticalAlignment = VerticalAlignment.Top;
        Grid.SetColumn(refresh, 1);
        header.Children.Add(refresh);

        var body = new DockPanel { Margin = new Thickness(24, 18, 24, 18) };
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(_toastBar, Dock.Bottom);
        body.Children.Add(header);
        body.Children.Add(_toastBar);
        body.Children.Add(_content);
        Grid.SetColumn(body, 1);
        root.Children.Add(body);
        return root;
    }

    public void ShowSection(string key, string? select = null)
    {
        Section section = SectionOf(key);
        if (!ReferenceEquals(section, Current))
        {
            Current?.Hidden();
            Current = section;
            _title.Text = section.Title;
            _blurb.Text = section.Blurb;
            _content.Content = section.View;
            _navigating = true;
            foreach (object? item in _nav.Items)
            {
                if (item is ListBoxItem { Tag: string tag } row && tag == key)
                {
                    _nav.SelectedItem = row;
                }
            }

            _navigating = false;
            section.Shown();
        }

        if (select is not null)
        {
            section.Select(select);
        }
    }

    /// <summary>A launch, the first or a later one handed over the pipe.</summary>
    public void Handle(LaunchArgs launch)
    {
        Visibility now = !IsVisible ? Visibility.Hidden : IsActive ? Visibility.Active : Visibility.Shown;
        if (launch.Section is { } section)
        {
            ShowSection(section, launch.Select);
        }

        switch (Launch.Decide(launch, now))
        {
            case LaunchAction.Show:
                Show();
                Activate();
                Current?.Refresh();
                FocusThroughTheDaemon();
                break;
            case LaunchAction.Activate:
                Activate();
                FocusThroughTheDaemon();
                break;
            case LaunchAction.Hide:
                Hide();
                break;
        }
    }

    /// <summary>
    /// A launch handed over the pipe reaches a process that is not the
    /// foreground one, and Windows refuses its SetForegroundWindow: the window
    /// showed and stayed behind (gui case 1, 2026-09-23). The daemon has the
    /// focus routes; it is asked by container id once it has seen the window.
    /// </summary>
    private void FocusThroughTheDaemon()
    {
        if (TryGetPlatformHandle()?.Handle is not { } handle || !OperatingSystem.IsWindows())
        {
            return;
        }

        long hwnd = handle.ToInt64();
        IDaemon daemon = _services.Daemon;
        Task.Run(() =>
        {
            for (int attempt = 0; attempt < 6; attempt++)
            {
                CommandResponse windows = daemon.Send("compat query windows");
                if (!windows.Success)
                {
                    return;
                }

                JsonNode? inner = windows.Data is JsonObject o && o["data"] is JsonNode d ? d : windows.Data;
                if (inner is JsonObject w && w["windows"] is JsonArray list)
                {
                    foreach (JsonNode? node in list)
                    {
                        if (node is JsonObject window && window["handle"]?.GetValue<long>() == hwnd && window["id"]?.GetValue<string>() is { } id)
                        {
                            daemon.Send($"compat command focus --container-id {id}");
                            return;
                        }
                    }
                }

                Thread.Sleep(150);
            }
        });
    }

    public void Toast(string text, bool error)
    {
        _toast.Text = text;
        _toast.Foreground = error ? Palette.LoveBrush : Palette.FoamBrush;
        _toastBar.IsVisible = true;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    private void PollDaemon()
    {
        if (!IsVisible)
        {
            return;
        }

        IDaemon daemon = _services.Daemon;
        Task.Run(daemon.IsRunning).ContinueWith(t =>
        {
            bool up = t.Status == TaskStatus.RanToCompletion && t.Result;
            _dot.Fill = up ? Palette.FoamBrush : Palette.LoveBrush;
            _status.Text = up ? "AkuWM is running" : "AkuWM is not running";
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }
}
