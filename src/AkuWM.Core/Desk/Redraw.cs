using AkuWM.Core.Model;

namespace AkuWM.Core.Desk;

/// <summary>
/// Everything that has to happen to the desk to make it match the model, and
/// nothing that already matches.
/// </summary>
/// <remarks>
/// <para>
/// The model decides; this is the sentence it hands to the platform. Keeping
/// them apart is what lets every decision in the window manager be tested on
/// Linux: the test asserts the sentence, not the screen.
/// </para>
/// <para>
/// It is also the reason the desk does not flicker. A workspace switch is one
/// batch -- a dozen moves, a dozen cloaks -- rather than a dozen separate
/// trips through the window manager, and anything already in the right place
/// is not in the list at all.
/// </para>
/// </remarks>
public sealed record Redraw
{
    public static readonly Redraw Nothing = new();

    /// <summary>Windows to move, by visible frame.</summary>
    public IReadOnlyList<Placement> Place { get; init; } = [];

    /// <summary>Windows to hide: the shell's cloak goes on.</summary>
    public IReadOnlyList<WindowHandle> Hide { get; init; } = [];

    /// <summary>Windows to show: the cloak comes off.</summary>
    public IReadOnlyList<WindowHandle> Show { get; init; } = [];

    /// <summary>
    /// Windows to bring back from the taskbar: the model says they are tiled
    /// or floating, Windows still has them minimised.
    /// </summary>
    /// <remarks>
    /// A command that changes a window's state -- set-floating, set-tiling,
    /// move-workspace -- changes the MODEL. Nothing else un-minimises, so
    /// before this pass existed `set-floating` on a minimised window left the
    /// model saying floating and the window still on the taskbar, invisible
    /// and unreachable until something unrelated restored it. Measured on the
    /// live desk 2026-09-21, on the three sticky windows a test run had parked.
    /// </remarks>
    public IReadOnlyList<WindowHandle> Restore { get; init; } = [];

    /// <summary>
    /// Windows to un-maximise before they are placed: a maximised window
    /// ignores a move, so a tile or a floating rectangle asked of one was
    /// refused every time ("will not go to", Brave on the vertical monitor,
    /// 2026-09-22). AkuWM never maximises; this is the only way it takes a
    /// maximised state off.
    /// </summary>
    public IReadOnlyList<WindowHandle> Unmaximize { get; init; } = [];

    /// <summary>Windows entering or leaving the always-on-top band.</summary>
    public IReadOnlyList<(WindowHandle Window, bool Topmost)> Band { get; init; } = [];

    /// <summary>
    /// Floating windows to bring back over the tiles, without activating them.
    /// </summary>
    /// <remarks>
    /// The band is the first answer to "floating stays above tiling" and it
    /// is not enough: an application that manages its own always-on-top
    /// (Windows Terminal) strips the bit on every activation, and a click on
    /// a tile then covers the terminal. So every time the focus lands on a
    /// tile, the floating windows of that screen that are not in the band are
    /// raised over it -- one SetWindowPos each, after the click, which is a
    /// frame late and the person does not see it.
    /// </remarks>
    public IReadOnlyList<WindowHandle> Raise { get; init; } = [];

    /// <summary>Floating windows the person sent behind the tiles (the <c>lower</c> command).</summary>
    public IReadOnlyList<WindowHandle> Lower { get; init; } = [];

    /// <summary>
    /// Windows to put directly behind a fullscreen one, in the ordinary band.
    /// </summary>
    /// <remarks>
    /// Leaving the always-on-top band (HWND_NOTOPMOST) lands a window at the
    /// TOP of the ordinary band, above the game it was taken out for. The
    /// call is made on the sibling, never on the game, so the game's
    /// swapchain is never touched.
    /// </remarks>
    public IReadOnlyList<(WindowHandle Window, WindowHandle Behind)> Behind { get; init; } = [];

    /// <summary>Windows the taskbar must be told about, so it drops behind a game.</summary>
    public IReadOnlyList<(WindowHandle Window, bool Fullscreen)> TaskbarMark { get; init; } = [];

    /// <summary>Windows whose border or corners should change.</summary>
    public IReadOnlyList<(WindowHandle Window, Decoration How)> Decorate { get; init; } = [];

    /// <summary>Of those, the ones to send even if nothing differs from the last time.</summary>
    public IReadOnlySet<WindowHandle> Forced { get; init; } = new HashSet<WindowHandle>();

    /// <summary>Windows gaining or losing their button on the taskbar.</summary>
    public IReadOnlyList<(WindowHandle Window, bool Shown)> TaskbarButton { get; init; } = [];

    /// <summary>Where the focus should end up, or none to leave it alone.</summary>
    public WindowHandle Focus { get; init; } = WindowHandle.None;

    /// <summary>
    /// Take the keyboard off the focused window, because it has just been
    /// hidden and nothing is taking its place.
    /// </summary>
    /// <remarks>
    /// Cloaking does not move the foreground. A switch to an EMPTY workspace
    /// left every keystroke going to the window that had just vanished.
    /// </remarks>
    public bool Unfocus { get; init; }

    public bool IsNothing =>
        Place.Count == 0 && Hide.Count == 0 && Show.Count == 0 && Restore.Count == 0 && Unmaximize.Count == 0
        && Band.Count == 0 && Behind.Count == 0 && TaskbarMark.Count == 0 && Decorate.Count == 0
        && TaskbarButton.Count == 0 && Focus.IsNone && !Unfocus;

    public override string ToString() =>
        IsNothing
            ? "nothing to do"
            : $"{Place.Count} to place, {Hide.Count} to hide, {Show.Count} to show, {Restore.Count} to restore, {Unmaximize.Count} to unmaximize, "
              + $"{Band.Count} to reband, {Behind.Count} behind a game, {TaskbarMark.Count} to mark, {Decorate.Count} to decorate, {TaskbarButton.Count} to (un)button"
              + (Focus.IsNone ? string.Empty : $", focus {Focus}")
              + (Unfocus ? ", unfocus" : string.Empty);
}
