namespace AkuWM.Core.Model;

/// <summary>What a window is, as the manager sees it.</summary>
public enum WindowState
{
    /// <summary>In the workspace's tree; its rectangle is computed by the layout.</summary>
    Tiling,

    /// <summary>Keeps its own rectangle, lives above the tiling layer.</summary>
    Floating,

    /// <summary>Covers its monitor and is left alone.</summary>
    Fullscreen,

    /// <summary>Out of the tree; remembers what it was before.</summary>
    Minimized,
}

/// <summary>Why a window is not managed.</summary>
public enum UnmanagedReason
{
    None,

    /// <summary>A rule says <c>ignore</c>.</summary>
    Rule,

    /// <summary>The application cloaked itself: it is in the tray, not on screen.</summary>
    SelfCloaked,

    /// <summary>It belongs to another native virtual desktop.</summary>
    OtherVirtualDesktop,

    /// <summary>
    /// Something else has it cloaked. Another window manager is hiding it, or
    /// the shell is: either way it is not AkuWM's to arrange.
    /// </summary>
    CloakedElsewhere,

    /// <summary>AkuWM's own windows.</summary>
    Ours,
}
