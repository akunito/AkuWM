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

    /// <summary>Where AkuWM last put it, so an unchanged rectangle is not sent again.</summary>
    public Rect? Placed { get; set; }

    /// <summary>Whether AkuWM last put it in the always-on-top band.</summary>
    public bool? Banded { get; set; }

    /// <summary>Whether the taskbar has been told this window is fullscreen.</summary>
    public bool Marked { get; set; }

    /// <summary>Rules that fired for it, by name, for <c>query</c> and the GUI.</summary>
    public IReadOnlyList<string> Rules { get; set; } = [];

    public override string ToString() =>
        $"{Handle} {Snapshot.ProcessName} \"{Snapshot.Title}\" {State}"
        + (Workspace is null ? string.Empty : $" on {Workspace}")
        + (Sticky ? " sticky" : string.Empty)
        + (Hidden ? " hidden" : string.Empty);
}
