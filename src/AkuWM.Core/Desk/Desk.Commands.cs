using AkuWM.Core.Layout;
using AkuWM.Core.Logging;
using AkuWM.Core.Model;

namespace AkuWM.Core.Desk;

/// <summary>
/// The gestures: what a chord, a CLI command or the bar asks the desk to do.
/// </summary>
/// <remarks>
/// Every one of them changes the model and nothing else. The screen catches up
/// when the caller computes a <see cref="Redraw"/> and applies it, which means
/// two commands in a row cost one batch of window moves rather than two, and a
/// command that turns out to be a no-op costs nothing at all.
/// </remarks>
public sealed partial class Desk
{
    /// <summary>Records where the focus actually is, from the OS.</summary>
    /// <remarks>
    /// A foreground event for a window AkuWM has hidden is not a focus change
    /// to believe: it happens when something raises a cloaked window, and
    /// following it would leave the keyboard pointing at a window nobody can
    /// see. The caller is told to put the focus back.
    /// </remarks>
    public bool Focus(WindowHandle handle)
    {
        DeskWindow? window = Window(handle);
        if (window is null || !window.Managed)
        {
            Focused = handle;
            return true;
        }

        if (window.Hidden)
        {
            // Refusing to believe it is half the job; the other half is taking
            // the keyboard off the invisible window, which otherwise swallows
            // every keystroke.
            FocusSomethingVisible();
            return false;
        }

        Focused = handle;
        LookingAt(MonitorByHandle(window.Snapshot.Monitor));

        if (window.Workspace is { } name && Workspace(name) is { } workspace)
        {
            workspace.Touch(handle);
        }

        return true;
    }

    /// <summary>
    /// Asks for the focus to land on something the person can see.
    /// </summary>
    /// <remarks>
    /// Called when something raised a window AkuWM has hidden -- a taskbar
    /// click, a toast, an app calling SetForegroundWindow on itself. Refusing
    /// to believe it is half the job; the other half is taking the keyboard
    /// off the invisible window, which otherwise swallows every keystroke.
    /// </remarks>
    public bool FocusSomethingVisible()
    {
        DeskMonitor? monitor = FocusedMonitor;
        WindowHandle target = monitor?.Displayed?.LastFocused ?? WindowHandle.None;

        if (target.IsNone || Window(target) is not { Managed: true, Hidden: false })
        {
            for (int i = 0; i < _monitors.Count; i++)
            {
                if (_monitors[i].Displayed?.LastFocused is { IsNone: false } other
                    && Window(other) is { Managed: true, Hidden: false })
                {
                    target = other;
                    break;
                }
            }
        }

        if (target.IsNone)
        {
            return false;
        }

        WantFocus(target);
        return true;
    }

    /// <summary>Shows a workspace on its monitor, hiding whatever was there.</summary>
    /// <returns>The window that should end up with the focus.</returns>
    public WindowHandle FocusWorkspace(string name)
    {
        if (Workspace(name) is not { } workspace)
        {
            return WindowHandle.None;
        }

        DeskMonitor? monitor = MonitorOf(workspace);
        if (monitor is null)
        {
            Log.Warn($"workspace {name} belongs to the monitor role '{workspace.MonitorRole}', which is not here");
            return WindowHandle.None;
        }

        // Before anything else, including the back-and-forth below: asking for
        // a workspace is saying where you are, and it is true even when that
        // workspace was ALREADY the one showing on the other monitor -- which
        // is the ordinary way of hopping screens, and used to leave the desk
        // believing the person was still on the first one.
        LookingAt(monitor);

        Workspace? outgoing = monitor.Displayed;

        // Asking for the workspace you are already on goes back to the one
        // before it -- sway's workspace_auto_back_and_forth.
        if (ReferenceEquals(outgoing, workspace))
        {
            if (Config.General?.ToggleWorkspaceOnRefocus != true
                || monitor.Previous is not { } previous
                || Workspace(previous) is not { } back
                || ReferenceEquals(back, workspace))
            {
                return workspace.LastFocused;
            }

            workspace = back;
        }

        if (outgoing is not null && !ReferenceEquals(outgoing, workspace))
        {
            outgoing.Displayed = false;
            monitor.Previous = outgoing.Name;
        }

        foreach (Workspace other in monitor.Workspaces)
        {
            other.Displayed = ReferenceEquals(other, workspace);
        }

        return workspace.LastFocused;
    }

