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
        // Taken, not copied. A JsonNode cannot have two parents, and the data
        // of a reply is built fresh for that reply and used once -- so the
        // clone was a second full copy of a tree that for this desk is
        // thousands of nodes, on every query, and the bar sends one per event
        // per widget. Cloned only if it already belongs to somebody.
        ["data"] = result.Data is { Parent: not null } shared ? shared.DeepClone() : result.Data,
        ["error"] = result.Error,
        ["success"] = result.Success,
    };

    /// <summary>
    /// One event frame. The caller owns <paramref name="payload"/> afterwards.
    /// </summary>
    /// <remarks>
    /// Built once per event, not once per subscriber: the only thing that
    /// differs between subscribers is the id, and <see cref="Subscription"/>
    /// changes it in place. Building it per target meant cloning and
    /// re-serialising the whole payload for every widget on the bar.
    /// </remarks>
    public static JsonObject Event(Guid subscription, string eventType, JsonObject payload)
    {
        payload["eventType"] = eventType;

        return new JsonObject
        {
            ["messageType"] = "event_subscription",
            ["data"] = payload,
            ["error"] = null,
            ["subscriptionId"] = subscription.ToString(),
            ["success"] = true,
        };
    }

    /// <summary>Re-aims a frame at another subscription.</summary>
    public static void Subscription(JsonObject frame, Guid subscription) =>
        frame["subscriptionId"] = subscription.ToString();
}
