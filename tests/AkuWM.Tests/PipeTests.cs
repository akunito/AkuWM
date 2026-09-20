using AkuWM.App.Commands;
using AkuWM.App.Ipc;
using AkuWM.Cli;
using AkuWM.Core.Config;
using AkuWM.Core.Ipc;
using Xunit;

namespace AkuWM.Tests;

/// <summary>
/// The pipe, end to end. .NET maps a named pipe onto a Unix socket, so this
/// runs on Linux as well as on the desk -- the transport is exercised in CI,
/// and only its Windows ACL is left to the machine itself.
/// </summary>
public class PipeTests
{
    private static string UniqueName() => "akuwm-test-" + Guid.NewGuid().ToString("n")[..12];

    private static CommandRouter Router(TempDir dir)
    {
        var paths = new ConfigPaths(dir.Path, "TEST", dir.Path);
        return new CommandRouter(new ConfigCommands(paths), new DoctorCommand(paths, () => true));
    }

    [Fact]
    public async Task ACommandGoesDownThePipeAndTheAnswerComesBack()
    {
        using var dir = new TempDir();
        string name = UniqueName();
        await using var server = new PipeServer(Router(dir), name);
        server.Start(instances: 2);

        CommandResponse response = new PipeClient(name).Send("version");

        Assert.True(response.Success, response.Error);
        Assert.Equal("version", response.Command);
        Assert.NotNull(response.Data!["version"]);
    }

    [Fact]
    public async Task AFailingCommandComesBackAsAFailureNotAnException()
    {
        using var dir = new TempDir();
        string name = UniqueName();
        await using var server = new PipeServer(Router(dir), name);
        server.Start(instances: 1);

        CommandResponse response = new PipeClient(name).Send("nonsense");

        Assert.False(response.Success);
        Assert.NotNull(response.Error);
    }

    [Fact]
    public async Task SeveralCommandsInARowAreServed()
    {
        using var dir = new TempDir();
        string name = UniqueName();
        await using var server = new PipeServer(Router(dir), name);
        server.Start(instances: 2);

        var client = new PipeClient(name);
        for (int i = 0; i < 5; i++)
        {
            Assert.True(client.Send("version").Success);
        }
    }

    [Fact]
    public void WithNothingListeningTheClientSaysSoQuickly()
    {
        CommandResponse response = new PipeClient(UniqueName()).Send("version", timeoutMs: 300);

        Assert.False(response.Success);
        Assert.Contains("not running", response.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IsRunningTellsTheTwoApart()
    {
        using var dir = new TempDir();
        string name = UniqueName();

        Assert.False(new PipeClient(name).IsRunning(300));

        await using var server = new PipeServer(Router(dir), name);
        server.Start(instances: 1);

        Assert.True(new PipeClient(name).IsRunning(2000));
    }

    [Fact]
    public async Task ExitIsAnsweredAndThenAsksTheDaemonToStop()
    {
        using var dir = new TempDir();
        string name = UniqueName();
        await using var server = new PipeServer(Router(dir), name);
        var asked = new TaskCompletionSource();
        server.ExitRequested += () => asked.TrySetResult();
        server.Start(instances: 1);

        Assert.True(new PipeClient(name).Send("exit").Success);

        await asked.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
