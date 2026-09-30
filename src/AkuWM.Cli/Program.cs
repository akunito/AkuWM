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

        Utf8Console();
        return Print(new PipeClient().Send(CommandLine.Join(args)));
    }

    /// <summary>
    /// The console's default is the OEM code page: a title's bullet came out
    /// as byte 0x07 (CP437 draws it as a bullet) and an accented name as '?',
    /// so `query windows` was not JSON to any strict reader (2026-09-30).
    /// </summary>
    public static void Utf8Console()
    {
        try
        {
            Console.OutputEncoding = new System.Text.UTF8Encoding(false);
        }
        catch (IOException)
        {
            // No console at all: nothing to set.
        }
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
}
