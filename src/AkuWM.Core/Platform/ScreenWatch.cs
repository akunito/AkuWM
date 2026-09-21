using AkuWM.Core.Model;

namespace AkuWM.Core.Platform;

/// <summary>
/// Noticing that the screens changed without being told.
/// </summary>
/// <remarks>
/// <para>
/// AkuWM hears about display changes as a Windows broadcast, and a broadcast
/// is the one kind of message that can simply not arrive: it goes to top-level
/// windows, a window that is busy elsewhere misses it, and the whole mechanism
/// was silently dead here for a while because the listener was a message-only
/// window, which broadcasts skip (fixed 2026-09-21).
/// </para>
/// <para>
/// What that cost is out of proportion to the cause. A desk laid out against
/// screens that are no longer there puts windows off the edge of the ones that
/// are, and nothing recovers until the daemon is restarted. So the list is
/// read again from time to time and compared: a handful of structs a few
/// seconds apart, against a window manager that cannot be wrong about where
/// the screens are.
/// </para>
/// </remarks>
public sealed class ScreenWatch
{
    /// <summary>
    /// How often the list is read again.
    /// </summary>
    /// <remarks>
    /// Plugging a monitor in is a thing a person does and then looks at the
    /// screen, so seconds are the unit, not milliseconds. The notification
    /// normally arrives first and this never fires at all.
    /// </remarks>
    public const int EveryMs = 4000;

    private readonly Func<long> _clock;
    private MonitorSnapshot[] _last = [];
    private long _readAt;

    public ScreenWatch(Func<long>? clock = null) =>
        _clock = clock ?? (() => Environment.TickCount64);

    /// <summary>
    /// True when the screens are not what they were. Reads the list only when
    /// enough time has passed, so this is free to call on every pass.
    /// </summary>
    public bool Changed(Func<IReadOnlyList<MonitorSnapshot>> read)
    {
        long now = _clock();
        if (_readAt != 0 && now - _readAt < EveryMs)
        {
            return false;
        }

        _readAt = now;
        IReadOnlyList<MonitorSnapshot> screens = read();

        if (Same(screens))
        {
            return false;
        }

        _last = [.. screens];
        return true;
    }

    /// <summary>What the screens were the last time this looked.</summary>
    public IReadOnlyList<MonitorSnapshot> Last => _last;

    private bool Same(IReadOnlyList<MonitorSnapshot> screens)
    {
        if (screens.Count != _last.Length)
        {
            return false;
        }

        // By hand rather than SequenceEqual: this runs on the window-manager
        // thread on a timer, and the comparison must not allocate an enumerator
        // per pass for a list of two.
        for (int at = 0; at < _last.Length; at++)
        {
            MonitorSnapshot was = _last[at];
            MonitorSnapshot now = screens[at];

            if (was.Handle != now.Handle
                || was.Bounds != now.Bounds
                || was.WorkArea != now.WorkArea
                || was.Dpi != now.Dpi
                || was.IsPrimary != now.IsPrimary
                || !string.Equals(was.HardwareId, now.HardwareId, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}