    /// <summary>Moves a window to another workspace, wherever that workspace lives.</summary>
    public bool MoveToWorkspace(WindowHandle handle, string name)
    {
        if (Window(handle) is not { Managed: true } window || Workspace(name) is not { } destination)
        {
            return false;
        }

        if (window.Sticky)
        {
            // A sticky window has no workspace to move between; it follows its
            // monitor. Moving it to a workspace means it stops being sticky.
            SetSticky(handle, false);
        }

        if (string.Equals(window.Workspace, name, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        Place(window, destination);
        return true;
    }

    /// <summary>The window in that direction, crossing to the next monitor when there is none.</summary>
    public WindowHandle InDirection(Direction direction)
    {
        if (Window(Focused) is not { Managed: true } window || window.Workspace is not { } name
            || Workspace(name) is not { } workspace || MonitorOf(workspace) is not { } monitor)
        {
            return WindowHandle.None;
        }

        WindowHandle neighbour = workspace.Tiling.Neighbour(
            Focused, direction, monitor.TilingArea, GapsFor(monitor));

        if (!neighbour.IsNone)
        {
            return neighbour;
        }

        // Off the edge of this workspace: sway's `focus output`.
        return MonitorInDirection(monitor, direction)?.Displayed?.LastFocused ?? WindowHandle.None;
    }

    /// <summary>Moves the focused window one place, or onto the next monitor.</summary>
    public bool MoveFocused(Direction direction)
    {
        if (Window(Focused) is not { Managed: true } window || window.Workspace is not { } name
            || Workspace(name) is not { } workspace || MonitorOf(workspace) is not { } monitor)
        {
            return false;
        }

        if (window.State == WindowState.Tiling
            && workspace.Tiling.Move(Focused, direction, monitor.TilingArea, GapsFor(monitor)))
        {
            return true;
        }

        if (MonitorInDirection(monitor, direction)?.Displayed is { } destination)
        {
            Place(window, destination);
            return true;
        }

        return false;
    }

    /// <summary>The next monitor that way, by where the screens actually are.</summary>
    public DeskMonitor? MonitorInDirection(DeskMonitor from, Direction direction)
    {
        Rect here = from.Snapshot.Bounds;

        return _monitors
            .Where(m => !ReferenceEquals(m, from))
            .Select(m => (Monitor: m, Distance: Ahead(here, m.Snapshot.Bounds, direction)))
            .Where(c => c.Distance >= 0)
            .OrderBy(c => c.Distance)
            .Select(c => c.Monitor)
            .FirstOrDefault();
    }

    private static int Ahead(Rect source, Rect other, Direction direction)
    {
        int gap = direction switch
        {
            Direction.Left => source.Left - other.Right,
            Direction.Right => other.Left - source.Right,
            Direction.Up => source.Top - other.Bottom,
            _ => other.Top - source.Bottom,
        };

        return gap >= 0 ? gap : -1;
    }

    /// <summary>Moves an edge of the focused window, in points of its split.</summary>
    /// <summary>
    /// Resizes by a number of pixels rather than a share of the workspace.
    /// </summary>
    /// <remarks>
    /// What an Alt+drag sends: it knows exactly how far the pointer went, and
    /// turning that into a percentage at the far end would need the monitor,
    /// which the caller has and the command grammar does not carry.
    /// </remarks>
    public bool ResizeByPixels(WindowHandle handle, Direction direction, int pixels)
    {
        if (Window(handle) is not { Managed: true } window)
        {
            return false;
        }

        if (window.State != WindowState.Tiling)
        {
            return ResizeFloating(window, direction, pixels);
        }

        if (window.Workspace is not { } on || Workspace(on) is not { } where
            || MonitorOf(where) is not { } monitor)
        {
            return false;
        }

        Rect area = monitor.TilingArea;
        int extent = direction.Axis() == SplitDirection.Horizontal ? area.Width : area.Height;

        return extent > 0
            && Resize(handle, direction, (int)Math.Round(pixels * 100.0 / extent));
    }

    public bool Resize(WindowHandle handle, Direction direction, int points)
    {
        if (Window(handle) is not { Managed: true } window || window.Workspace is not { } name
            || Workspace(name) is not { } workspace)
        {
            return false;
        }

        if (window.State != WindowState.Tiling)
        {
            // A floating window is resized directly; the tree has no say.
            return ResizeFloating(window, direction, points * 10);
        }

        return workspace.Tiling.Resize(handle, direction, points / 100.0);
    }

    private static bool ResizeFloating(DeskWindow window, Direction direction, int pixels)
    {
        if (window.FloatingRect is not { } rect)
        {
            return false;
        }

        int by = direction.IsBackwards() ? -pixels : pixels;

        window.FloatingRect = direction.Axis() == SplitDirection.Horizontal
            ? rect with { Width = Math.Max(100, rect.Width + by) }
            : rect with { Height = Math.Max(100, rect.Height + by) };

        return true;
    }

    public bool ToggleDirection(WindowHandle handle) =>
        Window(handle) is { Managed: true, Workspace: { } name }
        && Workspace(name)?.Tiling.ToggleDirection(handle) == true;

    /// <summary>
    /// Puts a window back on a workspace, if it was stuck to its monitor.
    /// </summary>
    /// <remarks>
    /// A sticky window belongs to no workspace, and tiling and fullscreen are
    /// both things a workspace owns. Asking for either is asking for the
    /// window to rejoin one -- which is a clearer answer than doing nothing
    /// and saying nothing, which is what a chord pressed on the chat window
    /// used to get.
    /// </remarks>
    private bool Unstick(DeskWindow window)
    {
        if (!window.Sticky)
        {
            return window.Workspace is not null;
        }

        SetSticky(window.Handle, false);
        return window.Workspace is not null;
    }

    /// <summary>Floats a tiled window, or tiles a floating one.</summary>
    public bool SetFloating(WindowHandle handle, bool floating)
    {
        if (Window(handle) is not { Managed: true } window)
        {
            return false;
        }

        // Tiling a sticky window means it rejoins the workspace on screen;
        // floating one is what it already is.
        if (window.Sticky && floating)
        {
            return false;
        }

        if (!Unstick(window) || window.Workspace is not { } name || Workspace(name) is not { } workspace)
        {
            return false;
        }

        if (window.State == WindowState.Fullscreen)
        {
            SetFullscreen(window, false);
        }

        if (floating == (window.State == WindowState.Floating))
        {
            return false;
        }

        if (floating)
        {
            workspace.Tiling.Remove(handle);
            window.State = WindowState.Floating;

            // Somewhere sensible rather than wherever the tiling left it: a
            // window that fills its half of the screen looks broken as a
            // floating window.
            window.FloatingRect ??= Centred(workspace, window);
            if (!workspace.Floating.Contains(handle))
            {
                workspace.Floating.Add(handle);
            }
        }
        else
        {
            workspace.Floating.Remove(handle);
            window.State = WindowState.Tiling;
            workspace.Tiling.Add(handle, workspace.LastFocused, DirectionFor(workspace));
        }

        window.PreviousState = window.State;
        return true;
    }

    private Rect Centred(Workspace workspace, DeskWindow window)
    {
        Rect area = MonitorOf(workspace)?.TilingArea ?? window.Snapshot.FrameBounds;
        int width = Math.Min(area.Width * 2 / 3, Math.Max(600, window.Snapshot.FrameBounds.Width));
        int height = Math.Min(area.Height * 2 / 3, Math.Max(400, window.Snapshot.FrameBounds.Height));

        return new Rect(
            area.X + ((area.Width - width) / 2),
            area.Y + ((area.Height - height) / 2),
            width,
            height);
    }

    public bool SetFullscreen(WindowHandle handle, bool fullscreen)
    {
        if (Window(handle) is not { Managed: true } window)
        {
            return false;
        }

        // Covering the screen is something a workspace owns, so a window stuck
        // to its monitor joins the workspace in front of the person first.
        if (fullscreen && !Unstick(window))
        {
            return false;
        }

        return SetFullscreen(window, fullscreen);
    }

    /// <summary>
    /// Puts a window over its whole monitor, or gives it back to the layout.
    /// </summary>
    /// <remarks>
    /// The state it had before is remembered, because a game that leaves
    /// fullscreen should go back to being a floating window if that is what it
    /// was, not be dropped into the tiling tree because that is the default.
    /// </remarks>
    private bool SetFullscreen(DeskWindow window, bool fullscreen)
    {
        if (window.Workspace is not { } name || Workspace(name) is not { } workspace)
        {
            return false;
        }

        if (fullscreen)
        {
            if (window.State == WindowState.Fullscreen)
            {
                return false;
            }

            // Only one window covers a workspace. Whoever was there goes back
            // to what it was, or two windows both believe they own the screen
            // and one of them is drawn nowhere.
            if (!workspace.Fullscreen.IsNone
                && workspace.Fullscreen != window.Handle
                && Window(workspace.Fullscreen) is { } covering)
            {
                SetFullscreen(covering, false);
            }

            window.PreviousState = window.State;
            workspace.Tiling.Remove(window.Handle);
            workspace.Floating.Remove(window.Handle);
            window.State = WindowState.Fullscreen;
            workspace.Fullscreen = window.Handle;
            return true;
        }

        if (window.State != WindowState.Fullscreen)
        {
            return false;
        }

        if (workspace.Fullscreen == window.Handle)
        {
            workspace.Fullscreen = WindowHandle.None;
        }

        window.State = window.PreviousState == WindowState.Fullscreen
            ? WindowState.Tiling
            : window.PreviousState;

        Place(window, workspace);
        return true;
    }

    /// <summary>Brings a window back from the taskbar, into the state it left from.</summary>
    private void Restore(DeskWindow window)
    {
        window.State = window.PreviousState == WindowState.Minimized
            ? WindowState.Tiling
            : window.PreviousState;

        if (window.Workspace is { } name && Workspace(name) is { } workspace)
        {
            Place(window, workspace);
        }
        else if (window.Sticky)
        {
            MakeSticky(window);
        }
    }

    /// <summary>Pins a window to its monitor, or lets it belong to a workspace again.</summary>
    public bool SetSticky(WindowHandle handle, bool sticky)
    {
        if (Window(handle) is not { Managed: true } window || window.Sticky == sticky)
        {
            return false;
        }

        if (sticky)
        {
            MakeSticky(window);
            return true;
        }

        window.Sticky = false;
        foreach (DeskMonitor monitor in _monitors)
        {
            monitor.Sticky.Remove(handle);
        }

        DeskMonitor? on = MonitorByRole(window.StickyMonitor ?? string.Empty) ?? FocusedMonitor;
        window.StickyMonitor = null;

        if (on?.Displayed is { } workspace)
        {
            Place(window, workspace);
        }

        return true;
    }

    /// <summary>Raises a floating window within its band.</summary>
    public bool Raise(WindowHandle handle)
    {
        if (Window(handle) is not { Managed: true, State: WindowState.Floating } window
            || window.Workspace is not { } name)
        {
            return false;
        }

        Workspace(name)?.Raise(handle);
        return true;
    }

    /// <summary>Remembers where a floating window has been dragged to.</summary>
    public bool SetFloatingRect(WindowHandle handle, Rect rect)
    {
        if (Window(handle) is not { Managed: true } window
            || window.State is not (WindowState.Floating or WindowState.Fullscreen))
        {
            return false;
        }

        window.FloatingRect = rect;
        return true;
    }
}
