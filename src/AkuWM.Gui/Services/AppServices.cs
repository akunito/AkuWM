using AkuWM.Core.Config;

namespace AkuWM.Gui.Services;

/// <summary>What every section is given: the daemon, the files, and a way to say something.</summary>
public sealed class AppServices
{
    public required IDaemon Daemon { get; init; }

    public required ConfigService Config { get; init; }

    /// <summary>Runs a program and returns its whole output, or null when it is not there. Replaced in tests.</summary>
    public Func<string, string, int, string?> Run { get; init; } = ProcessRunner.Capture;

    /// <summary>Starts a program the way the shell would, and says what went wrong if it did.</summary>
    public Func<string, string?> Start { get; init; } = ProcessRunner.ShellStart;

    public Action<string, bool> Toast { get; set; } = (_, _) => { };

    public static AppServices Real() => new()
    {
        Daemon = new PipeDaemon(),
        Config = new ConfigService(ConfigPaths.Discover()),
    };
}
