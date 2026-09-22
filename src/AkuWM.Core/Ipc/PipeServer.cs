using System.IO.Pipes;
using System.Text;
using AkuWM.Core.Commands;
using AkuWM.Core.Logging;

namespace AkuWM.Core.Ipc;

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
                using NamedPipeServerStream pipe = Create();

                await pipe.WaitForConnectionAsync(token).ConfigureAwait(false);
                await ServeAsync(pipe, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                // Once, then quietly: a name held by another process (a wedged
                // twin, a squatter) used to write this line five times a
                // second for as long as the daemon ran.
                if (Interlocked.Exchange(ref _complained, 1) == 0)
                {
                    Log.Error("pipe listener", ex);
                }
                else
                {
                    Log.Debug(() => $"pipe listener: {ex.Message}");
                }

                await Task.Delay(1000, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private int _complained;

    /// <summary>The longest request line accepted; anything past it is dropped, not buffered.</summary>
    public const int MaxLineBytes = 64 * 1024;

    /// <summary>
    /// One listener instance, owned by the user who started AkuWM and nobody
    /// else.
    /// </summary>
    /// <remarks>
    /// The default DACL of a named pipe is NOT "the creating user": it grants
    /// read to Everyone and Anonymous, and every process at the user's
    /// integrity level full access. The pipe carries <c>shell-exec</c> into a
    /// process installed with uiAccess, so it is the one boundary that has to
    /// be exact: the current user's SID, and nothing else. The first instance
    /// is also marked as such, so a process that pre-created the name to sit
    /// in front of AkuWM fails instead of being served first.
    /// </remarks>
    private NamedPipeServerStream Create()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new NamedPipeServerStream(
                _pipeName,
                PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);
        }

        var security = new PipeSecurity();
        using var me = System.Security.Principal.WindowsIdentity.GetCurrent();
        security.AddAccessRule(new PipeAccessRule(
            me.User!,
            PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance,
            System.Security.AccessControl.AccessControlType.Allow));

        PipeOptions options = PipeOptions.Asynchronous;
        if (Interlocked.Exchange(ref _first, 1) == 0)
        {
            options |= PipeOptions.FirstPipeInstance;
        }

        return NamedPipeServerStreamAcl.Create(
            _pipeName,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            options,
            inBufferSize: 0,
            outBufferSize: 0,
            security);
    }

    private int _first;

    private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken token)
    {
        var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 1024, leaveOpen: true);
        var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };

        string? line = await ReadLineBounded(reader, token).ConfigureAwait(false);
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

    /// <summary>A line, or null at end of stream or past <see cref="MaxLineBytes"/>.</summary>
    private static async Task<string?> ReadLineBounded(StreamReader reader, CancellationToken token)
    {
        var line = new StringBuilder();
        var one = new char[1024];

        while (true)
        {
            int read = await reader.ReadAsync(one.AsMemory(), token).ConfigureAwait(false);
            if (read <= 0)
            {
                return line.Length == 0 ? null : line.ToString();
            }

            for (int i = 0; i < read; i++)
            {
                if (one[i] == '\n')
                {
                    return line.ToString().TrimEnd('\r');
                }

                line.Append(one[i]);
            }

            if (line.Length > MaxLineBytes)
            {
                Log.Warn("a pipe client sent a request longer than 64 KB; dropped");
                return null;
            }
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
