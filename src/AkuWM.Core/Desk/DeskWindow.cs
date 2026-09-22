using AkuWM.Core.Model;

namespace AkuWM.Core.Desk;

/// <summary>
/// A window as the running window manager holds it: what it is, where it
/// belongs, and what AkuWM has done to it.
/// </summary>
/// <remarks>
/// The snapshot is what the OS last said; everything beside it is AkuWM's own
/// and survives the snapshot being replaced. That separation is what lets a
/// window be moved, hidden, and moved back without the model ever having to
/// ask the OS what it used to be.
/// </remarks>
public sealed class DeskWindow
{
    public DeskWindow(WindowSnapshot snapshot) => Snapshot = snapshot;

    /// <summary>The container id the bar and the scripts address it by.</summary>
    public Guid Id { get; } = Guid.NewGuid();

    public WindowHandle Handle => Snapshot.Handle;

    public WindowSnapshot Snapshot { get; set; }

    public bool Managed { get; set; }

    public UnmanagedReason Reason { get; set; }

    /// <summary>The rule that decided it, when one did.</summary>
    public string? ReasonDetail { get; set; }

    public WindowState State { get; set; } = WindowState.Tiling;

    /// <summary>What it was before fullscreen or minimising, to go back to.</summary>
    public WindowState PreviousState { get; set; } = WindowState.Tiling;

    /// <summary>The workspace it belongs to, by name. Null for sticky and unmanaged windows.</summary>
    public string? Workspace { get; set; }

    /// <summary>
    /// Drawn on whichever workspace its monitor is showing.
    /// </summary>
    /// <remarks>
    /// A deliberate difference from the manager AkuWM replaces: a sticky
    /// window belongs to the <em>monitor</em>, so switching workspaces never
    /// moves it and it cannot end up on the other screen by accident. It also
    /// means a sticky window is never in a tiling tree -- it would have to be
    /// in all of them -- so sticky implies floating.
    /// </remarks>
    public bool Sticky { get; set; }

    /// <summary>The monitor role it sticks to, when it is sticky.</summary>
    public string? StickyMonitor { get; set; }

    /// <summary>Where a floating window sits. Remembered across a workspace switch.</summary>
    public Rect? FloatingRect { get; set; }

    /// <summary>AkuWM has the shell's cloak on this window.</summary>
    public bool Hidden { get; set; }

    /// <summary>When AkuWM first saw it, in milliseconds of the desk's clock.</summary>
    /// <remarks>
    /// A window that takes the foreground within a moment of being adopted is
    /// an application activating the window it just created, not hover focus
    /// from a layout change -- and the hold-still guard refused it (tests/wm:
    /// a game placed and then "refused the focus for the hidden window", the
    /// focus went to the other monitor, and the next window opened there).
    /// </remarks>
    public long AdoptedAt { get; set; }

    /// <summary>Where AkuWM last put it, so an unchanged rectangle is not sent again.</summary>
    public Rect? Placed { get; set; }

    /// <summary>
    /// The size a window that leaves its scaling to Windows had before
    /// Windows blew it up at a seam; kept until AkuWM has placed it again.
    /// </summary>
    /// <remarks>
    /// One jump was caught and the next tick, thirteen pixels wider as the
    /// drag went on, was believed -- the blown-up size came back anyway.
    /// </remarks>
    public (int Width, int Height)? SteadySize { get; set; }

    /// <summary>
    /// When AkuWM last asked Windows to un-maximise it, so the maximised
    /// rectangle seen meanwhile is not read as the window covering its screen.
    /// </summary>
    public long? UnmaximizeAskedAt { get; set; }

    /// <summary>When that was, in milliseconds, so a move that never lands is noticed.</summary>
    public long PlacedAt { get; set; }

    /// <summary>
    /// When the window's own rectangle last changed, whoever changed it.
    /// </summary>
    /// <remarks>
    /// A window that is still moving is not refusing: an application sizing
    /// ITSELF -- a game coming out of fullscreen recreates its swapchain and
    /// takes a second or more about it -- passes through rectangles that are
    /// neither the old one nor the one it was asked for. Asking again at every
    /// step makes it lay its content out again each time.
    /// </remarks>
    public long MovedAt { get; set; }

    /// <summary>
    /// Whether the window has been where AkuWM last asked it to be.
    /// </summary>
    /// <remarks>
    /// This is what separates "still on its way" from "somebody moved it". A
    /// window that has NOT landed yet and is moving is settling, and asking
    /// again at every step is what makes an application redraw its content
    /// over and over. A window that HAD landed and then moved was dragged, and
    /// goes back at once.
    /// </remarks>
    public bool Landed { get; set; }

    /// <summary>When this window last crossed onto another screen.</summary>
    /// <remarks>
    /// Windows rescales a window that crosses between screens of different
    /// scaling, 125 to 156 ms after the move (measured). That arrives as a
    /// resize nobody asked for, and without this it was read as the person
    /// resizing the window -- which overwrote the size the crossing had just
    /// decided, so <c>layout.across_monitors</c> had no effect at all beyond
    /// the first moment.
    /// </remarks>
    public long CrossedAt { get; set; }

