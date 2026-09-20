using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace AkuWM.Core.Ipc;

/// <summary>
/// The pipe protocol between <c>akuwm &lt;command&gt;</c> and the running
/// daemon: one request per line, one reply per line, UTF-8, no framing beyond
/// the newline.
/// </summary>
/// <remarks>
/// This is AkuWM's own envelope. The GlazeWM-compatible one that Zebar and the
/// two test suites speak is a separate serialisation in
/// <c>AkuWM.App/Compat</c>, added in M2 together with the WebSocket server on
/// 6123; keeping them apart is what stops a protocol AkuWM must imitate from
/// shaping the model behind it.
/// </remarks>
public static class Protocol
{
    /// <summary>The pipe's name. Windows sees <c>\\.\pipe\akuwm</c>.</summary>
    public const string PipeName = "akuwm";

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static readonly JsonSerializerOptions Pretty = new(Json) { WriteIndented = true };
}

/// <summary>What the daemon answers. <c>Data</c> is whatever the command produced.</summary>
public sealed class CommandResponse
{
    public bool Success { get; set; }

    /// <summary>The request, echoed, so a log of one side reads on its own.</summary>
    public string? Command { get; set; }

    public JsonNode? Data { get; set; }

    public string? Error { get; set; }

    public static CommandResponse Ok(string command, object? data = null) => new()
    {
        Success = true,
        Command = command,
        Data = data is null ? null : JsonSerializer.SerializeToNode(data, Protocol.Json),
    };

    public static CommandResponse Fail(string command, string error) => new()
    {
        Success = false,
        Command = command,
        Error = error,
    };

    public string ToLine() => JsonSerializer.Serialize(this, Protocol.Json);

    public static CommandResponse FromLine(string line) =>
        JsonSerializer.Deserialize<CommandResponse>(line, Protocol.Json)
        ?? Fail(string.Empty, "the daemon sent an empty reply");
}
