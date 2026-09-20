using System.IO.Pipes;
using System.Text;

namespace AkuWM.Core.Ipc;

/// <summary>
/// Talks to the running AkuWM over its named pipe.
/// </summary>
/// <remarks>
/// One connection per command: a chord that fires a hundred times a day is
/// answered inside the daemon, not here, so the cost of connecting is paid
/// only by a person typing or a script -- and being connectionless is what
/// makes the <c>glazewm</c> shim a drop-in for a CLI that also exited after
/// every call.
/// </remarks>
public sealed class PipeClient
{
    private readonly string _pipeName;

    public PipeClient(string pipeName = Protocol.PipeName) => _pipeName = pipeName;

    /// <summary>Is a daemon listening?</summary>
    public bool IsRunning(int timeoutMs = 200)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.None);
            pipe.Connect(timeoutMs);
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public CommandResponse Send(string command, int timeoutMs = 5000)
    {
        using var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.None);
        try
        {
            pipe.Connect(timeoutMs);
        }
        catch (TimeoutException)
        {
            return CommandResponse.Fail(command, "AkuWM is not running (nothing is listening on the pipe)");
        }

        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 1024, leaveOpen: true);

        writer.WriteLine(command);
        string? line = reader.ReadLine();

        return line is null
            ? CommandResponse.Fail(command, "AkuWM closed the connection without answering")
            : CommandResponse.FromLine(line);
    }
}
