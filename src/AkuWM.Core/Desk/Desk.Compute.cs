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
        var restore = new List<WindowHandle>();
        var band = new List<(WindowHandle, bool)>();
        var behind = new List<(WindowHandle, WindowHandle)>();
        var mark = new List<(WindowHandle, bool)>();
        var decorate = new List<(WindowHandle, Decoration)>();
        var button = new List<(WindowHandle, bool)>();
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

                // Only a GAME takes its neighbours out of the always-on-top
                // band: what the shield protects is a flip-model swapchain,
                // and a maximised browser has none. A maximised window that
                // covers a monitor with no taskbar counted the same, and every
                // floating window on that screen went behind it on its next
                // click, lost (Diego, 2026-09-22).
                WindowHandle shielding = Live(workspace.Fullscreen) is { } fs && LooksLikeAGame(fs)
                    ? workspace.Fullscreen
                    : WindowHandle.None;

                WindowHandle fullscreen = Live(workspace.Fullscreen) is not null
                    ? workspace.Fullscreen
                    : WindowHandle.None;

                // Claimed before the loops below, not after them. A window
                // that covers the screen is usually ALSO in the workspace's
                // floating list (it floated before it went fullscreen), and
                // whichever loop reached it first took it: it was placed at
                // its floating rectangle, taken OUT of the always-on-top band
                // by the rule meant for the windows around it, and the block
                // that bands it was skipped for being already accounted for.
                // Measured on the desk 2026-09-21: game-topmost=False with a
                // chat window above it and 11% of frames direct.
                if (!fullscreen.IsNone)
                {
                    accounted.Add(fullscreen);
                }

                foreach ((WindowHandle handle, Rect rect) in workspace.Tiling.Rects(monitor.TilingArea, gaps))
                {
                    if (Live(handle) is not { } window || !accounted.Add(handle))
                    {
                        continue;
                    }

                    WantHidden(window, false, hide, show);
                    WantPlaced(window, rect, monitor, place);
                    WantBanded(window, false, band);
                    WantBehind(window, shielding, behind);
                    WantMarked(window, false, mark);
                }

                foreach (WindowHandle handle in workspace.Floating)
                {
                    if (Live(handle) is not { } window || !accounted.Add(handle))
                    {
                        continue;
                    }

                    WantHidden(window, false, hide, show);
                    WantPlaced(window, FloatingRectOf(window, monitor), monitor, place);

                    // A fullscreen window on this workspace takes everything
                    // else out of the always-on-top band: a window that is up
                    // there cannot simply be put behind a normal one, it has
                    // to leave the band first -- and then be put behind it,
                    // because leaving the band lands it on top.
                    WantBanded(window, shielding.IsNone, band);
                    WantBehind(window, shielding, behind);
                    WantMarked(window, false, mark);
                }

                if (!fullscreen.IsNone && Live(fullscreen) is { } covering)
                {
                    WantHidden(covering, false, hide, show);

                    // A MAXIMISED window is where Windows keeps it -- the work
                    // area, under the bar -- and ignores a move; asking it to
                    // cover the monitor's bounds was refused on every desk
                    // with a bar (Zen on the vertical monitor, 35 px short,
                    // "will not go to", 2026-09-22).
                    if (!covering.Snapshot.IsMaximized)
                    {
                        WantPlaced(covering, monitor.FullArea, monitor, place);
                    }

                    // The band is NOT touched, either way. A window that
                    // covers the screen owns it by being the foreground window
                    // and by the taskbar mark below; a SetWindowPos from
                    // another process against a game that has just taken a
                    // flip-model swapchain is what turned Age of Empires II
                    // black -- menus invisible, music playing -- with the
                    // band as the only thing AkuWM had done to it (traces,
                    // live desk 2026-09-21: "0 to place, 1 to reband, 1 to
                    // mark"). Taking it OUT of a band it put itself in is the
                    // same call, and every window used to get it on adoption
                    // (Banded was null, not what Windows said). The windows
                    // AROUND it still leave the band and go behind it, which
                    // is what keeps a chat window off a game.

                    // The taskbar drops behind it, and is told again when it
                    // stops being fullscreen. Not for a maximised application
                    // that stops at the bar: telling the shell that one is
                    // fullscreen hides the bar under a browser.
                    // A window AkuWM itself places over the bounds is about to
                    // cover them; only a maximised one has to prove it.
                    bool coversTheBounds = !covering.Snapshot.IsMaximized
                        || covering.Snapshot.FrameBounds.Contains(monitor.FullArea);
                    WantMarked(covering, coversTheBounds || LooksLikeAGame(covering), mark);
                }
            }

            // A game on the screen this window follows takes it out of the
            // always-on-top band, exactly as it does to a floating window of
            // its own workspace. Without this a sticky Telegram or terminal
            // sat above a fullscreen game -- and a visible always-on-top
            // window over one costs it the direct path to the screen: DWM
            // composes the frame instead, measured at 45 fps and +60 ms on
            // Aion 2. That is the whole reason this window manager exists, and
            // it was true of every sticky window on the desk.
            bool covered = displayed is not null && Live(displayed.Fullscreen) is { } game && LooksLikeAGame(game);

            foreach (WindowHandle handle in monitor.Sticky)
            {
                if (Live(handle) is not { } window || !accounted.Add(handle))
                {
                    continue;
                }

                WantHidden(window, false, hide, show);
                WantPlaced(window, FloatingRectOf(window, monitor), monitor, place);
                WantBanded(window, !covered, band);
                WantBehind(window, covered ? displayed!.Fullscreen : WindowHandle.None, behind);

                // A sticky window that was covering the screen when it was
                // stuck keeps the mark otherwise, and the taskbar stays down.
                WantMarked(window, false, mark);
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

        // A window the model has put back into a layout while Windows still
        // has it minimised. Asked once per window: a restore that the shell
        // refuses must not be re-sent on every redraw for the rest of the run.
        foreach (DeskWindow window in _windows.Values)
        {
            if (!window.Managed || window.State == WindowState.Minimized)
            {
                _asked.Remove(window.Handle);
                continue;
            }

            if (window.Snapshot.IsMinimized && _asked.Add(window.Handle))
            {
                restore.Add(window.Handle);
            }
        }

        // One pass over everything, after the workspaces have decided what is
        // managed and what is visible: decoration follows the focus, and the
        // focus is not a property of any one workspace.
        foreach (DeskWindow window in _windows.Values)
        {
            // A window that covers the screen is never decorated. A DWM
            // border colour or a corner preference makes the shell COMPOSE
            // and clip the window, and a game that was presenting straight to
            // the screen stops: Age of Empires II went black with its music
            // still playing, and froze again when it was maximised, with the
            // purple border drawn around it both times (live desk 2026-09-21).
            // There is nothing to see either way -- a fullscreen window has no
            // visible edge to put a border on.
            Decoration want = window.Managed && window.State != WindowState.Fullscreen
                ? DecorationFor(window)
                : Decoration.Untouched;

            // Null means AkuWM has never touched it, and Untouched means put it
            // back: a window that was never decorated needs neither. A
            // decoration the shell refused is asked for once.
            if (window.Decorated == want
                || (window.Decorated is null && want == Decoration.Untouched)
                || window.DecorationRefused == want)
            {
                continue;
            }

            decorate.Add((window.Handle, want));
        }

        // The taskbar button follows the cloak, when the configuration says
        // the bar should only show what is on screen. Never the other way
        // round: a window with no button AND no pixels is unreachable, so the
        // button comes back with the window, always.
        bool showAll = Config.General?.ShowAllInTaskbar ?? false;

        foreach (DeskWindow window in _windows.Values)
        {
            if (!window.Managed)
            {
                if (window.InTaskbar == false)
                {
                    button.Add((window.Handle, true));
                    window.InTaskbar = null;
                }

                continue;
            }

            // What this pass is about to do, not what was true before it:
            // Hidden is set when the redraw is applied, so reading it here
            // takes the button off a window one pass late and puts it back one
            // pass late too. Scanned rather than hashed -- both lists are a
            // workspace's worth of windows and this allocates nothing.
            bool hiding = hide.Contains(window.Handle);
            bool showing = show.Contains(window.Handle);
            bool shown = showAll || (showing || (!hiding && !window.Hidden));

            // Null is "AkuWM has never had an opinion", and a window it has
            // never hidden must not have its button taken away by a pass that
            // merely noticed it.
            if (window.InTaskbar == shown || (window.InTaskbar is null && shown))
            {
                continue;
            }

            button.Add((window.Handle, shown));
        }

        return new Redraw
        {
            Place = place,
            Hide = hide,
            Show = show,
            Restore = restore,
            Band = band,
            Behind = behind,
            TaskbarMark = mark,
            Decorate = decorate,
            TaskbarButton = button,

            // The focus goes last, after the windows are where they belong and
            // visible. Focusing a window that is still cloaked hands the
            // keyboard to something nobody can see.
            Focus = _wantFocus,

            // And when nothing is to be focused while the focused window is
            // going away, the keyboard must not stay on it.
            Unfocus = _wantFocus.IsNone && !Focused.IsNone && hide.Contains(Focused),
        };
    }

    /// <summary>
    /// Whether a window covering its screen is the kind that must not have
    /// an always-on-top window over it: a game.
    /// </summary>
    /// <remarks>
    /// By the frame, since nothing else is knowable from outside: a game
    /// covers the screen with a borderless popup (no resize border) or runs
    /// elevated; an application that covers it is maximised and keeps its
    /// resize border. `layout.floating_above_maximized: false` makes every
    /// covering window a game, which is what the desk did before.
    /// </remarks>
    public bool LooksLikeAGame(DeskWindow window) =>
        window.State == WindowState.Fullscreen
        && (Config.Layout?.FloatingAboveMaximized == false
            || !window.Snapshot.IsResizable
            || window.Snapshot.IsElevated);

    /// <summary>
    /// Asks for a window to be put behind the fullscreen one, once per pair.
    /// </summary>
    private static void WantBehind(DeskWindow window, WindowHandle game, List<(WindowHandle, WindowHandle)> into)
    {
        if (game.IsNone || window.Behind == game || window.Handle == game)
        {
            return;
        }

        into.Add((window.Handle, game));
    }

    /// <summary>
    /// Asks for the decoration of a window to be sent again next pass.
    /// </summary>
    /// <remarks>
    /// Windows Terminal and VS Code (any Chromium) set their own
    /// DWMWA_BORDER_COLOR on every activation, a beat after AkuWM's: the
    /// purple border showed for an instant and went. Sent once more,
    /// <c>effects.reassert_ms</c> after the focus lands.
    /// </remarks>
    public void Redecorate(WindowHandle handle)
    {
        if (Window(handle) is { Managed: true } window)
        {
            window.Decorated = null;
            window.DecorationRefused = null;
        }
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

        // Bigger than the screen it is on. A floating window's size is the
        // person's choice everywhere else in here, but a window that does not
        // fit cannot be put inside the work area at all -- the move below has
        // nowhere to move it to -- so it hangs off the edge for ever and, worse,
        // Windows goes on reporting it as being on the screen it covers most
        // of, which is the one it came from. That is why Diego's terminal could
        // not be dropped on the BenQ: 2020x2591 onto a work area of 1920x1052
        // (2026-09-21). Trimmed to fit, and only the placement -- the
        // remembered rectangle keeps his size for when it goes back to a screen
        // that has room.
        if (rect.Width > area.Width || rect.Height > area.Height)
        {
            rect = rect with
            {
                Width = Math.Min(rect.Width, area.Width),
                Height = Math.Min(rect.Height, area.Height),
            };
        }
        else if (rect.FractionInside(area) > 0.5)
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

    /// <summary>How still a window must be before it is asked again.</summary>
    public const int SettleMs = 300;

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

    private void WantPlaced(DeskWindow window, Rect frame, DeskMonitor monitor, List<Placement> into)
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

        // Asked for this already, and the window is STILL MOVING. It is not
        // refusing, it is on its way: an application sizing itself -- a game
        // leaving fullscreen -- passes through a second's worth of rectangles
        // that are neither where it was nor where it is going. Asking again at
        // every step makes it lay out its content again each time, which is
        // what is seen as the inside of the window redrawing for a second or
        // two after it is tiled. Measured on the desk 2026-09-21: six asks in
        // one and a half seconds. The patience clock above is not reset by
        // this, so a window that moves for ever is still given up on.
        // MovedAt of zero is "never seen to move", not "moved at time zero":
        // a window that has never budged is refusing, not settling, and must
        // not be given the benefit of the doubt for ever.
        if (window.Placed == frame
            && !window.Landed
            && window.MovedAt != 0
            && Now - window.MovedAt < SettleMs)
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

        // The border travels with it: the model read both rectangles when the
        // window last changed, and the platform would otherwise ask Windows
        // for them again -- a GetWindowRect and a DWM round trip per window,
        // inside the batch, on every redraw. Not across a change of scale,
        // and not from a maximised window: the invisible border is 9 px at
        // 150 % and ~6 at 100 %, and a maximised window's outer rectangle
        // overhangs on every side, so a delta read there is wrong here and
        // the 32 px slack then accepted the miss for good.
        (int Top, int Right, int Bottom, int Left)? border =
            window.Snapshot.IsMaximized
            || (MonitorByHandle(window.Snapshot.Monitor) is { } was
                && was.Snapshot.ScaleFactor != monitor.Snapshot.ScaleFactor)
                ? null
                : window.Snapshot.BorderDelta;

        into.Add(new Placement(window.Handle, frame, border));
    }

    private bool _saidItCannotHide;

    /// <summary>Windows already asked to come back from the taskbar.</summary>
    private readonly HashSet<WindowHandle> _asked = [];

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

    /// <summary>What the shell should draw around one window, right now.</summary>
    private Decoration DecorationFor(DeskWindow window)
    {
        Config.EffectsConfig? global = Config.Effects;
        Config.EffectsConfig? mine = window.Effects;

        // The window's own rules first, then the global block for anything
        // they did not mention.
        string? border = Focused == window.Handle
            ? mine?.FocusedBorder ?? global?.FocusedBorder
            : mine?.OtherBorder ?? global?.OtherBorder;

        double opacity = mine?.Opacity ?? global?.Opacity ?? 1;

        return new Decoration(
            Decoration.ColorRef(border),
            ParseCorners(mine?.Corners ?? global?.Corners),
            TitleBar: (mine?.TitleBar ?? global?.TitleBar) is not "hide",
            Opacity: Math.Clamp(opacity, Decoration.MinimumOpacity, 1));
    }

    private static Corners ParseCorners(string? corners) => corners?.ToLowerInvariant() switch
    {
        "square" or "none" => Model.Corners.Square,
        "round" => Model.Corners.Round,
        "round_small" or "round-small" => Model.Corners.RoundSmall,
        _ => Model.Corners.Default,
    };

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
    public void Applied(
        Redraw redraw,
        IReadOnlySet<WindowHandle>? refused = null,
        IReadOnlySet<WindowHandle>? unmarked = null,
        IReadOnlySet<WindowHandle>? undecorated = null,
        bool focusRefused = false)
    {

        if (redraw.Place.Count > 0)
        {
            MovedWindowsAt(Now);
        }

        foreach (Placement placement in redraw.Place)
        {
            if (Window(placement.Window) is not { } window)
            {
                continue;
            }

            // The clock starts when the rectangle being ASKED FOR changes, not
            // on every re-send. Stamping it here unconditionally reset the
            // patience on AkuWM's own retry, so `Now - PlacedAt` never reached
            // PlacementPatienceMs and the argument never ended: a window with a
            // size of its own snapped back, that raised an event, the next
            // redraw asked again, and the window flickered between the two
            // rectangles for as long as it took the window to give in.
            // Reported from the desk as a loop on Hyper+Shift+F (2026-09-21).
            if (window.Placed != placement.Frame)
            {
                window.PlacedAt = Now;

                // A new rectangle: it has not been there yet, whatever it did
                // about the last one.
                window.Landed = false;
            }

            window.Placed = placement.Frame;
        }

        foreach (WindowHandle handle in redraw.Hide)
        {
            if (Window(handle) is { } window && refused?.Contains(handle) != true)
            {
                window.Hidden = true;
                RecordHidden(handle, true);
            }
        }

        foreach (WindowHandle handle in redraw.Show)
        {
            if (Window(handle) is { } window && refused?.Contains(handle) != true)
            {
                window.Hidden = false;
                RecordHidden(handle, false);
            }
        }

        foreach ((WindowHandle handle, bool topmost) in redraw.Band)
        {
            if (Window(handle) is { } window)
            {
                window.Banded = topmost;

                // Back in the band is above everything; the insert-behind is
                // owed again the next time it leaves.
                if (topmost)
                {
                    window.Behind = WindowHandle.None;
                }
            }
        }

        foreach ((WindowHandle handle, WindowHandle game) in redraw.Behind)
        {
            if (Window(handle) is { } window)
            {
                window.Behind = game;
            }
        }

        foreach ((WindowHandle handle, Decoration how) in redraw.Decorate)
        {
            if (Window(handle) is not { } decorated)
            {
                continue;
            }

            if (undecorated?.Contains(handle) == true)
            {
                decorated.DecorationRefused = how;
                continue;
            }

            decorated.DecorationRefused = null;
            decorated.Decorated = how == Decoration.Untouched ? null : how;
        }

        // The focused window has just been hidden: what the model calls
        // focused must not be a window nobody can see, or the next chord with
        // no --id acts on it and the bar says the wrong workspace has focus.
        if (Window(Focused) is { Hidden: true })
        {
            Focused = WindowHandle.None;
        }

        foreach ((WindowHandle handle, bool shown) in redraw.TaskbarButton)
        {
            if (Window(handle) is { } buttoned)
            {
                buttoned.InTaskbar = shown;
            }
        }

        foreach ((WindowHandle handle, bool fullscreen) in redraw.TaskbarMark)
        {
            // A mark the shell would not take is one the next pass has to make
            // again, so the model must not remember it as done.
            if (Window(handle) is { } window && unmarked?.Contains(handle) != true)
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

            // Only when the platform managed it. Recording the target
            // regardless had the model certain the chat window was in front
            // while an elevated game still held the foreground, and no
            // foreground event was ever going to correct it.
            if (!focusRefused && Window(redraw.Focus) is not null)
            {
                Focus(redraw.Focus, fromTheDesk: true);
            }
        }
    }
}
