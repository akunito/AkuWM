using AkuWM.Core.Logging;

namespace AkuWM.Core.Config;

/// <summary>
/// Tells the daemon when a configuration file changes on disk. Until
/// 2026-10-01 nothing did: the compat <c>wm-reload-config</c> was the only
/// way in, the settings window sent it after its own edits, and a
/// <c>git pull</c> into the Windows clone (the way an edit from WSL or from
/// another machine arrives) changed nothing until the next restart -- while
/// two documents said the daemon watched its files.
/// </summary>
/// <remarks>
/// One callback per burst: an editor or git writes a temp file and renames
/// it, which is two to four events in a few milliseconds, and the settings
/// window writes two files in a row. The timer restarts on every event and
/// fires <see cref="DebounceMs"/> after the last one. The callback runs on a
/// timer thread; the caller posts it where it belongs.
/// </remarks>
public sealed class ConfigWatcher : IDisposable
{
    public const int DebounceMs = 400;

    private readonly FileSystemWatcher _watcher;
    private readonly Timer _debounce;
    private readonly Action _changed;
    private int _pending;

    public ConfigWatcher(string directory, Action changed, int debounceMs = DebounceMs)
    {
        _changed = changed;
        _debounce = new Timer(_ => Fire(), null, Timeout.Infinite, Timeout.Infinite);
        _watcher = new FileSystemWatcher(directory, "*.json")
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.CreationTime,
            IncludeSubdirectories = false,
        };
        _watcher.Changed += (_, e) => Touch(e.Name);
        _watcher.Created += (_, e) => Touch(e.Name);
        _watcher.Renamed += (_, e) => Touch(e.Name);
        _watcher.Deleted += (_, e) => Touch(e.Name);
        _watcher.Error += (_, e) => Log.Warn($"configuration watcher: {e.GetException().Message}");
        _debounceMs = debounceMs;
        _watcher.EnableRaisingEvents = true;
    }

    private readonly int _debounceMs;

    /// <summary>How many bursts have been reported; for tests and doctor.</summary>
    public int Fired { get; private set; }

    private void Touch(string? name)
    {
        // Snapshots and the daemon's own state live elsewhere; only the two
        // layers and anything else *.json in the directory count, and a
        // temp file git or an editor leaves for a moment counts as a change
        // of the file it will become.
        Log.Debug(() => $"configuration file {name ?? "?"} changed on disk");
        Interlocked.Exchange(ref _pending, 1);
        _debounce.Change(_debounceMs, Timeout.Infinite);
    }

    private void Fire()
    {
        if (Interlocked.Exchange(ref _pending, 0) == 1)
        {
            Fired++;
            _changed();
        }
    }

    public void Dispose()
    {
        _watcher.EnableRaisingEvents = false;
        _watcher.Dispose();
        _debounce.Dispose();
    }
}
