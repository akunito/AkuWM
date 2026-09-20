using AkuWM.Core.Platform;

namespace AkuWM.Core.Wm;

/// <summary>What one platform event costs to answer.</summary>
public enum EventResponse
{
    /// <summary>Read the whole desk again: the set of windows has changed.</summary>
    ReadTheDesk,

    /// <summary>Read one window: something about it changed.</summary>
    ReadTheWindow,

    /// <summary>The focus moved. Nothing needs reading.</summary>
    TheFocusMoved,

    /// <summary>The screens changed. Roles are resolved again, then the desk is read.</summary>
    TheScreensChanged,

    /// <summary>Nothing AkuWM needs to do.</summary>
    Nothing,
}

/// <summary>
/// How much work each thing Windows says is worth.
/// </summary>
/// <remarks>
/// This is the difference between a gesture that costs microseconds and one
/// that costs milliseconds. Reading every window on the desk to learn that one
/// of them moved is the cost that made the stack AkuWM replaces feel slow --
/// and moving a window produces dozens of these events, not one.
/// </remarks>
public static class WmEvents
{
    public static EventResponse Decide(PlatformEventKind kind) => kind switch
    {
        // The set of windows changed, so the set is read again.
        PlatformEventKind.WindowCreated => EventResponse.ReadTheDesk,
        PlatformEventKind.WindowDestroyed => EventResponse.ReadTheDesk,
        PlatformEventKind.WindowShown => EventResponse.ReadTheDesk,
        PlatformEventKind.WindowHidden => EventResponse.ReadTheDesk,

        PlatformEventKind.ForegroundChanged => EventResponse.TheFocusMoved,

        PlatformEventKind.DisplayChanged => EventResponse.TheScreensChanged,

        // One window, read by itself.
        PlatformEventKind.WindowMoved => EventResponse.ReadTheWindow,
        PlatformEventKind.WindowMinimizeStart => EventResponse.ReadTheWindow,
        PlatformEventKind.WindowMinimizeEnd => EventResponse.ReadTheWindow,
        PlatformEventKind.WindowCloaked => EventResponse.ReadTheWindow,
        PlatformEventKind.WindowUncloaked => EventResponse.ReadTheWindow,
        PlatformEventKind.WindowTitleChanged => EventResponse.ReadTheWindow,

        // A setting change can move the work area; power events are for the
        // milestone that owns suspend and resume.
        PlatformEventKind.SettingsChanged => EventResponse.TheScreensChanged,
        PlatformEventKind.PowerSuspend => EventResponse.Nothing,
        PlatformEventKind.PowerResume => EventResponse.TheScreensChanged,

        _ => EventResponse.ReadTheDesk,
    };
}
