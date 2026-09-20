using AkuWM.Core.Ipc;

namespace AkuWM.Shim;

/// <summary>
/// The command-line client the existing scripts already call.
/// </summary>
/// <remarks>
/// <para>
/// <c>glazewm query workspaces</c>, <c>glazewm command focus --workspace 11</c>:
/// the same words, the same JSON back, answered by AkuWM. It exists so that
/// 1,961 lines of working AutoHotkey and two test suites keep working through
/// the milestone that replaces everything underneath them -- which means the
/// 155 checks in those suites are guarding the new window manager before its
/// own input layer exists.
/// </para>
/// <para>
/// It talks over AkuWM's named pipe rather than the WebSocket the bar uses.
/// Both reach the same model; the pipe costs a connect instead of an HTTP
/// upgrade, and this is the one a script pays for on every gesture.
/// </para>
/// </remarks>
public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("usage: glazewm <query|command> ...   (this is AkuWM's shim)");
            return 2;
        }

        // Handed on verbatim: the grammar belongs to the callers, and AkuWM
        // parses it at the far end.
        CommandResponse response = new PipeClient().Send("compat " + CommandLine.Join(args));

        if (!response.Success)
        {
            Console.Error.WriteLine(response.Error ?? "AkuWM is not running");
            return 1;
        }

        // The envelope, on one line, as the callers' regular expressions
        // expect it.
        Console.WriteLine(response.Data?.ToJsonString(AkuWM.Core.Compat.GlazeProtocol.Compact) ?? "null");

        // The reply says whether the command itself worked; the exit code has
        // to say the same, because that is what a script branches on.
        return response.Data?["success"]?.GetValue<bool>() == false ? 1 : 0;
    }
}
