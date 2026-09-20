using AkuWM.Core.Config;
using AkuWM.Core.Matching;

namespace AkuWM.Core.Model;

/// <summary>What AkuWM would do with one window, without doing it.</summary>
public sealed record ManagedWindow
{
    public required WindowSnapshot Window { get; init; }
    public required bool Managed { get; init; }
    public required UnmanagedReason Reason { get; init; }

    /// <summary>The rule that took it out, when one did.</summary>
    public string? ReasonDetail { get; init; }

    public required WindowState State { get; init; }
    public required bool Sticky { get; init; }

    /// <summary>The role of the monitor it is on, when that monitor has one.</summary>
    public string? MonitorRole { get; init; }

    /// <summary>Every rule that fired, by name, in configuration order.</summary>
    public required IReadOnlyList<string> Rules { get; init; }

    /// <summary>
    /// Where a rule says this window opens, when one does.
    /// </summary>
    /// <remarks>
    /// Carried out of the decision rather than matched again, so the rules are
    /// run over a window exactly once. The first rule that names a target
    /// wins, in configuration order.
    /// </remarks>
    public RuleTarget? Target { get; init; }
}

/// <summary>The whole desk, as AkuWM sees it at one instant.</summary>
public sealed record ShadowView
{
    public required IReadOnlyList<MonitorSnapshot> Monitors { get; init; }
    public required IReadOnlyDictionary<MonitorHandle, string> Roles { get; init; }
    public required IReadOnlyList<ManagedWindow> Windows { get; init; }
    public required WindowHandle Foreground { get; init; }

    public IEnumerable<ManagedWindow> Managed => Windows.Where(w => w.Managed);
}

/// <summary>
/// Builds the view of the desk from a configuration and a set of snapshots,
/// and changes nothing.
/// </summary>
/// <remarks>
/// <para>
/// This is M1's shadow mode: AkuWM decides what every window is -- managed or
/// not, tiling or floating or fullscreen, sticky or not, on which monitor role
/// -- while GlazeWM is still the one actually arranging them, so the two can
/// be compared on the live desktop before anything is handed over.
/// </para>
/// <para>
/// It is a pure function of its inputs, so the same decisions are exercised on
/// Linux against snapshots taken from the real desk.
/// </para>
/// </remarks>
public static class ShadowModel
{
    public static ShadowView Build(
        AkuWmConfig config,
        IReadOnlyList<MonitorSnapshot> monitors,
        IReadOnlyList<WindowSnapshot> windows,
        WindowHandle foreground = default,
        Func<WindowSnapshot, bool>? isOurs = null,
        IReadOnlySet<WindowHandle>? cloakedByUs = null)
    {
        Dictionary<MonitorHandle, string> roles = MonitorRoles.Resolve(config.Monitors ?? [], monitors);
        Dictionary<MonitorHandle, MonitorSnapshot> byHandle = monitors.ToDictionary(m => m.Handle);
        var matcher = new RuleMatcher();
        List<RuleConfig> rules = (config.Rules ?? []).Where(r => r.Enabled != false).ToList();

        var result = new List<ManagedWindow>(windows.Count);
        foreach (WindowSnapshot window in windows)
        {
            result.Add(Decide(window, rules, matcher, byHandle, roles, config, isOurs, cloakedByUs));
        }

        return new ShadowView
        {
            Monitors = monitors,
            Roles = roles,
            Windows = result,
            Foreground = foreground,
        };
    }

    /// <summary>
    /// What one window is: managed or not, tiling or floating or fullscreen,
    /// sticky or not, and on which monitor role.
    /// </summary>
    /// <remarks>
    /// Public because it is the same decision whether AkuWM is watching the
    /// desk or arranging it. Two answers to "is this window mine" would be two
    /// window managers, and the shadow diff that proved M1 would stop proving
    /// anything the moment they drifted.
    /// </remarks>
    public static ManagedWindow Decide(
        WindowSnapshot window,
        List<RuleConfig> rules,
        RuleMatcher matcher,
        Dictionary<MonitorHandle, MonitorSnapshot> monitors,
        Dictionary<MonitorHandle, string> roles,
        AkuWmConfig config,
        Func<WindowSnapshot, bool>? isOurs,
        IReadOnlySet<WindowHandle>? cloakedByUs)
    {
        var facts = new WindowFacts(window.ProcessName, window.ClassName, window.Title);
        List<RuleConfig> fired = matcher.Firing(rules, facts).ToList();
        List<string> actions = fired.SelectMany(r => r.Actions ?? []).ToList();

        monitors.TryGetValue(window.Monitor, out MonitorSnapshot? monitor);
        roles.TryGetValue(window.Monitor, out string? role);

        (bool managed, UnmanagedReason reason, string? detail) =
            Manageable(window, fired, isOurs, cloakedByUs);

        return new ManagedWindow
        {
            Window = window,
            Managed = managed,
            Reason = reason,
            ReasonDetail = detail,
            State = StateOf(window, monitor, actions, config),
            Sticky = actions.Contains("sticky") && !actions.Contains("unsticky"),
            MonitorRole = role,
            Rules = fired.Select(r => r.Name ?? r.Id ?? "?").ToList(),
            Target = fired.FirstOrDefault(r => r.Target is not null)?.Target,
        };
    }

