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

    /// <summary>Windows entering or leaving the always-on-top band.</summary>
    public IReadOnlyList<(WindowHandle Window, bool Topmost)> Band { get; init; } = [];

    /// <summary>Windows the taskbar must be told about, so it drops behind a game.</summary>
    public IReadOnlyList<(WindowHandle Window, bool Fullscreen)> TaskbarMark { get; init; } = [];

    /// <summary>Windows whose border or corners should change.</summary>
    public IReadOnlyList<(WindowHandle Window, Decoration How)> Decorate { get; init; } = [];

    /// <summary>Windows gaining or losing their button on the taskbar.</summary>
    public IReadOnlyList<(WindowHandle Window, bool Shown)> TaskbarButton { get; init; } = [];

    /// <summary>Where the focus should end up, or none to leave it alone.</summary>
    public WindowHandle Focus { get; init; } = WindowHandle.None;

    public bool IsNothing =>
        Place.Count == 0 && Hide.Count == 0 && Show.Count == 0
        && Band.Count == 0 && TaskbarMark.Count == 0 && Decorate.Count == 0
        && TaskbarButton.Count == 0 && Focus.IsNone;

    public override string ToString() =>
        IsNothing
            ? "nothing to do"
            : $"{Place.Count} to place, {Hide.Count} to hide, {Show.Count} to show, "
              + $"{Band.Count} to reband, {TaskbarMark.Count} to mark, {Decorate.Count} to decorate, {TaskbarButton.Count} to (un)button"
              + (Focus.IsNone ? string.Empty : $", focus {Focus}");
}
