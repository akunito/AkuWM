using AkuWM.Gui.Services;
using AkuWM.Gui.Shell;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace AkuWM.Gui;

public sealed class App : Application
{
    public static LaunchArgs Launch { get; set; } = new(null, null, false, false, null);

    /// <summary>Set by the tests to keep the window off the pipe and off the disk.</summary>
    public static Func<AppServices>? ServicesFactory { get; set; }

    public MainWindow? Window { get; private set; }

    public override void Initialize()
    {
        RequestedThemeVariant = ThemeVariant.Dark;
        Styles.Add(new FluentTheme());
        Palette.Apply(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            AppServices services = ServicesFactory?.Invoke() ?? AppServices.Real();
            Window = new MainWindow(services);
            if (Launch.Smoke is { } smokeFile)
            {
                // The smoke runs beside the instance the tray holds: no tray
                // of its own, and it does not take the pipe from that one.
                Window.Show();
                Smoke.Run(Window, smokeFile, () => desktop.Shutdown());
            }
            else
            {
                SetTray(desktop);
                Program.Listen(Window.Handle);
                Window.Handle(Launch);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void SetTray(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var menu = new NativeMenu();
        var open = new NativeMenuItem("Open AkuWM");
        open.Click += (_, _) => Window?.Handle(new LaunchArgs(null, null, false, false, null));
        var quit = new NativeMenuItem("Quit the settings window");
        quit.Click += (_, _) => desktop.Shutdown();
        menu.Add(open);
        menu.Add(new NativeMenuItemSeparator());
        menu.Add(quit);
        var tray = new TrayIcon { ToolTipText = "AkuWM", Menu = menu, Icon = Icon() };
        tray.Clicked += (_, _) => Window?.Handle(new LaunchArgs(null, null, true, false, null));
        TrayIcon.SetIcons(this, [tray]);
    }

    public static WindowIcon Icon()
    {
        using Stream stream = typeof(App).Assembly.GetManifestResourceStream("akuwm.png")!;
        return new WindowIcon(new Bitmap(stream));
    }
}
