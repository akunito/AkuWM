using AkuWM.Core.Logging;
using AkuWM.Core.Model;
using AkuWM.Core.Platform;

namespace AkuWM.Core.State;

/// <param name="Handle">The window, as a number.</param>
/// <param name="Process">Who owns it, so a log line means something to a person.</param>
/// <param name="Title">What it was called when it was hidden.</param>
/// <param name="At">Seconds since the epoch.</param>
public readonly record struct CloakedWindow(long Handle, string Process, string Title, long At);

/// <summary>
/// A list, on disk, of every window AkuWM has hidden.
/// </summary>
/// <remarks>
/// <para>
/// Hiding a window by cloaking it is what lets a game keep rendering on a
/// workspace nobody is looking at. It is also a promise: a cloaked window is
/// invisible in every way a person can check -- not on screen, not on the
/// taskbar, not in Alt+Tab -- so whoever put the cloak on has to take it off,
/// and if that program dies first the window is simply gone. That has happened
/// on this desk repeatedly, every time the old manager was restarted
/// mid-test, and "open a new one" was the only recourse.
/// </para>
/// <para>
/// So the list is written <strong>before</strong> the cloak goes on and
/// rewritten after it comes off, atomically, outside the process's memory. Two
/// things then become true. AkuWM recovers its own windows on the next start
/// without being asked, however badly the previous run ended. And
/// <c>akuwm uncloak-all</c> can give back windows hidden by a run that is no
/// longer around to be asked.
/// </para>
/// <para>
/// Handles are reused by Windows, so a recovered entry is checked against the
/// window that holds that handle now: same process, and still cloaked. An
/// entry that matches nothing is dropped.
/// </para>
/// </remarks>
public sealed class CloakLedger
{
    private readonly RecordStore _store;
    private readonly Dictionary<long, CloakedWindow> _entries = [];
    private readonly object _gate = new();

    public CloakLedger(string file, int? capacity = null)
    {
        // Memory-mapped, not JSON. Measured on the desk: rewriting the file per
        // record cost 0.576 ms, paid once per window hidden and once per window
        // shown -- 9.2 ms for a workspace switch of eight, inside a 5 ms budget.
        _store = capacity is { } slots ? new RecordStore(file, slots) : new RecordStore(file);

        foreach (StoredWindow stored in _store.All())
        {
            _entries[stored.Handle] = new CloakedWindow(
                stored.Handle, stored.Process, stored.Title, stored.At);
        }
    }

    /// <summary>True when the records could not be opened: nothing here is trustworthy.</summary>
    public bool Broken => _store.Broken;

    public IReadOnlyCollection<CloakedWindow> Entries
    {
        get
        {
            lock (_gate)
            {
                return [.. _entries.Values];
            }
        }
    }

    /// <summary>Called before the cloak goes on, never after.</summary>
    /// <returns>
    /// False when the record did not reach the disk -- the store is broken or
    /// full -- in which case the cloak must not go on: a record that lives
    /// only in this process is exactly what the next crash loses.
    /// </returns>
    public bool Record(WindowSnapshot window)
    {
        lock (_gate)
        {
            var entry = new CloakedWindow(
                window.Handle.Value,
                window.ProcessName,
                window.Title,
                DateTimeOffset.UtcNow.ToUnixTimeSeconds());

            if (!_store.Put(new StoredWindow(entry.Handle, entry.Process, entry.Title, entry.At)))
            {
                return false;
            }

            _entries[entry.Handle] = entry;
            return true;
        }
    }

    /// <summary>
    /// Reads the file again: for a ledger that reports what another process
    /// -- the daemon -- is writing, such as `doctor` and `rescue`.
    /// </summary>
    public void Reload()
    {
        lock (_gate)
        {
            _store.Rescan();
            _entries.Clear();
            foreach (StoredWindow stored in _store.All())
            {
                _entries[stored.Handle] = new CloakedWindow(
                    stored.Handle, stored.Process, stored.Title, stored.At);
            }
        }
    }

    /// <summary>Called after the cloak has come off and been read back.</summary>
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
            _entries.Clear();
            _store.Clear();
        }
    }

    /// <summary>
    /// Gives back every window a previous run left hidden.
    /// </summary>
    /// <remarks>
    /// Runs at startup, before anything else touches a window: whatever went
    /// wrong last time, the desk is whole again by the time AkuWM is listening.
    /// </remarks>
    /// <param name="taskbar">
    /// Given the button back at the same time, when there is one to ask.
    /// A window AkuWM hid may also have lost its place on the bar, and a
    /// window with neither pixels nor a button is one nobody can reach. Asking
    /// for a button a window already has costs a call and changes nothing, so
    /// this does not need to know which is which.
    /// </param>
    public RecoveryResult Recover(IPlatform platform, IPlatformActions actions, ITaskbar? taskbar = null)
    {
        List<CloakedWindow> pending;
        lock (_gate)
        {
            pending = [.. _entries.Values];
        }

        if (pending.Count == 0)
        {
            return new RecoveryResult([], [], []);
        }

        var recovered = new List<CloakedWindow>();
        var failed = new List<(CloakedWindow Window, string Error)>();
        var stale = new List<CloakedWindow>();

        foreach (CloakedWindow entry in pending)
        {
            var handle = new WindowHandle(entry.Handle);
            WindowSnapshot? window = platform.Window(handle);

            // A handle Windows has handed to somebody else since, or a window
            // that is not cloaked any more: nothing to give back.
            if (window is null
                || !string.Equals(window.ProcessName, entry.Process, StringComparison.OrdinalIgnoreCase)
                || !window.Cloak.HasFlag(CloakKind.Shell))
            {
                stale.Add(entry);
                continue;
            }

            string? error = actions.SetCloak(handle, false);
            CloakKind after = platform.Window(handle)?.Cloak ?? CloakKind.None;

            if (error is null && !after.HasFlag(CloakKind.Shell))
            {
                taskbar?.ShowInTaskbar(handle, true);
                recovered.Add(entry);
                Log.Info($"recovered {entry.Process} \"{entry.Title}\" from a previous run");
            }
            else
            {
                failed.Add((entry, error ?? "the cloak is still on after the call"));
                Log.Warn($"could not recover {entry.Process} \"{entry.Title}\": {error ?? "still cloaked"}");
            }
        }

        lock (_gate)
        {
            foreach (CloakedWindow entry in recovered.Concat(stale))
            {
                _entries.Remove(entry.Handle);
                _store.Remove(entry.Handle);
            }
        }

        return new RecoveryResult(recovered, failed, stale);
    }
}

/// <param name="Recovered">Windows given back.</param>
/// <param name="Failed">Windows that refused, and why.</param>
/// <param name="Stale">Entries that no longer point at anything.</param>
public readonly record struct RecoveryResult(
    IReadOnlyList<CloakedWindow> Recovered,
    IReadOnlyList<(CloakedWindow Window, string Error)> Failed,
    IReadOnlyList<CloakedWindow> Stale)
{
    public bool Anything => Recovered.Count > 0 || Failed.Count > 0;
}
