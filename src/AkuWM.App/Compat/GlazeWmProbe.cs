using System.Diagnostics;
using AkuWM.Core.Logging;

namespace AkuWM.App.Compat;

/// <summary>
/// Asks the GlazeWM still in charge what it sees, for <c>shadow diff</c>.
/// </summary>
/// <remarks>
/// <para>
/// This is the only place in AkuWM that knows the other program exists, which
/// is why it is in <c>Compat</c> with the rest of the names AkuWM has to speak
/// rather than own. It uses its command-line interface and its documented
/// output; nothing of the program itself is read.
/// </para>
/// <para>
/// It disappears with M2: once AkuWM is the one arranging the desk there is no
/// other manager to compare against.
/// </para>
/// </remarks>
public static class GlazeWmProbe
{
    private static readonly string[] Candidates =
    [
        @"C:\Program Files\glzr.io\GlazeWM\cli\glazewm.exe",
        "glazewm.exe",
    ];

    /// <summary>Runs one query and hands back the raw reply, or null.</summary>
    public static string? Ask(string query)
    {
        foreach (string exe in Candidates)
        {
            try
            {
                using var process = Process.Start(new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = query,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });

                if (process is null)
                {
                    continue;
                }

                string output = process.StandardOutput.ReadToEnd();
                process.WaitForExit(5000);
                if (output.Length > 0)
                {
                    return output;
                }
            }
            catch (Exception ex)
            {
                Log.Debug(() => $"{exe} {query}: {ex.Message}");
            }
        }

        return null;
    }
}
