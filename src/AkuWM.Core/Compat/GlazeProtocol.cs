using System.Text.Json;
using System.Text.Json.Nodes;

namespace AkuWM.Core.Compat;

/// <summary>
/// The envelope every reply and every event is wrapped in.
/// </summary>
/// <remarks>
/// Captured black-box from the window manager AkuWM replaces. One field of it
/// is load-bearing in a way that is easy to miss: the scripts test for
/// <c>clientMessage</c> to decide whether the window manager answered at all,
/// because an empty reply and "that application is not running" look identical
/// otherwise -- and the second one makes a script launch a second copy of
/// whatever it was looking for.
/// </remarks>
public static class GlazeProtocol
{
    public const int Port = 6123;

    public static readonly JsonSerializerOptions Compact = new()
    {
        WriteIndented = false,

        // The scripts read this with regular expressions over the raw text; an
        // escaped "+" or "/" would not match what they were written against.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static JsonObject Reply(string request, ExecResult result) => new()
    {
        ["messageType"] = "client_response",
        ["clientMessage"] = request,
        ["data"] = result.Data?.DeepClone(),
        ["error"] = result.Error,
        ["success"] = result.Success,
    };

    public static JsonObject Event(Guid subscription, string eventType, JsonObject payload)
    {
        JsonObject data = payload.DeepClone().AsObject();
        data["eventType"] = eventType;

        return new JsonObject
        {
            ["messageType"] = "event_subscription",
            ["data"] = data,
            ["error"] = null,
            ["subscriptionId"] = subscription.ToString(),
            ["success"] = true,
        };
    }
}