    /// <summary>
    /// It was asked to go somewhere, it did not, and AkuWM has stopped asking.
    /// </summary>
    /// <remarks>
    /// Some windows will not take the size they are given -- a minimum size of
    /// their own, a frame they draw themselves. Asking again every time the
    /// answer comes back wrong is an argument the window always wins, at the
    /// cost of a window manager that never stops working. It is logged once
    /// and shown by <c>doctor</c> instead.
    /// </remarks>
    public bool PlacementRefused { get; set; }

    /// <summary>Whether AkuWM last put it in the always-on-top band.</summary>
    public bool? Banded { get; set; }

    /// <summary>
    /// The band was asked for and the window did not keep it: the
    /// application manages its own always-on-top (Windows Terminal). Not
    /// asked again; kept over the tiles by <see cref="Redraw.Raise"/> instead.
    /// </summary>
    public bool BandRefused { get; set; }

    /// <summary>
    /// A floating window the person sent behind the tiles with <c>lower</c>.
    /// Cleared by <c>raise</c>, or by the person focusing it.
    /// </summary>
    public bool Lowered { get; set; }

    /// <summary>
    /// Minimised by Windows, not by the person: within <see cref="Desk.ParkWindowMs"/>
    /// of a screen change. Windows parks every window of a monitor it loses
    /// (and of one it reconfigures); once the screens have settled and the
    /// window's monitor is here, it is brought back where it was.
    /// </summary>
    public bool Parked { get; set; }

    /// <summary>The HWND_BOTTOM for <see cref="Lowered"/> has been sent.</summary>
    public bool LoweredApplied { get; set; }

    /// <summary>
    /// The fullscreen window AkuWM last put this one behind, or None.
    /// </summary>
    /// <remarks>
    /// Leaving the always-on-top band is HWND_NOTOPMOST, and that puts a
    /// window at the TOP of the ordinary band -- above the game it was meant
    /// to get out of the way of. The insert-behind is the second half of
    /// de-banding, sent once per (window, game) pair.
    /// </remarks>
    public WindowHandle Behind { get; set; } = WindowHandle.None;

    /// <summary>
    /// It was covering its monitor when it went to the taskbar, and comes back
    /// covering it, whatever it was before that.
    /// </summary>
    /// <remarks>
    /// Kept apart from <see cref="PreviousState"/>: minimising a fullscreen
    /// window used to overwrite the state it had BEFORE fullscreen, so a
    /// floating game that minimised on focus loss came back, left fullscreen,
    /// and was dropped into the tiling tree.
    /// </remarks>
    public bool WasFullscreen { get; set; }

    /// <summary>
    /// A decoration the shell would not take, so it is not asked for again
    /// until a different one is wanted.
    /// </summary>
    /// <remarks>
    /// UIPI refuses DWM attributes on a higher-integrity window from a
    /// process without uiAccess; recording it as done left the model certain
    /// the border was there, and re-sending it on every redraw is two DWM
    /// calls per pass for nothing.
    /// </remarks>
    public Decoration? DecorationRefused { get; set; }

    /// <summary>The next decoration is sent whatever was sent before: an application painted over it.</summary>
    public bool RedecorateAsked { get; set; }

    /// <summary>Whether the taskbar has been told this window is fullscreen.</summary>
    public bool Marked { get; set; }

    /// <summary>
    /// What AkuWM last asked the shell to draw around it.
    /// </summary>
    /// <remarks>
    /// Null while AkuWM has never decorated it, which is what tells the unmanage
    /// path there is nothing to put back.
    /// </remarks>
    public Decoration? Decorated { get; set; }

    /// <summary>
    /// Whether the taskbar is currently showing a button for it.
    /// </summary>
    /// <remarks>
    /// Null until AkuWM has had an opinion, which is what stops it taking the
    /// button off a window it has never hidden.
    /// </remarks>
    public bool? InTaskbar { get; set; }

    /// <summary>Rules that fired for it, by name, for <c>query</c> and the GUI.</summary>
    public IReadOnlyList<string> Rules { get; set; } = [];

    /// <summary>
    /// Whether the person has changed this window's state themselves.
    /// </summary>
    /// <remarks>
    /// A chord that floats a window is a decision, and it outranks the
    /// configuration that would have tiled it. Without this, editing an
    /// unrelated rule and reloading -- or anything else that re-decides a
    /// window -- would quietly put it back, and the person would have to make
    /// the same decision twice. Reset when the window is closed, never by a
    /// reload.
    /// </remarks>
    public bool DecidedByHand { get; set; }

    /// <summary>
    /// How this window should look, from the rules that caught it.
    /// </summary>
    /// <remarks>
    /// Resolved once, when the window is adopted, rather than walked on every
    /// redraw: the rules that caught it do not change while it is open, and
    /// the decoration pass runs over every window every time.
    /// </remarks>
    public Config.EffectsConfig? Effects { get; set; }

    public override string ToString() =>
        $"{Handle} {Snapshot.ProcessName} \"{Snapshot.Title}\" {State}"
        + (Workspace is null ? string.Empty : $" on {Workspace}")
        + (Sticky ? " sticky" : string.Empty)
        + (Hidden ? " hidden" : string.Empty);
}
