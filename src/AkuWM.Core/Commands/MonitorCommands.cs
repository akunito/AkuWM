using AkuWM.Core.Config;
using AkuWM.Core.Ipc;
using AkuWM.Core.Model;
using AkuWM.Core.Platform;

namespace AkuWM.Core.Commands;

/// <summary>
/// <c>akuwm monitors ...</c>: the roles, and how they are recognised.
/// </summary>
public sealed class MonitorCommands
{
    private readonly IPlatform _platform;
    private readonly ConfigPaths _paths;

    public MonitorCommands(IPlatform platform, ConfigPaths paths)
    {
        _platform = platform;
        _paths = paths;
    }

    public CommandResponse Execute(string line, string[] tokens)
    {
        if (tokens.Length < 2)
        {
            return CommandResponse.Fail(line, "monitors needs a verb: list, identify");
        }

        return tokens[1].ToLowerInvariant() switch
        {
            "list" => List(line),
            "identify" => Identify(line, CommandLine.Options(tokens, 2)),
            _ => CommandResponse.Fail(line, $"'{tokens[1]}' is not list or identify"),
        };
    }

    private CommandResponse List(string line)
    {
        LoadedConfig loaded = ConfigStore.Load(_paths);
        IReadOnlyList<MonitorSnapshot> present = _platform.Monitors();
        Dictionary<MonitorHandle, string> roles =
            MonitorRoles.Resolve(loaded.Effective.Monitors ?? [], present);

        return CommandResponse.Ok(line, present.Select(monitor => new
        {
            role = roles.GetValueOrDefault(monitor.Handle),
            name = monitor.FriendlyName,
            hardwareId = monitor.HardwareId,
            device = monitor.DeviceName,
            bounds = monitor.Bounds.ToString(),
            dpi = monitor.Dpi,
            primary = monitor.IsPrimary,
            identified = loaded.Effective.Monitors?
                .Any(m => MonitorRoles.Matches(m, monitor)) == true,
        }).ToList());
    }

    /// <summary>
    /// Writes each present monitor's identity into the machine layer, against
    /// the role it currently holds.
    /// </summary>
    /// <remarks>
    /// This is the step the configuration cannot do on its own: the EDID can
    /// only be read on the machine the monitors are plugged into. Until it has
    /// run, roles are assigned by position -- which a sleep cycle can change,
    /// and does. The identities go in the machine layer, not the shared one:
    /// they are true of this desk and no other.
    /// </remarks>
    private CommandResponse Identify(string line, Dictionary<string, string?> options)
    {
        bool dryRun = options.ContainsKey("dry-run");
        LoadedConfig loaded = ConfigStore.Load(_paths);
        IReadOnlyList<MonitorSnapshot> present = _platform.Monitors();
        Dictionary<MonitorHandle, string> roles =
            MonitorRoles.Resolve(loaded.Effective.Monitors ?? [], present);

        AkuWmConfig layer = loaded.Profile ?? new AkuWmConfig { Version = ConfigDefaults.SchemaVersion };
        layer.Monitors ??= [];

        var written = new List<object>();
        long now = ConfigStore.Now();

        foreach (MonitorSnapshot monitor in present)
        {
            if (!roles.TryGetValue(monitor.Handle, out string? role))
            {
                continue;
            }

            if (monitor.HardwareId.Length == 0)
            {
                continue;
            }

            MonitorConfig? entry = layer.Monitors.FirstOrDefault(
                m => string.Equals(m.Id, role, StringComparison.OrdinalIgnoreCase));

            if (entry is null)
            {
                entry = new MonitorConfig { Id = role };
                layer.Monitors.Add(entry);
            }

            entry.Match = new MonitorMatch { Edid = monitor.HardwareId, Name = monitor.FriendlyName.Trim() };
            entry.Name = monitor.FriendlyName.Trim();
            entry.Orientation = monitor.IsVertical ? "vertical" : "horizontal";
            entry.Notes = $"identified on this machine; {monitor.Bounds} at {monitor.Dpi} dpi";
            entry.UpdatedAt = now;

            written.Add(new { role, hardwareId = monitor.HardwareId, name = entry.Name });
        }

        if (!dryRun && written.Count > 0)
        {
            ConfigStore.Save(_paths.ProfileFile, layer);
        }

        return CommandResponse.Ok(line, new
        {
            dryRun,
            written = !dryRun && written.Count > 0,
            target = _paths.ProfileFile,
            monitors = written,
        });
    }
}
