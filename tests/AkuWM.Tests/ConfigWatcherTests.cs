using AkuWM.Core.Config;
using Xunit;

namespace AkuWM.Tests;

/// <summary>
/// A configuration edit on disk reaches the daemon without a restart and
/// without the settings window asking for it (2026-10-01: a git pull into the
/// Windows clone changed nothing until wm-reload-config was sent by hand).
/// </summary>
public class ConfigWatcherTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "akuwm-watch-" + Guid.NewGuid().ToString("N"));

    public ConfigWatcherTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void A_burst_of_writes_is_one_reload_and_a_rename_counts()
    {
        int fired = 0;
        using var gate = new ManualResetEventSlim();
        using var watcher = new ConfigWatcher(_dir, () => { Interlocked.Increment(ref fired); gate.Set(); }, debounceMs: 150);

        // What git and editors do: write a temp file, rename it over the real one.
        string tmp = Path.Combine(_dir, "common.json.tmp");
        File.WriteAllText(tmp, "{}");
        File.WriteAllText(tmp, "{\"version\": 1}");
        File.Move(tmp, Path.Combine(_dir, "common.json"), overwrite: true);

        Assert.True(gate.Wait(TimeSpan.FromSeconds(5)), "the watcher never fired");
        Thread.Sleep(400); // long enough for a second debounce window to have passed
        Assert.Equal(1, Volatile.Read(ref fired));
        Assert.Equal(1, watcher.Fired);

        // A second, separate edit is a second reload.
        gate.Reset();
        File.WriteAllText(Path.Combine(_dir, "DESK_W11.json"), "{\"version\": 1}");
        Assert.True(gate.Wait(TimeSpan.FromSeconds(5)), "the watcher did not fire for the second edit");
        Assert.Equal(2, Volatile.Read(ref fired));
    }

    [Fact]
    public void Files_that_are_not_json_are_ignored()
    {
        int fired = 0;
        using var watcher = new ConfigWatcher(_dir, () => Interlocked.Increment(ref fired), debounceMs: 100);
        File.WriteAllText(Path.Combine(_dir, "README.md"), "hello");
        Thread.Sleep(500);
        Assert.Equal(0, Volatile.Read(ref fired));
    }
}
