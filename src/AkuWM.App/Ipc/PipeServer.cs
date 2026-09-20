using System.IO.Pipes;
using System.Text;
using AkuWM.App.Commands;
using AkuWM.Core.Ipc;
using AkuWM.Core.Logging;

namespace AkuWM.App.Ipc;

/// <summary>
/// The named pipe the CLI and the <c>glazewm</c> shim talk to.
/// </summary>
/// <remarks>
/// <para>
/// One line in, one line out, one client at a time on each instance; several
/// instances are kept open so a chord never waits behind a script. The pipe is
/// created with the default ACL, which on Windows means the user who started
/// AkuWM -- nothing else on the machine can drive the window manager.
/// </para>
/// <para>
/// From M2 the commands are posted to the wm thread and awaited; in M0 the
/// router answers on the connection's own thread, because nothing it does
/// touches a model yet.
/// </para>
/// </remarks>
public sealed class PipeServer : IAsyncDisposable
{
    private readonly string _pipeName;
    private readonly CommandRouter _router;
    private readonly CancellationTokenSource _stopping = new();
    private readonly List<Task> _listeners = [];

    public PipeServer(CommandRouter router, string pipeName = Protocol.PipeName)
    {
        _router = router;
        _pipeName = pipeName;
    }

    /// <summary>Raised when a client asks AkuWM to stop.</summary>
    public event Action? ExitRequested;

    public void Start(int instances = 4)
    {
        for (int i = 0; i < instances; i++)
        {
            _listeners.Add(Task.Run(() => ListenAsync(_stopping.Token)));
        }

        Log.Info($"pipe {_pipeName} listening ({instances} instances)");
    }

    private async Task ListenAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await pipe.WaitForConnectionAsync(token).ConfigureAwait(false);
                await ServeAsync(pipe, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Log.Error("pipe listener", ex);
                await Task.Delay(200, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken token)
    {
        var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 1024, leaveOpen: true);
        var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };

        string? line = await reader.ReadLineAsync(token).ConfigureAwait(false);
        if (line is null)
        {
            // A connection with nothing on it: that is how the CLI asks whether
            // AkuWM is up at all.
            return;
        }

        CommandResponse response = _router.Execute(line);
        await writer.WriteLineAsync(response.ToLine().AsMemory(), token).ConfigureAwait(false);

        if (CommandLine.Split(line).FirstOrDefault()?.Equals("exit", StringComparison.OrdinalIgnoreCase) == true)
        {
            ExitRequested?.Invoke();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync().ConfigureAwait(false);

        // A listener is parked inside WaitForConnectionAsync; cancelling it is
        // enough, but a client that connects in the same instant is served
        // first, which is why the wait is bounded rather than awaited forever.
        await Task.WhenAny(Task.WhenAll(_listeners), Task.Delay(1000)).ConfigureAwait(false);
        _stopping.Dispose();
    }
}
