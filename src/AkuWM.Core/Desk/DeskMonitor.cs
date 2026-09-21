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

    /// <summary>The container id the bar and the scripts address it by.</summary>
    public Guid Id { get; } = Guid.NewGuid();

    public MonitorSnapshot Snapshot { get; set; }

    public string Role { get; }

    public MonitorHandle Handle => Snapshot.Handle;

    public List<Workspace> Workspaces { get; } = [];

    /// <summary>The one on screen.</summary>
    /// <summary>
    /// The workspace this screen is showing.
    /// </summary>
    /// <remarks>
    /// An indexed loop, not <c>FirstOrDefault</c>: this is a property getter
    /// read about twenty times per `query monitors`, and the bar sends one of
    /// those on every event, per widget. The LINQ version allocated a closure,
    /// a delegate and a boxed enumerator every time.
    /// </remarks>
    public Workspace? Displayed
    {
        get
        {
            for (int i = 0; i < Workspaces.Count; i++)
            {
                if (Workspaces[i].Displayed)
                {
                    return Workspaces[i];
                }
            }

            return null;
        }
    }

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
