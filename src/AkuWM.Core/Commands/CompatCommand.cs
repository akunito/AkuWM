using System.Text.Json.Nodes;
using AkuWM.Core.Ipc;

namespace AkuWM.Core.Commands;

/// <summary>
/// <c>akuwm compat &lt;request&gt;</c>: one request in the grammar the bar and
/// the existing scripts speak, answered in their envelope.
/// </summary>
/// <remarks>
/// This is what the <c>glazewm</c> shim calls. It exists as a command rather
/// than as a second server so there is one path into the model from outside:
/// whether a request arrives on the WebSocket the bar holds open or on the
/// pipe a script connects to for one gesture, it is the same executor on the
/// same thread.
/// </remarks>
public sealed class CompatCommand
{
    private readonly Func<string, JsonObject> _ask;

    public CompatCommand(Func<string, JsonObject> ask) => _ask = ask;

    public CommandResponse Execute(string line, string[] tokens)
    {
        if (tokens.Length < 2)
        {
            return CommandResponse.Fail(line, "compat needs a request, e.g. `compat query workspaces`");
        }

        string request = CommandLine.Join(tokens[1..]);
        return CommandResponse.Ok(line, _ask(request));
    }
}
