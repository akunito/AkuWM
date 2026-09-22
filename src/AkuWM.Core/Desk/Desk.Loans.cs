using AkuWM.Core.Layout;
using AkuWM.Core.Logging;
using AkuWM.Core.Model;

namespace AkuWM.Core.Desk;

/// <summary>
/// A monitor that goes away lends its windows, or its workspaces, to a
/// monitor that is here; when it is back, the loan is undone exactly or
/// consolidated, by <c>layout.when_monitor_returns</c>.
/// </summary>
/// <remarks>
/// <para>
/// Nothing of the absent monitor's state is thrown away while it is gone:
/// each workspace's tree, floating rectangles, layers and displayed flag are
/// copied at the moment of loss, and the sticky set with its rectangles. So
/// "restore" is not a reconstruction, it is the copy put back -- whatever the
/// person did with the windows in between, which is what Diego asked for
/// (2026-09-22): the position they had when the monitor went, not the one
/// they have now. A window is taken back only if it is still where the loan
/// put it; one moved on purpose meanwhile is left alone, as is one that
/// closed.
/// </para>
/// <para>
/// Windows itself has already moved the windows onto the remaining screens
/// by the time the change is heard; what the loan decides is which workspace
/// they belong to while the screen is dark.
/// </para>
/// </remarks>
public sealed partial class Desk
{
    private sealed class LoanedWorkspace
    {
        public required Workspace Workspace { get; init; }
        public required string Role { get; init; }
        public required bool Displayed { get; init; }
        public required TilingTree Tree { get; init; }
        public required List<WindowHandle> Floating { get; init; }
        public required WindowHandle Fullscreen { get; init; }
        public required Dictionary<WindowHandle, (Rect? Floating, WindowState State, WindowState Previous)> Windows { get; init; }
    }

    private sealed class Loan
    {
        public required string Role { get; init; }
        public required string Host { get; init; }
        public required bool Workspaces { get; init; }
        public required List<LoanedWorkspace> Lent { get; init; }
        public required List<(WindowHandle Window, Rect? Floating)> Sticky { get; init; }
        public required Workspace? HostWorkspace { get; init; }
    }

    private readonly Dictionary<string, Loan> _loans = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The roles whose windows are on loan, for doctor and the tests.</summary>
    public IReadOnlyCollection<string> OnLoan => _loans.Keys;

    private string LeavesPolicy => (Config.Layout?.WhenMonitorLeaves ?? "move_windows").ToLowerInvariant().Replace('-', '_');

    private string ReturnsPolicy => (Config.Layout?.WhenMonitorReturns ?? "restore").ToLowerInvariant();

