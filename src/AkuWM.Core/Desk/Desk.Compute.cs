using AkuWM.Core.Model;

namespace AkuWM.Core.Desk;

/// <summary>
/// Turning the model into the list of things the platform has to do.
/// </summary>
public sealed partial class Desk
{
    /// <summary>
    /// What would have to happen for the screen to match the model.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Changes nothing, and lists nothing that is already true. That second
    /// part is what a desk feels like rather than a detail: a workspace switch
    /// touches the windows that are moving and no others, so nothing else
    /// flickers, and a command that turns out to change nothing costs one
    /// empty list.
    /// </para>
    /// <para>
    /// A window that is minimised is not here at all. It is on the taskbar
    /// because somebody put it there, and a window manager that keeps
    /// repositioning windows nobody can see is a window manager that fights
    /// the person using it.
    /// </para>
    /// </remarks>
    public Redraw Compute()
    {
        if (Paused)
        {
            return Redraw.Nothing;
        }

        var place = new List<Placement>();
        var hide = new List<WindowHandle>();
        var show = new List<WindowHandle>();
        var band = new List<(WindowHandle, bool)>();
        var mark = new List<(WindowHandle, bool)>();
        var accounted = new HashSet<WindowHandle>();

        foreach (DeskMonitor monitor in _monitors)
        {
            Layout.Gaps gaps = GapsFor(monitor);
            Workspace? displayed = monitor.Displayed;

            foreach (Workspace workspace in monitor.Workspaces)
            {
                if (!ReferenceEquals(workspace, displayed))
                {
                    foreach (WindowHandle handle in workspace.Windows)
                    {
                        if (Live(handle) is { } window && accounted.Add(handle))
                        {
                            WantHidden(window, true, hide, show);

                            // A window that was covering the screen and is now
                            // out of sight must let the taskbar back up, or it
                            // stays behind everything for ever and looks
                            // exactly like a broken shell.
                            WantMarked(window, false, mark);
                        }
                    }

                    continue;
                }

                WindowHandle fullscreen = Live(workspace.Fullscreen) is not null
                    ? workspace.Fullscreen
                    : WindowHandle.None;

                foreach ((WindowHandle handle, Rect rect) in workspace.Tiling.Rects(monitor.TilingArea, gaps))
                {
                    if (Live(handle) is not { } window || !accounted.Add(handle))
                    {
                        continue;
                    }

                    WantHidden(window, false, hide, show);
                    WantPlaced(window, rect, place);
                    WantBanded(window, false, band);
                    WantMarked(window, false, mark);
                }

                foreach (WindowHandle handle in workspace.Floating)
                {
                    if (Live(handle) is not { } window || !accounted.Add(handle))
                    {
                        continue;
                    }

                    WantHidden(window, false, hide, show);
                    WantPlaced(window, FloatingRectOf(window, monitor), place);

                    // A fullscreen window on this workspace takes everything
                    // else out of the always-on-top band: a window that is up
                    // there cannot simply be put behind a normal one, it has
                    // to leave the band first.
                    WantBanded(window, fullscreen.IsNone, band);
                    WantMarked(window, false, mark);
                }

                if (!fullscreen.IsNone && Live(fullscreen) is { } covering && accounted.Add(fullscreen))
                {
                    WantHidden(covering, false, hide, show);
                    WantPlaced(covering, monitor.FullArea, place);
                    WantBanded(covering, true, band);

                    // The taskbar drops behind it, and is told again when it
                    // stops being fullscreen.
                    WantMarked(covering, true, mark);
                }
            }

            foreach (WindowHandle handle in monitor.Sticky)
            {
                if (Live(handle) is not { } window || !accounted.Add(handle))
                {
                    continue;
                }

                WantHidden(window, false, hide, show);
                WantPlaced(window, FloatingRectOf(window, monitor), place);
                WantBanded(window, true, band);
            }
        }

        // Anything AkuWM has not accounted for in this pass -- a window whose
        // workspace belongs to a monitor that has been unplugged -- must not
        // be left invisible because of AkuWM. It is somewhere on the remaining
        // screens, and the person can reach it.
        foreach (DeskWindow window in _windows.Values)
        {
            if (window.Managed && window.Hidden && !accounted.Contains(window.Handle))
            {
                WantHidden(window, false, hide, show);
                WantMarked(window, false, mark);
            }
        }

        return new Redraw
        {
            Place = place,
            Hide = hide,
            Show = show,
            Band = band,
            TaskbarMark = mark,

            // The focus goes last, after the windows are where they belong and
            // visible. Focusing a window that is still cloaked hands the
            // keyboard to something nobody can see.
            Focus = _wantFocus,
        };
    }

    /// <summary>A window AkuWM manages, is still there, and is not on the taskbar.</summary>
    private DeskWindow? Live(WindowHandle handle) =>
        Window(handle) is { Managed: true, State: not WindowState.Minimized } window ? window : null;

    /// <summary>
    /// Where a floating window sits, kept on a monitor that still exists.
    /// </summary>
    /// <remarks>
    /// A rectangle remembered from a screen that has since gone away would put
    /// the window somewhere nobody can reach it, so it is pulled back onto the
    /// work area -- moved, not resized, because a floating window's size is
    /// the person's choice and its position is only where they left it.
    /// </remarks>
    private static Rect FloatingRectOf(DeskWindow window, DeskMonitor monitor)
    {
        Rect rect = window.FloatingRect ?? window.Snapshot.FrameBounds;
        Rect area = monitor.TilingArea;

        if (rect.FractionInside(area) > 0.5)
        {
            return rect;
        }

        return rect with
        {
            X = Math.Clamp(rect.X, area.Left, Math.Max(area.Left, area.Right - rect.Width)),
            Y = Math.Clamp(rect.Y, area.Top, Math.Max(area.Top, area.Bottom - rect.Height)),
        };
    }

