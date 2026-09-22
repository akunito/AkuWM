using AkuWM.Core.Logging;
using AkuWM.Core.Model;
using AkuWM.Core.Platform;

namespace AkuWM.Core.State;

/// <param name="Handle">The window, as a number.</param>
/// <param name="Process">Who owns it, so a recovered entry can be checked against the handle Windows holds now.</param>
/// <param name="Title">What it was called, so a log line means something to a person.</param>
/// <param name="Frame">The visible frame it had before AkuWM moved it.</param>
/// <param name="Maximized">Whether it was maximised before AkuWM took it over.</param>
/// <param name="Minimized">Whether it was minimised.</param>
/// <param name="Topmost">Whether it was in the always-on-top band.</param>
/// <param name="At">Seconds since the epoch.</param>
public readonly record struct OriginalGeometry(
    long Handle,
    string Process,
    string Title,
    Rect Frame,
    bool Maximized,
    bool Minimized,
    bool Topmost,
    long At);

/// <summary>
/// Where every window was before AkuWM touched it, on disk.
/// </summary>
/// <remarks>
/// <para>
/// The cloak ledger answers "which windows are invisible because of us".
/// This one answers the other half: "what did the desk look like before we
/// rearranged it". Together they are the promise that AkuWM can be taken out
/// of the picture -- on purpose, or by a crash, or by a reboot -- and leave
/// the desk as it found it.
/// </para>
/// <para>
/// The first touch wins. A window is remembered once, with the geometry it had
/// when AkuWM first moved it; every later move overwrites nothing, because
/// what has to be restored is the state before the window manager, not the
/// state before the last command.
/// </para>
/// <para>
/// Handles are reused by Windows, so a restored entry is checked against
/// whatever holds that handle now, by process name. An entry that matches
/// nothing is dropped rather than applied to a stranger's window.
/// </para>
/// </remarks>
public sealed class GeometryJournal
{
    private readonly RecordStore _store;
    private readonly Dictionary<long, OriginalGeometry> _entries = [];
    private readonly object _gate = new();

    public GeometryJournal(string file)
    {
        // Memory-mapped for the same reason as the cloak ledger: this is
        // written before the first move of every window, on the wm thread.
        _store = new RecordStore(file);

        foreach (StoredWindow stored in _store.All())
        {
            _entries[stored.Handle] = new OriginalGeometry(
                stored.Handle,
                stored.Process,
                stored.Title,
                stored.Frame,
                stored.Maximized,
                stored.Minimized,
                stored.Topmost,
                stored.At);
        }
    }

    /// <summary>Already remembered? Saves the caller a full window read.</summary>
    public bool Knows(WindowHandle window)
    {
        lock (_gate)
        {
            return _entries.ContainsKey(window.Value);
        }
    }

    public IReadOnlyCollection<OriginalGeometry> Entries
    {
        get
        {
            lock (_gate)
            {
                return [.. _entries.Values];
            }
        }
    }

    /// <summary>
    /// Called before AkuWM moves a window for the first time. Later calls for
    /// the same window do nothing.
    /// </summary>
    public void Remember(WindowSnapshot window)
    {
        lock (_gate)
        {
            if (_entries.ContainsKey(window.Handle.Value))
            {
                return;
            }

            var entry = new OriginalGeometry(
                window.Handle.Value,
                window.ProcessName,
                window.Title,
                window.FrameBounds,
                window.IsMaximized,
                window.IsMinimized,
                window.IsTopmost,
                DateTimeOffset.UtcNow.ToUnixTimeSeconds());

            _entries[entry.Handle] = entry;
            _store.Put(new StoredWindow(
                entry.Handle, entry.Process, entry.Title, entry.At,
                entry.Frame, entry.Maximized, entry.Minimized, entry.Topmost));
        }
    }

    /// <summary>Called when AkuWM stops being responsible for a window.</summary>
    public void Forget(WindowHandle window)
    {
        lock (_gate)
        {
            if (_entries.Remove(window.Value))
            {
                _store.Remove(window.Value);
            }
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            if (_entries.Count == 0)
            {
                return;
            }

            _entries.Clear();
            _store.Clear();
        }
    }

    /// <summary>
    /// Puts every remembered window back where it was, and empties the
    /// journal.
    /// </summary>
    /// <remarks>
    /// Runs on the way out and at the start of the next run, so it does not
    /// matter which of the two happens: whichever gets there first restores
    /// the desk, and the second finds nothing to do.
    /// </remarks>
    public GeometryRestoreResult Restore(IPlatform platform, IPlatformActions actions)
    {
        List<OriginalGeometry> pending;
        lock (_gate)
        {
            pending = [.. _entries.Values];
        }

        if (pending.Count == 0)
        {
            return new GeometryRestoreResult([], []);
        }

        var restored = new List<OriginalGeometry>();
        var stale = new List<OriginalGeometry>();
        var batch = new List<Placement>();
        IReadOnlyList<MonitorSnapshot> screens = platform.Monitors();

        foreach (OriginalGeometry entry in pending)
        {
            var handle = new WindowHandle(entry.Handle);
            WindowSnapshot? window = platform.Window(handle);

            if (window is null
                || !string.Equals(window.ProcessName, entry.Process, StringComparison.OrdinalIgnoreCase))
            {
                stale.Add(entry);
                continue;
            }

            // A rectangle on no screen at all is a corrupt record, or a
            // screen that has gone; replaying it puts a live window where
            // nobody can reach it. The store checks its magic and nothing
            // else, so this is where garbage is caught.
            if (!entry.Frame.IsEmpty && !OnAnyScreen(entry.Frame, screens))
            {
                Log.Warn($"not putting {entry.Process} back at {entry.Frame}: that is on no screen");
                stale.Add(entry);
                continue;
            }

            // Order matters. A maximised window ignores a move, and a
            // minimised one has no frame to move, so the state comes off
            // first, the rectangle goes on, and the state the window is
            // supposed to end in goes back last.
            if (window.IsMaximized || window.IsMinimized)
            {
                actions.SetMaximized(handle, false);
            }

            if (!entry.Frame.IsEmpty && !entry.Minimized && !entry.Maximized)
            {
                batch.Add(new Placement(handle, entry.Frame));
            }

            restored.Add(entry);
        }

        actions.Place(batch);

        foreach (OriginalGeometry entry in restored)
        {
            var handle = new WindowHandle(entry.Handle);

            if (entry.Maximized)
            {
                actions.SetMaximized(handle, true);
            }
            else if (entry.Minimized)
            {
                actions.SetMinimized(handle, true);
            }

            if (!entry.Topmost)
            {
                actions.SetTopmost(handle, false);
            }

            Log.Info($"put {entry.Process} \"{entry.Title}\" back at {entry.Frame}");
        }

        lock (_gate)
        {
            _entries.Clear();
            _store.Clear();
        }

        return new GeometryRestoreResult(restored, stale);
    }

    private static bool OnAnyScreen(Rect frame, IReadOnlyList<MonitorSnapshot> screens)
    {
        if (screens.Count == 0)
        {
            return true; // nothing to check against
        }

        for (int i = 0; i < screens.Count; i++)
        {
            if (frame.FractionInside(screens[i].Bounds) > 0)
            {
                return true;
            }
        }

        return false;
    }

}

/// <param name="Restored">Windows put back.</param>
/// <param name="Stale">Entries whose handle no longer belongs to the same program.</param>
public readonly record struct GeometryRestoreResult(
    IReadOnlyList<OriginalGeometry> Restored,
    IReadOnlyList<OriginalGeometry> Stale)
{
    public bool Anything => Restored.Count > 0;
}