    /// <param name="cloakedByUs">
    /// The windows AkuWM itself has hidden. Everything else the shell has
    /// cloaked belongs to somebody else -- another virtual desktop, or another
    /// window manager still running -- and is not AkuWM's to arrange. In
    /// shadow mode this set is empty, which is exactly right: AkuWM has hidden
    /// nothing, so every cloaked window is someone else's.
    /// </param>
    private static (bool Managed, UnmanagedReason Reason, string? Detail) Manageable(
        WindowSnapshot window,
        List<RuleConfig> fired,
        Func<WindowSnapshot, bool>? isOurs,
        IReadOnlySet<WindowHandle>? cloakedByUs)
    {
        if (isOurs?.Invoke(window) == true)
        {
            return (false, UnmanagedReason.Ours, null);
        }

        RuleConfig? ignored = fired.FirstOrDefault(r => r.Actions?.Contains("ignore") == true);
        if (ignored is not null)
        {
            return (false, UnmanagedReason.Rule, ignored.Name ?? ignored.Id);
        }

        // A window the application itself cloaked is in the tray, not on the
        // desk. The shell's cloak is a different matter: that is how a
        // workspace is hidden, and those windows are still managed.
        if (window.Cloak.HasFlag(CloakKind.App))
        {
            return (false, UnmanagedReason.SelfCloaked, null);
        }

        if (!window.OnCurrentVirtualDesktop)
        {
            return (false, UnmanagedReason.OtherVirtualDesktop, null);
        }

        if (window.Cloak.HasFlag(CloakKind.Shell) && cloakedByUs?.Contains(window.Handle) != true)
        {
            return (false, UnmanagedReason.CloakedElsewhere, null);
        }

        if (window.Cloak.HasFlag(CloakKind.InheritedOrOtherDesktop))
        {
            return (false, UnmanagedReason.OtherVirtualDesktop, null);
        }

        return (true, UnmanagedReason.None, null);
    }

    /// <summary>
    /// Minimised beats everything, then fullscreen, then what the rules and the
    /// window itself allow.
    /// </summary>
    /// <remarks>
    /// A window that cannot be resized is floated rather than fought with: a
    /// dialog put in a tiling slot either ignores the size or looks wrong, and
    /// on this desk that is most of what <c>#32770</c> produces.
    /// </remarks>
    public static WindowState StateOf(
        WindowSnapshot window, MonitorSnapshot? monitor, IReadOnlyList<string> actions, AkuWmConfig config)
    {
        if (window.IsMinimized || actions.Contains("minimize"))
        {
            return WindowState.Minimized;
        }

        if (actions.Contains("fullscreen") || IsFullscreen(window, monitor))
        {
            return WindowState.Fullscreen;
        }

        if (actions.Contains("float"))
        {
            return WindowState.Floating;
        }

        if (actions.Contains("tile"))
        {
            return WindowState.Tiling;
        }

        if (!window.IsResizable && config.Layout?.FloatUnresizable != false)
        {
            return WindowState.Floating;
        }

        return WindowState.Tiling;
    }

    /// <summary>
    /// Covering the monitor -- the <strong>whole</strong> monitor, not the work
    /// area.
    /// </summary>
    /// <remarks>
    /// The taskbar shrinks the work area, so judging fullscreen against it
    /// demotes a borderless game the moment the taskbar exists; that is the
    /// mistake the current setup has a config comment about. A maximised
    /// window counts as well: it covers what the user can see and must not be
    /// re-tiled.
    /// </remarks>
    public static bool IsFullscreen(WindowSnapshot window, MonitorSnapshot? monitor)
    {
        if (window.IsMinimized)
        {
            return false;
        }

        if (window.IsMaximized)
        {
            return true;
        }

        return monitor is not null && window.FrameBounds.Contains(monitor.Bounds);
    }
}
