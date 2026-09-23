using AkuWM.Core.Ipc;
using AkuWM.Gui.Services;
using AkuWM.Gui.Shell;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace AkuWM.Gui.Sections;

/// <summary>The tail of the daemon's log, re-read every second while shown, with the debug switch.</summary>
public sealed class LogSection : Section
{
    private const int TailBytes = 96 * 1024;

    private readonly TextBox _text = new() { IsReadOnly = true, AcceptsReturn = true, FontFamily = new FontFamily("Cascadia Mono,Consolas,monospace"), FontSize = 12, TextWrapping = TextWrapping.NoWrap };
    private readonly TextBox _filter = new() { PlaceholderText = "only lines containing", Width = 260 };
    private readonly ToggleSwitch _debug = new() { OnContent = "debug on", OffContent = "debug off" };
    private readonly TextBlock _note = Ui.Dim(string.Empty);
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private long _lastLength = -1;
    private string _lastFilter = string.Empty;
    private bool _settingSwitch;

    public LogSection(AppServices services)
        : base(services)
    {
        _timer.Tick += (_, _) => Refresh();
        _debug.IsCheckedChanged += (_, _) =>
        {
            if (!_settingSwitch)
            {
                SetDebug(_debug.IsChecked == true);
            }
        };
        _filter.TextChanged += (_, _) => { _lastLength = -1; Refresh(); };
    }

    public override string Key => "log";

    public override string Title => "Log";

    public override string Blurb => "the daemon's log, live";

    public override string Glyph => "≣";

    public string LogFile => Path.Combine(Services.Config.Paths.LogDir, "akuwm.log");

    protected override Control Build()
    {
        var bar = Ui.Row(12, _filter, _debug, _note);
        var dock = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        bar.Margin = new Avalonia.Thickness(0, 0, 0, 10);
        dock.Children.Add(bar);
        dock.Children.Add(_text);
        return dock;
    }

    public override void Shown()
    {
        _settingSwitch = true;
        _debug.IsChecked = File.Exists(Services.Config.Paths.DebugMarkerFile);
        _settingSwitch = false;
        _lastLength = -1;
        Refresh();
        _timer.Start();
    }

    public override void Hidden() => _timer.Stop();

    public override void Refresh()
    {
        string file = LogFile;
        if (!File.Exists(file))
        {
            _text.Text = string.Empty;
            _note.Text = $"no log at {file}";
            return;
        }

        long length = new FileInfo(file).Length;
        string filter = _filter.Text ?? string.Empty;
        if (length == _lastLength && filter == _lastFilter)
        {
            return;
        }

        _lastLength = length;
        _lastFilter = filter;
        _text.Text = Filtered(Tail(file), filter);
        _text.CaretIndex = _text.Text.Length;
        _note.Text = $"{file} · {length / 1024} KB";
    }

    public static string Tail(string file)
    {
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        long from = Math.Max(0, stream.Length - TailBytes);
        stream.Seek(from, SeekOrigin.Begin);
        using var reader = new StreamReader(stream);
        string text = reader.ReadToEnd();
        if (from > 0)
        {
            int newline = text.IndexOf('\n');
            if (newline >= 0)
            {
                text = text[(newline + 1)..];
            }
        }

        return text;
    }

    public static string Filtered(string text, string filter)
    {
        if (filter.Length == 0)
        {
            return text;
        }

        var kept = new System.Text.StringBuilder(text.Length / 4);
        foreach (string line in text.Split('\n'))
        {
            if (line.Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                kept.Append(line).Append('\n');
            }
        }

        return kept.ToString();
    }

    /// <summary>The daemon flips its level and keeps the marker; without a daemon the marker alone is what the next start reads.</summary>
    public void SetDebug(bool on)
    {
        if (Services.Daemon.IsRunning())
        {
            CommandResponse reply = Services.Daemon.Send(on ? "debug on" : "debug off");
            Toast(reply.Success ? (on ? "debug on, live" : "debug off, live") : reply.Error ?? "refused", !reply.Success);
            return;
        }

        string marker = Services.Config.Paths.DebugMarkerFile;
        try
        {
            if (on)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
                File.WriteAllText(marker, string.Empty);
            }
            else if (File.Exists(marker))
            {
                File.Delete(marker);
            }

            Toast("AkuWM is not running: the marker is set for its next start");
        }
        catch (IOException ex)
        {
            Toast(ex.Message, error: true);
        }
    }
}
