using AkuWM.Core.Layout;
using AkuWM.Core.Model;

namespace AkuWM.Core.Desk;

/// <summary>
/// One workspace: a tiling tree, the windows floating over it, and at most one
/// window covering the lot.
/// </summary>
/// <remarks>
/// Three layers, sway's: tiling below, floating above it, fullscreen above
/// that. A window is in exactly one of them at a time, and changing which is
/// what <c>float</c>, <c>tile</c> and <c>fullscreen</c> do.
/// </remarks>
public sealed class Workspace
{
    public Workspace(string name, string monitorRole, SplitDirection? direction = null)
    {
        Name = name;
        MonitorRole = monitorRole;
        Direction = direction;
    }

    /// <summary>
    /// The id the bar and the scripts address it by.
    /// </summary>
    /// <remarks>
    /// A container id, stable for as long as this run: everything outside
    /// AkuWM identifies containers by these, and a workspace that changed its
    /// id when it was shown would break every script holding one.
    /// </remarks>
    public Guid Id { get; } = Guid.NewGuid();

    /// <summary>sway's numbering: <c>11</c>-<c>10</c> on the first role.</summary>
    public string Name { get; }

    /// <summary>What the bar shows, when it differs from the name.</summary>
    public string? DisplayName { get; set; }

    /// <summary>A monitor <em>role</em>, never an index.</summary>
    /// <summary>
    /// The role of the screen it belongs to.
    /// </summary>
    /// <remarks>
    /// Settable only from inside the model, and only by a reload: the
    /// configuration saying a workspace now belongs to another screen is a
    /// person editing the file, and the workspace should arrive there with its
    /// windows and its layout rather than being torn down and rebuilt.
    /// </remarks>
    public string MonitorRole { get; internal set; }

    /// <summary>
    /// Which way a new window splits the focused container, when the
    /// configuration says so for this workspace; null leaves it to the layout
    /// default and the monitor's shape (<c>Desk.DirectionFor</c>).
    /// </summary>
    /// <remarks>
    /// It was a plain Horizontal for every workspace without a setting, and
    /// the monitor's natural direction was consulted FIRST -- so the setting
    /// was dead, and an empty workspace on the portrait monitor answered
    /// "horizontal" to the bar while the next window would stack.
    /// </remarks>
    public SplitDirection? Direction { get; set; }

    /// <summary>Kept in the bar even when it is empty.</summary>
    public bool KeepAlive { get; set; }

    public TilingTree Tiling { get; } = new();

    /// <summary>Back to front; the last one is nearest the viewer.</summary>
    public List<WindowHandle> Floating { get; } = [];

    /// <summary>The window covering the monitor, if any.</summary>
    public WindowHandle Fullscreen { get; set; } = WindowHandle.None;

    /// <summary>Most recently focused first. What a workspace switch returns to.</summary>
    public List<WindowHandle> FocusOrder { get; } = [];

    public bool Displayed { get; set; }

    public IEnumerable<WindowHandle> Windows =>
        Tiling.Windows
            .Concat(Floating)
            .Concat(Fullscreen.IsNone ? [] : new[] { Fullscreen })
            .Distinct();

    public bool IsEmpty => !Windows.Any();

    // Straight at the three containers: the Concat/Distinct chain allocated a
    // kilobyte per call, and Compute calls it for every workspace.
    // None is in no workspace: an empty Fullscreen slot compared equal to it,
    // so a workspace with nothing covering it "contained" the absent focus and
    // told the bar it had it.
    public bool Contains(WindowHandle window) =>
        !window.IsNone && (Fullscreen == window || Floating.Contains(window) || Tiling.Contains(window));

    /// <summary>Puts a window at the front of the focus order.</summary>
    public void Touch(WindowHandle window)
    {
        FocusOrder.Remove(window);
        FocusOrder.Insert(0, window);
    }

    public void Release(WindowHandle window)
    {
        Tiling.Remove(window);
        Floating.Remove(window);
        FocusOrder.Remove(window);

        if (Fullscreen == window)
        {
            Fullscreen = WindowHandle.None;
        }
    }

    /// <summary>
    /// The window to focus when this workspace is shown: the last one that had
    /// it, or anything at all.
    /// </summary>
    public WindowHandle LastFocused =>
        FocusOrder.FirstOrDefault(Contains) is { IsNone: false } remembered
            ? remembered
            : Windows.FirstOrDefault();

    /// <summary>Raises a floating window to the front of its band.</summary>
    public void Raise(WindowHandle window)
    {
        if (Floating.Remove(window))
        {
            Floating.Add(window);
        }
    }

    public override string ToString() =>
        $"{Name}@{MonitorRole} {Tiling.Count} tiled, {Floating.Count} floating"
        + (Fullscreen.IsNone ? string.Empty : ", fullscreen");
}