    /// <summary>How long a window has to reach where it was put before AkuWM stops asking.</summary>
    public const int PlacementPatienceMs = 2000;

    /// <summary>
    /// How far from where it was put a window may land and still count as
    /// there.
    /// </summary>
    /// <remarks>
    /// A terminal rounds its size to whole character cells and lands a few
    /// pixels off, for ever. Measured on this desk: a console window asked for
    /// 1272x2118 settles at a size of its own choosing nearby. Insisting on
    /// the exact number is a move re-sent for as long as AkuWM runs. Anything
    /// further than this was moved by somebody, and is put back.
    /// </remarks>
    public const int PlacementSlack = 32;

    private void WantPlaced(DeskWindow window, Rect frame, List<Placement> into)
    {
        // Where it should be: the patience clock restarts, so a drag an hour
        // from now is a fresh request rather than a refusal that never was.
        if (window.Snapshot.FrameBounds == frame)
        {
            window.PlacementRefused = false;
            window.PlacedAt = Now;
            return;
        }

        // Already asked for exactly this, and it landed near enough: the
        // difference is the window's own doing, not a person moving it. The
        // clock restarts, or a drag an hour later reads as a refusal that
        // never happened and the window is left where it was dragged.
        if (window.Placed == frame && frame.CloseTo(window.Snapshot.FrameBounds, PlacementSlack))
        {
            window.PlacementRefused = false;
            window.PlacedAt = Now;
            return;
        }

        if (window.Snapshot.IsElevated && !CanPositionElevated)
        {
            // UIPI refuses, and the call fails without saying so. Hiding and
            // showing it by cloak still works, which is what matters for a
            // game on a workspace nobody is looking at.
            return;
        }

        // Already asked, for this exact rectangle, long enough ago that the
        // move would have landed. It is not going to: some windows have a
        // minimum size of their own, and asking for ever is an argument the
        // window always wins at the cost of a window manager that never stops
        // working.
        if (window.Placed == frame && Now - window.PlacedAt > PlacementPatienceMs)
        {
            if (!window.PlacementRefused)
            {
                window.PlacementRefused = true;
                Logging.Log.Warn(
                    $"{window.Snapshot.ProcessName} \"{window.Snapshot.Title}\" will not go to {frame} "
                    + $"(it is at {window.Snapshot.FrameBounds}); AkuWM has stopped asking");
            }

            return;
        }

        into.Add(new Placement(window.Handle, frame));
    }

    private bool _saidItCannotHide;

    private void WantHidden(
        DeskWindow window, bool hidden, List<WindowHandle> hide, List<WindowHandle> show)
    {
        if (window.Hidden == hidden)
        {
            return;
        }

        if (hidden && !CanHide)
        {
            if (!_saidItCannotHide)
            {
                _saidItCannotHide = true;
                Logging.Log.Warn(
                    "not hiding anything: bringing a hidden window back does not work on this machine, "
                    + "so every workspace shows all of its windows");
            }

            return;
        }

        (hidden ? hide : show).Add(window.Handle);
    }

    private static void WantBanded(DeskWindow window, bool topmost, List<(WindowHandle, bool)> into)
    {
        if (window.Banded == topmost)
        {
            return;
        }

        into.Add((window.Handle, topmost));
    }

    private static void WantMarked(DeskWindow window, bool fullscreen, List<(WindowHandle, bool)> into)
    {
        if (window.Marked == fullscreen)
        {
            return;
        }

        into.Add((window.Handle, fullscreen));
    }

    /// <summary>
    /// Records what the platform actually managed to do.
    /// </summary>
    /// <remarks>
    /// <paramref name="refused"/> is the windows whose cloak did not take --
    /// read back from DWM, never assumed, because the shell has a spelling of
    /// that call which reports success and does nothing. A window that refused
    /// keeps its old state in the model, so the next redraw tries again
    /// instead of believing a lie.
    /// </remarks>
    public void Applied(Redraw redraw, IReadOnlySet<WindowHandle>? refused = null)
    {

        foreach (Placement placement in redraw.Place)
        {
            if (Window(placement.Window) is { } window)
            {
                window.Placed = placement.Frame;
                window.PlacedAt = Now;
            }
        }

        foreach (WindowHandle handle in redraw.Hide)
        {
            if (Window(handle) is { } window && refused?.Contains(handle) != true)
            {
                window.Hidden = true;
            }
        }

        foreach (WindowHandle handle in redraw.Show)
        {
            if (Window(handle) is { } window && refused?.Contains(handle) != true)
            {
                window.Hidden = false;
            }
        }

        foreach ((WindowHandle handle, bool topmost) in redraw.Band)
        {
            if (Window(handle) is { } window)
            {
                window.Banded = topmost;
            }
        }

        foreach ((WindowHandle handle, bool fullscreen) in redraw.TaskbarMark)
        {
            if (Window(handle) is { } window)
            {
                window.Marked = fullscreen;
            }
        }

        // Last, after the un-hide above: Focus refuses a window the model still
        // believes is hidden, which used to leave Focused pointing at the
        // workspace that was just put away -- and the next chord with no --id
        // acted on a window nobody could see.
        if (!redraw.Focus.IsNone)
        {
            if (_wantFocus == redraw.Focus)
            {
                _wantFocus = WindowHandle.None;
            }

            Focus(redraw.Focus);
        }
    }
}
