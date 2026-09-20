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

        return new Redraw
        {
            Place = place,
            Hide = hide,
            Show = show,
            Band = band,
            TaskbarMark = mark,
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

    private void WantPlaced(DeskWindow window, Rect frame, List<Placement> into)
    {
        if (window.Snapshot.FrameBounds == frame)
        {
            return;
        }

        if (window.Snapshot.IsElevated && !CanPositionElevated)
        {
            // UIPI refuses, and the call fails without saying so. Hiding and
            // showing it by cloak still works, which is what matters for a
            // game on a workspace nobody is looking at.
            return;
        }

        into.Add(new Placement(window.Handle, frame));
    }

    private static void WantHidden(
        DeskWindow window, bool hidden, List<WindowHandle> hide, List<WindowHandle> show)
    {
        if (window.Hidden == hidden)
        {
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
    }
}
