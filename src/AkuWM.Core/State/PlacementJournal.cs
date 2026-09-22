using AkuWM.Core.Logging;
using AkuWM.Core.Model;

namespace AkuWM.Core.State;

/// <summary>Where a window was, in AkuWM's terms: workspace or monitor, layer, rectangle.</summary>
/// <param name="Workspace">The workspace name, or null for a sticky window.</param>
/// <param name="StickyTo">The monitor role a sticky window follows, or null.</param>
/// <param name="Floating">Whether it was in the floating layer (a fullscreen window comes back floating).</param>
/// <param name="FloatingRect">Its floating rectangle, or empty.</param>
public readonly record struct Placed(string? Workspace, string? StickyTo, bool Floating, Rect FloatingRect);

/// <summary>
/// Where every managed window is, written as it changes, so a restart of the
/// daemon finds the desk as the person left it.
/// </summary>
/// <remarks>
/// <para>
/// Every restart used to pile every window onto the workspace its monitor
/// happened to be showing: the model is rebuilt from scratch and a window
/// that exists already is adopted like a new one. Diego, after the third
/// restart of the day (2026-09-22): the windows he had spread over the
/// workspaces were all back on 11, 21 and 31.
/// </para>
/// <para>
/// The same record store as the cloak ledger, with its fields read as this
/// class says: <c>Title</c> holds the workspace name or <c>sticky:role</c>,
/// <c>Maximized</c> means floating, <c>Frame</c> is the floating rectangle.
/// Handle-keyed, checked against the process name, so it is exact across a
/// restart of AkuWM and useless across a reboot -- where every window is a
/// new one anyway.
/// </para>
/// </remarks>
public sealed class PlacementJournal
{
    private const string StickyPrefix = "sticky:";

    private readonly RecordStore _store;
    private readonly Dictionary<long, (string Process, Placed Where)> _entries = [];
    private readonly object _gate = new();

    public PlacementJournal(string file, long? droppedBefore = null)
    {
        _store = new RecordStore(file);

        foreach (StoredWindow stored in _store.All())
        {
            // Records from before this boot name handles that belong to
            // nobody now, or worse, to somebody else.
            if (droppedBefore is { } boot && stored.At < boot)
            {
                _store.Remove(stored.Handle);
                continue;
            }

            _entries[stored.Handle] = (stored.Process, Decode(stored));
        }
    }

    public bool Broken => _store.Broken;

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    /// <summary>What a window was, if this journal knows it and the process still matches.</summary>
    public Placed? Recall(WindowHandle handle, string process)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(handle.Value, out (string Process, Placed Where) entry)
                   && string.Equals(entry.Process, process, StringComparison.OrdinalIgnoreCase)
                ? entry.Where
                : null;
        }
    }

    public void Remember(WindowHandle handle, string process, in Placed where)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(handle.Value, out (string Process, Placed Where) had) && had.Where == where)
            {
                return;
            }

            _entries[handle.Value] = (process, where);
            _store.Put(new StoredWindow(
                handle.Value,
                process,
                where.StickyTo is { } role ? StickyPrefix + role : where.Workspace ?? string.Empty,
                DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                where.FloatingRect,
                Maximized: where.Floating));
        }
    }

    public void Forget(WindowHandle handle)
    {
        lock (_gate)
        {
            if (_entries.Remove(handle.Value))
            {
                _store.Remove(handle.Value);
            }
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
            _store.Clear();
            Log.Info("placement journal cleared: every window is placed afresh");
        }
    }

    private static Placed Decode(in StoredWindow stored) =>
        stored.Title.StartsWith(StickyPrefix, StringComparison.Ordinal)
            ? new Placed(null, stored.Title[StickyPrefix.Length..], true, stored.Frame)
            : new Placed(stored.Title.Length == 0 ? null : stored.Title, null, stored.Maximized, stored.Frame);
}
