using System.IO.Pipes;
using System.Text;
using Avalonia;
using Avalonia.Threading;

namespace AkuWM.Gui;

public static class Program
{
    /// <summary>The GUI's own pipe: a second launch hands its arguments to the first and exits.</summary>
    public const string PipeName = "akuwm-gui";

    [STAThread]
    public static int Main(string[] args)
    {
        LaunchArgs launch = LaunchArgs.Parse(args);
        if (!LaunchArgs.ValidSection(launch.Section))
        {
            Console.Error.WriteLine($"'{launch.Section}' is not a section: {string.Join(", ", LaunchArgs.Sections)}");
            return 2;
        }

        if (launch.Smoke is null && HandOff(launch))
        {
            return 0;
        }

        App.Launch = launch;
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont();

    private static bool HandOff(LaunchArgs launch)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            pipe.Connect(300);
            byte[] bytes = Encoding.UTF8.GetBytes(launch.ToLine() + "\n");
            pipe.Write(bytes, 0, bytes.Length);
            pipe.Flush();
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Listens for later launches, for the life of the process.</summary>
    public static void Listen(Action<LaunchArgs> onLaunch)
    {
        var thread = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1);
                    server.WaitForConnection();
                    using var reader = new StreamReader(server, Encoding.UTF8);
                    string? line = reader.ReadLine();
                    if (line is not null)
                    {
                        LaunchArgs launch = LaunchArgs.FromLine(line);
                        Dispatcher.UIThread.Post(() => onLaunch(launch));
                    }
                }
                catch (IOException)
                {
                    Thread.Sleep(200);
                }
            }
        })
        { IsBackground = true, Name = "akuwm-gui-pipe" };
        thread.Start();
    }
}
