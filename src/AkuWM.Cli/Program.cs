using System.Text.Json;
using AkuWM.Core.Ipc;

namespace AkuWM.Cli;

/// <summary>
/// The thin client: <c>akuwm-cli &lt;command&gt;</c>.
/// </summary>
/// <remarks>
/// The same code runs inside <c>akuwm.exe</c>, so there is one command grammar
/// and one output format whichever binary is called. This executable exists
/// for the <c>glazewm</c> shim and for scripts that must not start a daemon by
/// accident.
/// </remarks>
public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("usage: akuwm-cli <command>   (try: doctor)");
            return 2;
        }

        CommandResponse response = new PipeClient().Send(CommandLine.Join(args));
        return Print(response);
    }

    /// <summary>Prints a reply the way the CLI always does, and returns the exit code.</summary>
    public static int Print(CommandResponse response, TextWriter? output = null)
    {
        output ??= Console.Out;

        if (!response.Success)
        {
            Console.Error.WriteLine(response.Error ?? "failed");
            return 1;
        }

        if (response.Data is not null)
        {
            output.WriteLine(response.Data.ToJsonString(Protocol.Pretty));
        }

        return 0;
    }

    /// <summary>Formats a value the way a command's <c>data</c> is printed.</summary>
    public static string Format(object value) => JsonSerializer.Serialize(value, Protocol.Pretty);
}
