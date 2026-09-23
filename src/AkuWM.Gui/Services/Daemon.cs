using AkuWM.Core.Ipc;

namespace AkuWM.Gui.Services;

/// <summary>The daemon, as the window sees it: one line in, one reply out.</summary>
public interface IDaemon
{
    bool IsRunning();

    CommandResponse Send(string command);
}

public sealed class PipeDaemon : IDaemon
{
    private readonly PipeClient _client = new();

    public bool IsRunning() => _client.IsRunning();

    public CommandResponse Send(string command)
    {
        try
        {
            return _client.Send(command);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
        {
            return CommandResponse.Fail(command, $"AkuWM is not answering: {ex.Message}");
        }
    }
}

/// <summary>Replies from a table; records what was asked. The tests' daemon.</summary>
public sealed class FakeDaemon : IDaemon
{
    public List<string> Sent { get; } = [];

    public Dictionary<string, CommandResponse> Replies { get; } = new(StringComparer.Ordinal);

    public bool Running { get; set; } = true;

    public bool IsRunning() => Running;

    public CommandResponse Send(string command)
    {
        Sent.Add(command);
        return Replies.TryGetValue(command, out CommandResponse? reply)
            ? reply
            : CommandResponse.Ok(command);
    }

    public FakeDaemon Answers(string command, string json)
    {
        Replies[command] = CommandResponse.Ok(command, System.Text.Json.Nodes.JsonNode.Parse(json));
        return this;
    }
}
