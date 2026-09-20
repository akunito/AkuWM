using AkuWM.Core.Ipc;
using AkuWM.Core.Logging;
using AkuWM.Core.Model;
using AkuWM.Core.Platform;

namespace AkuWM.Core.Commands;

/// <summary>
/// <c>akuwm uncloak-all</c>: give every window back.
/// </summary>
/// <remarks>
/// <para>
/// A window manager that hides windows by cloaking them can leave them hidden
/// if it dies at the wrong moment, and a cloaked window is invisible in every
/// way that matters -- it is not on the taskbar, Alt+Tab does not find it, and
/// nothing on screen suggests it exists. This is the way out, and it is a
/// command rather than an automatic recovery because the person running it
/// knows something went wrong and the program does not.
/// </para>
/// <para>
/// Windows parked on another native virtual desktop are cloaked too, by the
/// shell, and are deliberately left alone: uncloaking those would drag them
/// onto the desktop in front of the user, which is not giving anything back.
/// </para>
/// </remarks>
public sealed class UncloakCommand
{
    private readonly IPlatform _platform;
    private readonly IPlatformActions _actions;

    public UncloakCommand(IPlatform platform, IPlatformActions actions)
    {
        _platform = platform;
        _actions = actions;
    }

    public CommandResponse Execute(string line)
    {
        var uncloaked = new List<object>();
        var refused = new List<object>();
        var skipped = new List<object>();

        foreach (WindowSnapshot window in _platform.Windows())
        {
            if (!window.Cloak.HasFlag(CloakKind.Shell))
            {
                continue;
            }

            if (!window.OnCurrentVirtualDesktop)
            {
                skipped.Add(new
                {
                    handle = window.Handle.Value,
                    process = window.ProcessName,
                    title = window.Title,
                    why = "on another virtual desktop",
                });
                continue;
            }

            string? error = _actions.SetCloak(window.Handle, false);
            var described = new
            {
                handle = window.Handle.Value,
                process = window.ProcessName,
                title = window.Title,
                error,
            };

            if (error is null)
            {
                uncloaked.Add(described);
                Log.Info($"uncloaked {window}");
            }
            else
            {
                refused.Add(described);
                Log.Warn($"could not uncloak {window}: {error}");
            }
        }

        return CommandResponse.Ok(line, new
        {
            uncloaked = uncloaked.Count,
            refused = refused.Count,
            skipped = skipped.Count,
            windows = uncloaked,
            refusals = refused,
            leftAlone = skipped,
        });
    }
}
