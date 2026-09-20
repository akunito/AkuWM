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
/// Windows parked on another native virtual desktop are cloaked with the very
/// same flag, and the shell will not let that one go: the call returns success
/// and the flag stays on (measured, spike S6). They are reported rather than
/// fixed, because uncloaking is not how a window comes back from another
/// desktop -- switching to it is.
/// </para>
/// <para>
/// The <c>IVirtualDesktopManager</c> answer is deliberately not used to tell
/// the two apart: on this Windows build it claims every window is on the
/// current desktop, including ones that demonstrably are not. What AkuWM
/// trusts instead is what it knows it did itself.
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

            // The call saying yes is not the window being visible. DWM is
            // asked again, because "hidden" means the flag is set and
            // "shown" means it is not -- and something else on the machine
            // may put it straight back.
            CloakKind after = _platform.Window(window.Handle)?.Cloak ?? CloakKind.None;
            bool stillCloaked = after.HasFlag(CloakKind.Shell);

            if (error is null && stillCloaked)
            {
                error = "the shell refused: the window is on another native virtual desktop, " +
                        "and uncloaking is not how it comes back — switch to that desktop";
            }

            var described = new
            {
                handle = window.Handle.Value,
                process = window.ProcessName,
                title = window.Title,
                error,
                cloakAfter = after.ToString(),
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
