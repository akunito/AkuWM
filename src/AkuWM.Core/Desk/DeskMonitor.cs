using AkuWM.Core.Layout;
using AkuWM.Core.Model;

namespace AkuWM.Core.Desk;

/// <summary>
/// One physical display, its role, and the workspaces bound to that role.
/// </summary>
/// <remarks>
/// The role is the durable name -- <c>main</c>, <c>second</c> -- matched by
/// EDID. Everything above this holds roles; only this class knows which
/// rectangle the role currently points at, which is why a monitor going to
/// sleep and coming back in a different order moves no windows.
/// </remarks>
public sealed class DeskMonitor
{
    public DeskMonitor(MonitorSnapshot snapshot, string role)
    {
        Snapshot = snapshot;
        Role = role;
    }

    public MonitorSnapshot Snapshot { get; set; }

    public string Role { get; }

    public MonitorHandle Handle => Snapshot.Handle;

    public List<Workspace> Workspaces { get; } = [];

    /// <summary>The one on screen.</summary>
    public Workspace? Displayed => Workspaces.FirstOrDefault(w => w.Displayed);

    /// <summary>The one before it, for a workspace key pressed twice.</summary>
    public string? Previous { get; set; }

    /// <summary>Windows drawn on whichever workspace this monitor shows.</summary>
    public HashSet<WindowHandle> Sticky { get; } = [];

    /// <summary>The direction a new window splits along, from the shape of the screen.</summary>
    public SplitDirection NaturalDirection =>
        Snapshot.IsVertical ? SplitDirection.Vertical : SplitDirection.Horizontal;

    /// <summary>Where tiling happens: the work area, so the taskbar keeps its strip.</summary>
    public Rect TilingArea => Snapshot.WorkArea;

    /// <summary>What a fullscreen window covers: the whole screen, taskbar included.</summary>
    public Rect FullArea => Snapshot.Bounds;

    public override string ToString() =>
        $"{Role} {Snapshot.FriendlyName} [{Snapshot.HardwareId}] showing {Displayed?.Name ?? "nothing"}";
}
