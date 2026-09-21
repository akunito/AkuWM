using AkuWM.Core.Logging;
using Xunit;

namespace AkuWM.Tests;

/// <summary>
/// The log has to look after itself, because debug tracing is left on.
/// </summary>
public class LogTests
{
    [Fact]
    public void A_running_log_rolls_over_instead_of_growing_for_ever()
    {
        string dir = Path.Combine(
            Path.GetTempPath(), "akuwm-log-" + Guid.NewGuid().ToString("n")[..12]);

        try
        {
            // Rotation used to happen only when the daemon STARTED. With debug
            // tracing on the log is megabytes an hour -- every frame the bar
            // exchanges goes through it -- and a daemon that runs for days
            // never got the chance.
            Log.ToDirectory(dir, maxBytes: 4096);
            Log.Level = LogLevel.Info;

            for (int i = 0; i < 400; i++)
            {
                Log.Info($"a line long enough to matter, number {i}, padded {new string('x', 60)}");
            }

            string live = Path.Combine(dir, "akuwm.log");
            Assert.True(File.Exists(live));
            Assert.True(File.Exists(live + ".1"), "the previous log should have been kept");

            // The live one is bounded; without rolling it would hold all 400.
            Assert.True(new FileInfo(live).Length <= 4096 * 2,
                $"the live log is {new FileInfo(live).Length} bytes");
        }
        finally
        {
            Log.ToConsoleOnly();
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }
}
