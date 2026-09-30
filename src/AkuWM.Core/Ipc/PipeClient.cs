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

    /// <summary>
    /// Sends one command and waits for the answer.
    /// </summary>
    /// <remarks>
    /// Both halves have a deadline. Connecting can fail because nothing is
    /// listening, which is ordinary; the answer can fail to arrive because the
    /// daemon accepted the connection and then stopped answering, which is the
    /// failure that matters -- without a deadline on the read, every command
    /// typed at a stuck window manager would hang the terminal too, including
    /// the one that puts the desk back.
    /// </remarks>
    /// <summary>
    /// How long to wait for an answer: the commands that reach a node over
    /// ssh (a dashboard is ~15 s through the VPS) get minutes; everything
    /// else stays at five seconds, the "it may be stuck" line.
    /// </summary>
    public static int TimeoutFor(string command)
    {
        int space = command.IndexOf(' ');
        string verb = (space < 0 ? command : command[..space]).ToLowerInvariant();
        return verb is "profiles" or "git" or "nodes" or "docker" or "monitor" ? 180_000 : 5_000;
    }

    public CommandResponse Send(string command, int timeoutMs = 5000)
    {
        using var pipe = new NamedPipeClientStream(
            ".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

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

        try
        {
            using var deadline = new CancellationTokenSource(timeoutMs);
            string? line = reader.ReadLineAsync(deadline.Token).AsTask().GetAwaiter().GetResult();

            return line is null
                ? CommandResponse.Fail(command, "AkuWM closed the connection without answering")
                : CommandResponse.FromLine(line);
        }
        catch (OperationCanceledException)
        {
            return CommandResponse.Fail(
                command,
                $"AkuWM took the command and did not answer within {timeoutMs} ms. "
                + "It may be stuck; `akuwm rescue` puts the desk back without asking it.");
        }
    }
}
