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
    public bool Focus(WindowHandle handle) => Focus(handle, fromTheDesk: false);

    /// <param name="fromTheDesk">
    /// True when AkuWM asked for this focus itself, so the guard below -- which
    /// exists to refuse focus the LAYOUT caused -- does not refuse it.
    /// </param>
    internal bool Focus(WindowHandle handle, bool fromTheDesk)
    {
        // The focus follows the mouse, not windows moving under it. Windows'
        // own active-window tracking hands the focus to whatever ends up under
        // the pointer, so AkuWM's layout change slides window after window
        // past a pointer nobody moved and each one takes the focus in turn.
        // Measured on the desk: four flips in one second after a window was
        // tiled, and what a person SEES is the content of both windows
        // repainting -- applications redraw on activate and deactivate -- for
        // two or three seconds (2026-09-21).
        // Narrow on purpose: only a window AkuWM ITSELF just moved is refused.
        // A click or an Alt+Tab onto a window that did not move is the person
        // choosing, and is taken. PlacedAt is stamped only when the rectangle
        // asked for CHANGES, so a window being re-asked for the place it is
        // already in does not keep the guard alive.
        // And never for a window that has just been born: an application
        // activates the window it creates, and AkuWM has usually just placed
        // it, which made it look exactly like hover focus.
        if (!fromTheDesk
            && handle != Focused
            && !Focused.IsNone
            && Window(handle) is { } arriving
            && Now - arriving.PlacedAt <= FocusHoldMs
            && Now - arriving.AdoptedAt > NewWindowMs
            && WindowsMovedUnderAStillPointer()
            && Window(Focused) is { Managed: true, Hidden: false })
        {
            _wantFocus = Focused;
            return false;
        }

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

        if (window.Workspace is { } name && Workspace(name) is { } workspace)
        {
            workspace.Touch(handle);

            // The screen the desk says it belongs to, not the one Windows has
            // it on this instant. A window adopted a moment ago is still
            // physically where Windows opened it while AkuWM is on its way to
            // moving it, so taking the snapshot's monitor sent the answer back
            // to the screen the person had just left -- and the NEXT window
            // opened there. Found by tests/wm: two windows started on the
            // vertical monitor, the first landed there and the second did not.
            LookingAt(MonitorOf(workspace));
        }
        else
        {
            LookingAt(MonitorByHandle(window.Snapshot.Monitor));
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
        WindowHandle target = monitor?.Displayed is { } displayed
            ? VisibleLastFocused(displayed)
            : WindowHandle.None;

        if (target.IsNone || Window(target) is not { Managed: true, Hidden: false })
        {
            for (int i = 0; i < _monitors.Count; i++)
            {
                if (_monitors[i].Displayed is { } other
                    && VisibleLastFocused(other) is { IsNone: false } candidate
                    && Window(candidate) is { Managed: true, Hidden: false })
                {
                    target = candidate;
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
                || ReferenceEquals(back, workspace)
                || !ReferenceEquals(MonitorOf(back), monitor))
            {
                // The last guard: a reload can move the previous workspace to
                // another screen, and going "back" to it here hid this
                // monitor's workspace and displayed nothing in its place.
                return VisibleLastFocused(workspace);
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

        return VisibleLastFocused(workspace);
    }

    /// <summary>
    /// The window a workspace hands the focus to: the last focused one that
    /// is not on the taskbar, or any that is not.
    /// </summary>
    /// <remarks>
    /// The workspace's own answer counts minimised windows, because it does
    /// not know states. Returning to a workspace used to focus a floating
    /// window sitting minimised on the taskbar -- SetForegroundWindow on an
    /// iconic window -- and every chord after that acted on it.
    /// </remarks>
    public WindowHandle VisibleLastFocused(Workspace workspace)
    {
        List<WindowHandle> order = workspace.FocusOrder;
        for (int i = 0; i < order.Count; i++)
        {
            if (workspace.Contains(order[i]) && Live(order[i]) is not null)
            {
                return order[i];
            }
        }

        foreach (WindowHandle handle in workspace.Windows)
        {
            if (Live(handle) is not null)
            {
                return handle;
            }
        }

        return WindowHandle.None;
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

        PlaceAcross(window, destination);
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
        return MonitorInDirection(monitor, direction)?.Displayed is { } next
            ? VisibleLastFocused(next)
            : WindowHandle.None;
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
            PlaceAcross(window, destination);
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
    public bool SetFloating(WindowHandle handle, bool floating) =>
        SetFloating(handle, floating, Config.Layout?.FloatCentered != false);

    /// <param name="centred">
    /// Where a window goes the FIRST time it floats: the middle of the screen,
    /// or exactly where it already is. One that has floated before goes back
    /// where it was either way.
    /// </param>
    public bool SetFloating(WindowHandle handle, bool floating, bool centred)
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

        // A minimised window is asked about the layer it will come back to,
        // not the taskbar. Comparing against Minimized refused `set-tiling`
        // outright and let `set-floating` through without bringing the window
        // back, which is how three parked windows ended up reported as
        // floating while they sat on the taskbar (live desk, 2026-09-21).
        // Compute's restore pass brings it back; this decides the layer.
        WindowState now = window.State == WindowState.Minimized
            ? window.PreviousState
            : window.State;

        if (floating == (now == WindowState.Floating))
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
            window.FloatingRect ??= centred
                ? Centred(workspace, window)
                : window.Snapshot.FrameBounds;
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

    /// <summary>A TILED window dragged to the top edge of its monitor.</summary>
    /// <remarks>
    /// A floating window is snapped by Windows itself and never reaches this;
    /// a tiled one is put straight back in its tile, so the gesture did
    /// nothing at all until now.
    ///
    /// What separates the three is whether the window KEEPS ITS PLACE. The
    /// default covers the screen from inside the layout, so PreviousState is
    /// Tiling and leaving fullscreen drops it back where it was. The two
    /// float_* answers take it out first, so the tiles close over the gap and
    /// it is a floating window afterwards. float_maximized stops at the work
    /// area -- the taskbar stays visible and, because it does not cover the
    /// whole monitor, AkuWM does not read it as fullscreen.
    /// </remarks>
    /// <summary>The monitor a point is on.</summary>
    public DeskMonitor? MonitorAtPoint(int x, int y)
    {
        for (int at = 0; at < _monitors.Count; at++)
        {
            if (_monitors[at].Snapshot.Bounds.Contains(x, y))
            {
                return _monitors[at];
            }
        }

        return null;
    }

    /// <summary>
    /// Where a tiled window dropped at this point would land.
    /// </summary>
    /// <remarks>
    /// What the drag outline draws, asked for while the drag is running. A
    /// tiled window is not moved live -- it is still in its tile, and the
    /// layout it came from does not close the gap until the drop -- so the
    /// outline is the only thing that can show where it is going.
    /// </remarks>
    public DropTarget? DropPreview(WindowHandle handle, int x, int y)
    {
        if (Window(handle) is not { Managed: true } window
            || window.State != WindowState.Tiling
            || MonitorAtPoint(x, y) is not { Displayed: { } workspace } monitor)
        {
            return null;
        }

        DropTarget target = DropTargets.Resolve(
            workspace.Tiling.Rects(monitor.TilingArea, GapsFor(monitor)),
            monitor.TilingArea,
            x,
            y,
            handle);

        // Nothing, for a point the drop would refuse -- the window's own tile,
        // or the gap between two. An outline drawn over the whole work area
        // there would promise something that is not going to happen, which is
        // the exact complaint this whole gesture was rebuilt to answer.
        if (target.NextTo.IsNone && workspace.Tiling.Windows.Any(w => w != handle))
        {
            return null;
        }

        return target;
    }

    /// <summary>
    /// Drops a tiled window into the layout under a point.
    /// </summary>
    /// <remarks>
    /// The same rule wherever the point is, on its own monitor or another:
    /// Diego asked for one thing to learn rather than two (2026-09-21). The
    /// gap where it came from closes now and not at the start of the drag, so
    /// a drag that is thought better of leaves the desk as it was.
    ///
    /// A FLOATING window is refused. Floating is a decision of the person's
    /// and only the floating toggle undoes it -- dragging never changes what a
    /// window is, which is the rule that makes this safe to do with any window
    /// under the pointer.
    /// </remarks>
    public bool DropTile(WindowHandle handle, int x, int y)
    {
        if (Window(handle) is not { Managed: true, State: WindowState.Tiling } window
            || MonitorAtPoint(x, y) is not { Displayed: { } destination } monitor)
        {
            return false;
        }

        DropTarget target = DropTargets.Resolve(
            destination.Tiling.Rects(monitor.TilingArea, GapsFor(monitor)),
            monitor.TilingArea,
            x,
            y,
            handle);

        if (target.NextTo == handle)
        {
            return false;
        }

        // Nothing under the pointer but the window being dragged, or the gap
        // between two tiles. On a workspace that already has tiles that is not
        // an instruction, and appending the window at the end -- which is what
        // "beside nobody" means to the tree -- would move it for no reason.
        if (target.NextTo.IsNone && destination.Tiling.Windows.Any(w => w != handle))
        {
            return false;
        }

        PlaceAcross(window, destination);

        // Place put it in the layout wherever the focus order suggested. Now
        // exactly where the pointer asked for.
        destination.Tiling.Remove(handle);
        destination.Tiling.Add(handle, target.NextTo, target.Direction, target.Before);
        destination.Touch(handle);
        return true;
    }

    public bool DragToTop(WindowHandle handle)
    {
        if (Window(handle) is not { Managed: true } window)
        {
            return false;
        }

        switch ((Config.Layout?.DragToTop ?? "fullscreen").ToLowerInvariant())
        {
            case "none":
                return false;

            case "float_maximized" or "float-maximized":
                if (window.State != WindowState.Floating && !SetFloating(handle, true, centred: false))
                {
                    return false;
                }

                return window.Workspace is { } name
                       && Workspace(name) is { } on
                       && MonitorOf(on) is { } screen
                       && SetFloatingRect(handle, screen.TilingArea);

            case "float_fullscreen" or "float-fullscreen":
                if (window.State != WindowState.Floating && !SetFloating(handle, true, centred: false))
                {
                    return false;
                }

                return SetFullscreen(handle, true);

            default:
                return SetFullscreen(handle, true);
        }
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
        window.State = window.WasFullscreen
            ? WindowState.Fullscreen
            : window.PreviousState == WindowState.Minimized
                ? WindowState.Tiling
                : window.PreviousState;
        window.WasFullscreen = false;

        if (window.Workspace is { } name && Workspace(name) is { } workspace)
        {
            // Somebody brought it back -- a taskbar click, the application
            // itself -- and it belongs to a workspace that is not on screen.
            // Placing it there had the next pass cloak it in the same breath:
            // the click restored a window that vanished. The workspace comes
            // into view with it, which is what the click meant.
            if (!workspace.Displayed && MonitorOf(workspace) is not null)
            {
                FocusWorkspace(workspace.Name);
                WantFocus(window.Handle);
            }

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
    /// <summary>
    /// Records that the person, not a rule, decided how this window behaves.
    /// </summary>
    /// <remarks>
    /// Called from the command path only. Everything inside the model that
    /// changes a state is carrying out a decision already made -- by a rule,
    /// by a redraw, or by this.
    /// </remarks>
    public void PersonDecided(WindowHandle handle)
    {
        if (Window(handle) is { } window)
        {
            window.DecidedByHand = true;
        }
    }

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