    private void Lend(string role)
    {
        string policy = LeavesPolicy;
        if (policy == "leave" || _loans.ContainsKey(role) || !_byRole.TryGetValue(role, out DeskMonitor? away))
        {
            return;
        }

        DeskMonitor? host = FocusedMonitor;
        if (host is null || string.Equals(host.Role, role, StringComparison.OrdinalIgnoreCase))
        {
            host = _monitors.FirstOrDefault(m => !string.Equals(m.Role, role, StringComparison.OrdinalIgnoreCase));
        }

        if (host is null)
        {
            return; // every screen is gone; there is nowhere to lend to
        }

        bool wholeWorkspaces = policy == "move_workspaces";
        var lent = new List<LoanedWorkspace>();

        foreach (Workspace workspace in _workspaces.Values)
        {
            if (!string.Equals(workspace.MonitorRole, role, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var windows = new Dictionary<WindowHandle, (Rect?, WindowState, WindowState)>();
            foreach (WindowHandle handle in workspace.Windows)
            {
                if (Window(handle) is { } window)
                {
                    windows[handle] = (window.FloatingRect, window.State, window.PreviousState);
                }
            }

            lent.Add(new LoanedWorkspace
            {
                Workspace = workspace,
                Role = workspace.MonitorRole,
                Displayed = workspace.Displayed,
                Tree = workspace.Tiling.Clone(),
                Floating = [.. workspace.Floating],
                Fullscreen = workspace.Fullscreen,
                Windows = windows,
            });
        }

        var sticky = new List<(WindowHandle, Rect?)>();
        foreach (WindowHandle handle in away.Sticky.ToList())
        {
            if (Window(handle) is { } window)
            {
                sticky.Add((handle, window.FloatingRect));
            }
        }

        Workspace? hostWorkspace = host.Displayed;

        if (wholeWorkspaces)
        {
            // The workspaces themselves go to the host, put away: a screen
            // that already shows a workspace is not asked to show two.
            foreach (LoanedWorkspace item in lent)
            {
                item.Workspace.MonitorRole = host.Role;
                item.Workspace.Displayed = false;
            }

            BindWorkspaces();
        }
        else if (hostWorkspace is not null)
        {
            foreach (LoanedWorkspace item in lent)
            {
                foreach (WindowHandle handle in item.Windows.Keys)
                {
                    if (Window(handle) is not { } window)
                    {
                        continue;
                    }

                    // A window that covered its screen floats on the host; the
                    // host's own layers are the host's.
                    if (window.State == WindowState.Fullscreen)
                    {
                        window.State = WindowState.Floating;
                        window.FloatingRect ??= window.Snapshot.FrameBounds;
                    }

                    if (window.FloatingRect is { } rect)
                    {
                        window.FloatingRect = AcrossMonitors.Map(
                            rect, away.TilingArea, host.TilingArea, Across(window.State == WindowState.Tiling, dragged: false));
                    }

                    Place(window, hostWorkspace);
                }
            }
        }

        // Sticky windows follow a monitor; while theirs is gone, this one.
        foreach ((WindowHandle handle, Rect? rect) in sticky)
        {
            if (Window(handle) is { } window)
            {
                if (rect is { } r)
                {
                    window.FloatingRect = AcrossMonitors.Map(r, away.TilingArea, host.TilingArea, Across(false, dragged: false));
                }

                MakeSticky(window, host);
            }
        }

        _loans[role] = new Loan
        {
            Role = role,
            Host = host.Role,
            Workspaces = wholeWorkspaces,
            Lent = lent,
            Sticky = sticky,
            HostWorkspace = hostWorkspace,
        };

        int count = lent.Sum(l => l.Windows.Count) + sticky.Count;
        Log.Info($"monitor {role} is gone: {(wholeWorkspaces ? $"{lent.Count} workspace(s)" : $"{count} window(s)")} lent to {host.Role}");
    }

    private void Reclaim(DeskMonitor back)
    {
        if (!_loans.Remove(back.Role, out Loan? loan))
        {
            return;
        }

        bool restore = ReturnsPolicy != "keep";
        DeskMonitor? host = MonitorByRole(loan.Host);

        if (loan.Workspaces)
        {
            foreach (LoanedWorkspace item in loan.Lent)
            {
                item.Workspace.MonitorRole = item.Role;
                item.Workspace.Displayed = item.Displayed;

                foreach ((WindowHandle handle, (Rect? floating, _, _)) in item.Windows)
                {
                    if (Window(handle) is not { } window || window.FloatingRect is not { } now)
                    {
                        continue;
                    }

                    // Restore: the rectangle it had. Keep: the one it has,
                    // brought along to the returning screen.
                    window.FloatingRect = restore
                        ? floating ?? now
                        : host is null ? now : AcrossMonitors.Map(now, host.TilingArea, back.TilingArea, Across(false, dragged: false));
                }
            }

            BindWorkspaces();
        }
        else if (restore)
        {
            foreach (LoanedWorkspace item in loan.Lent)
            {
                Workspace home = item.Workspace;

                foreach ((WindowHandle handle, (Rect? floating, WindowState state, WindowState previous)) in item.Windows)
                {
                    // Still where the loan put it, and still there at all;
                    // anything else was the person's doing since, and stands.
                    if (Window(handle) is not { Managed: true } window
                        || window.Sticky
                        || !string.Equals(window.Workspace, loan.HostWorkspace?.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (window.Workspace is { } current)
                    {
                        Workspace(current)?.Release(handle);
                    }

                    window.Workspace = home.Name;
                    window.State = state;
                    window.PreviousState = previous;
                    window.FloatingRect = floating;
                }

                home.Tiling.Restore(item.Tree, h => Window(h) is { Managed: true } w && string.Equals(w.Workspace, home.Name, StringComparison.OrdinalIgnoreCase));
                home.Floating.Clear();
                foreach (WindowHandle handle in item.Floating)
                {
                    if (Window(handle) is { Managed: true } w && string.Equals(w.Workspace, home.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        home.Floating.Add(handle);
                    }
                }

                home.Fullscreen = Window(item.Fullscreen) is { Managed: true } f && string.Equals(f.Workspace, home.Name, StringComparison.OrdinalIgnoreCase)
                    ? item.Fullscreen
                    : WindowHandle.None;
                home.Displayed = item.Displayed;

                foreach (WindowHandle handle in home.Windows)
                {
                    home.Touch(handle);
                }
            }
        }

        // Sticky windows go back to the screen they follow, at the rectangle
        // they had (restore) or the one they have, brought along (keep).
        foreach ((WindowHandle handle, Rect? rect) in loan.Sticky)
        {
            if (Window(handle) is not { Managed: true, Sticky: true } window
                || !string.Equals(window.StickyMonitor, loan.Host, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (restore)
            {
                window.FloatingRect = rect ?? window.FloatingRect;
            }
            else if (host is not null && window.FloatingRect is { } now)
            {
                window.FloatingRect = AcrossMonitors.Map(now, host.TilingArea, back.TilingArea, Across(false, dragged: false));
            }

            MakeSticky(window, back);
        }

        if (back.Displayed is null && back.Workspaces.Count > 0)
        {
            back.Workspaces[0].Displayed = true;
        }

        Log.Info($"monitor {back.Role} is back: its loan to {loan.Host} is {(restore ? "undone, everything where it was" : "kept, the windows stay where they are")}");
    }
}
