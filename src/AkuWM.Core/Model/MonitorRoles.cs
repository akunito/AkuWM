using AkuWM.Core.Config;

namespace AkuWM.Core.Model;

/// <summary>
/// Decides which physical monitor is <c>main</c>, which is <c>second</c>, and
/// so on.
/// </summary>
/// <remarks>
/// A role is matched on identity -- the EDID segment of the device path, or
/// failing that the friendly name -- because that is what survives this desk's
/// monitors going to sleep and coming back in another order. A configured role
/// with no identity yet falls back to position (primary first), which is
/// exactly the fragility the configuration's warning is about.
/// </remarks>
public static class MonitorRoles
{
    /// <param name="configured">The roles the configuration declares, in order.</param>
    /// <param name="present">The monitors the machine currently has, primary first.</param>
    /// <returns>Monitor handle → role, for the monitors that got one.</returns>
    public static Dictionary<MonitorHandle, string> Resolve(
        IReadOnlyList<MonitorConfig> configured,
        IReadOnlyList<MonitorSnapshot> present)
    {
        var roles = new Dictionary<MonitorHandle, string>();
        var takenRoles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var takenMonitors = new HashSet<MonitorHandle>();

        // Identity first, so a role never lands on the wrong screen because
        // something else was plugged in.
        foreach (MonitorConfig role in configured)
        {
            if (role.Id is not { Length: > 0 } id || role.Enabled == false)
            {
                continue;
            }

            MonitorSnapshot? match = present.FirstOrDefault(
                monitor => !takenMonitors.Contains(monitor.Handle) && Matches(role, monitor));

            if (match is not null)
            {
                roles[match.Handle] = id;
                takenRoles.Add(id);
                takenMonitors.Add(match.Handle);
            }
        }

        // Then whatever is left, by position, to the roles that are left.
        List<string> remainingRoles = configured
            .Where(role => role.Id is { Length: > 0 } && role.Enabled != false)
            .Select(role => role.Id!)
            .Where(id => !takenRoles.Contains(id))
            .ToList();

        foreach (MonitorSnapshot monitor in present.Where(m => !takenMonitors.Contains(m.Handle)))
        {
            if (remainingRoles.Count == 0)
            {
                break;
            }

            roles[monitor.Handle] = remainingRoles[0];
            remainingRoles.RemoveAt(0);
        }

        return roles;
    }

    public static bool Matches(MonitorConfig role, MonitorSnapshot monitor)
    {
        MonitorMatch? match = role.Match;
        if (match is null)
        {
            return false;
        }

        if (match.Edid is { Length: > 0 } edid)
        {
            return string.Equals(edid, monitor.HardwareId, StringComparison.OrdinalIgnoreCase);
        }

        if (match.DevicePath is { Length: > 0 } path)
        {
            return monitor.HardwareId.Length > 0
                   && path.Contains(monitor.HardwareId, StringComparison.OrdinalIgnoreCase);
        }

        if (match.Name is { Length: > 0 } name)
        {
            return string.Equals(name, monitor.FriendlyName, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }
}
