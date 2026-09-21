using System.Text.Json.Nodes;
using AkuWM.Core.Model;

namespace AkuWM.Core.Compat;

/// <summary>
/// Works out what to tell the bar by comparing the desk with what it was.
/// </summary>
/// <remarks>
/// <para>
/// By difference rather than raised at each call site, so a gesture that moves
/// three windows and switches a workspace produces the events that describe
/// the result, not a running commentary on how it was reached.
/// </para>
/// <para>
/// The difference is taken whether or not anyone is listening, and only the
/// payloads are skipped. Skipping the whole pass left the bookkeeping at
/// whatever it was when the last client went away, so the first change after a
/// bar connected reported EVERY window as newly managed and every displayed
/// workspace as newly activated. The bar answers each event by asking for the
/// monitors and the windows again, so one reconnect cost a query storm on the
/// wm thread: measured in the capture of 2026-09-21, ten window_managed for a
/// desk nobody had touched.
/// </para>
/// <para>
/// Nothing is allocated on a pass where nothing changed: the managed set is
/// filled into a buffer that is swapped with the last one rather than built
/// fresh, and the two differences are walked rather than produced by LINQ.
/// </para>
/// </remarks>
public sealed class GlazeEvents
{
    private readonly Dictionary<string, string> _wasDisplayed = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, MonitorSnapshot> _wasMonitor =
        new(StringComparer.OrdinalIgnoreCase);

    private HashSet<WindowHandle> _wasManaged = [];
    private HashSet<WindowHandle> _managed = [];
    private WindowHandle _wasFocused = WindowHandle.None;
    private bool _wasPaused;
    private bool _everPublished;

    /// <param name="listening">
    /// False when no client is connected: the desk is still followed, but no
    /// payload is built and <paramref name="fire"/> is never called.
    /// </param>
    public void Since(Desk.Desk desk, bool listening, Action<string, JsonObject> fire)
    {
        Monitors(desk, listening, fire);

        if (desk.Paused != _wasPaused)
        {
            _wasPaused = desk.Paused;

            if (listening)
            {
                fire("pause_changed", new JsonObject { ["isPaused"] = _wasPaused });
            }
        }

        foreach (Desk.DeskMonitor monitor in desk.Monitors)
        {
            string? now = monitor.Displayed?.Name;
            _wasDisplayed.TryGetValue(monitor.Role, out string? before);

            if (now is null || string.Equals(now, before, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (listening)
            {
                if (before is not null && desk.Workspace(before) is { } left)
                {
                    fire("workspace_deactivated", new JsonObject
                    {
                        ["deactivatedWorkspace"] = GlazeView.Workspace(desk, monitor, left),
                    });
                }

                fire("workspace_activated", new JsonObject
                {
                    ["activatedWorkspace"] = GlazeView.Workspace(desk, monitor, monitor.Displayed!),
                });
            }

            _wasDisplayed[monitor.Role] = now;
        }

        _managed.Clear();
        foreach (Desk.DeskWindow window in desk.Windows)
        {
            if (window.Managed)
            {
                _managed.Add(window.Handle);
            }
        }

        if (listening)
        {
            foreach (WindowHandle handle in _managed)
            {
                if (!_wasManaged.Contains(handle) && desk.Window(handle) is { } window)
                {
                    fire("window_managed", new JsonObject
                    {
                        ["managedWindow"] = GlazeView.Window(
                            desk, desk.Workspace(window.Workspace ?? string.Empty), window),
                    });
                }
            }

            foreach (WindowHandle handle in _wasManaged)
            {
                if (!_managed.Contains(handle))
                {
                    fire("window_unmanaged", new JsonObject { ["unmanagedHandle"] = handle.Value });
                }
            }
        }

        (_wasManaged, _managed) = (_managed, _wasManaged);

        if (desk.Focused != _wasFocused)
        {
            _wasFocused = desk.Focused;

            if (listening && desk.Window(_wasFocused) is { Managed: true } focused)
            {
                fire("focus_changed", new JsonObject
                {
                    ["focusedContainer"] = GlazeView.Window(
                        desk, desk.Workspace(focused.Workspace ?? string.Empty), focused),
                });
            }
        }

        _everPublished = true;
    }

    /// <summary>
    /// Monitors coming, going and changing shape.
    /// </summary>
    /// <remarks>
    /// The Samsung on this desk naps and comes back, and without these the bar
    /// keeps drawing pills for a screen that is not there: it refreshes only
    /// when an event arrives, and a display change produced none. The three
    /// names are from the captured sixteen; the payload key is inferred from
    /// how the others are named, which is safe here because the bar reads no
    /// payload at all -- it answers every event by asking for the monitors and
    /// the windows again (captured 2026-09-21).
    /// </remarks>
    private void Monitors(Desk.Desk desk, bool listening, Action<string, JsonObject> fire)
    {
        foreach (Desk.DeskMonitor monitor in desk.Monitors)
        {
            MonitorSnapshot now = monitor.Snapshot;

            if (!_wasMonitor.TryGetValue(monitor.Role, out MonitorSnapshot? before))
            {
                // Not on the first pass of a run: the monitors were not added,
                // they were always there, and telling the bar so would cost it
                // a full re-read of the desk for nothing.
                if (listening && _everPublished)
                {
                    fire("monitor_added", new JsonObject
                    {
                        ["addedMonitor"] = GlazeView.Monitor(desk, monitor),
                    });
                }
            }
            else if (before.Bounds != now.Bounds || before.WorkArea != now.WorkArea
                     || before.Dpi != now.Dpi)
            {
                if (listening)
                {
                    fire("monitor_updated", new JsonObject
                    {
                        ["updatedMonitor"] = GlazeView.Monitor(desk, monitor),
                    });
                }
            }

            _wasMonitor[monitor.Role] = now;
        }

        if (_wasMonitor.Count == desk.Monitors.Count)
        {
            return;
        }

        // Gone: walked only when the counts disagree, which is the only way a
        // role can have dropped out.
        List<string>? gone = null;
        foreach (string role in _wasMonitor.Keys)
        {
            if (desk.MonitorByRole(role) is null)
            {
                (gone ??= []).Add(role);
            }
        }

        if (gone is null)
        {
            return;
        }

        foreach (string role in gone)
        {
            _wasMonitor.Remove(role);

            if (listening)
            {
                fire("monitor_removed", new JsonObject { ["removedId"] = role });
            }
        }
    }
}
