using System.Text.Json.Nodes;
using AkuWM.Core.Desk;
using AkuWM.Core.Layout;
using AkuWM.Core.Logging;
using AkuWM.Core.Model;

namespace AkuWM.Core.Compat;

/// <summary>What the model cannot do on its own, because it holds no Win32.</summary>
public interface IDeskPlatform
{
    bool Focus(WindowHandle window);

    void Minimize(WindowHandle window);

    void Restore(WindowHandle window);

    void Close(WindowHandle window);

    void Exec(string command);
}

/// <param name="Success">Whether the command was carried out.</param>
/// <param name="Error">Why not, in the shape the callers expect.</param>
/// <param name="Subject">The container the command acted on.</param>
/// <param name="Data">The payload, for a query.</param>
public readonly record struct ExecResult(bool Success, string? Error, Guid? Subject, JsonNode? Data)
{
    public static ExecResult Ok(Guid? subject = null, JsonNode? data = null) => new(true, null, subject, data);

    public static ExecResult Fail(string error) => new(false, error, null, null);
}

/// <summary>
/// Runs the commands and queries the existing scripts and the bar send.
/// </summary>
/// <remarks>
/// Every one of them ends up as a call on the model, which is the point: there
/// is one window manager underneath, and the compatibility layer is a way of
/// addressing it rather than a second implementation of it.
/// </remarks>
public sealed class GlazeExecutor
{
    private readonly Desk.Desk _desk;
    private readonly IDeskPlatform _platform;
    private readonly Func<ExecResult>? _reload;

    /// <param name="reload">
    /// Reads the configuration again and applies it, returning what happened
    /// or why it could not. Null where there is no configuration to read --
    /// the tests, and a command run without a daemon.
    /// </param>
    public GlazeExecutor(Desk.Desk desk, IDeskPlatform platform, Func<ExecResult>? reload = null)
    {
        _desk = desk;
        _platform = platform;
        _reload = reload;
    }

    /// <summary>Set when a command asks the window manager to stop.</summary>
    public bool ExitRequested { get; private set; }

    /// <summary>Set when the desk was told to leave everything where it is.</summary>
    public bool Paused => _desk.Paused;

    /// <summary>
    /// One request as the callers write it: <c>query workspaces</c>,
    /// <c>command focus --workspace 11</c>.
    /// </summary>
    /// <remarks>
    /// The split lives here rather than at each caller so the shim, the socket
    /// and the tests all reach the model through the same two words.
    /// </remarks>
    public ExecResult Ask(string request)
    {
        string verb = request.Split(' ', 2)[0].ToLowerInvariant();
        string rest = request.Length > verb.Length ? request[(verb.Length + 1)..] : string.Empty;

        return verb switch
        {
            "query" => Query(rest),
            "command" => Command(rest),
            _ => ExecResult.Fail($"unrecognized subcommand '{verb}'"),
        };
    }

    public ExecResult Query(string line)
    {
        ParsedCommand parsed = GlazeCommandLine.Parse(line);

        return parsed.Verb.ToLowerInvariant() switch
        {
            "monitors" => ExecResult.Ok(data: new JsonObject { ["monitors"] = GlazeView.Monitors(_desk) }),
            "workspaces" => ExecResult.Ok(data: new JsonObject { ["workspaces"] = GlazeView.Workspaces(_desk) }),
            "windows" => ExecResult.Ok(data: new JsonObject { ["windows"] = GlazeView.Windows(_desk) }),
            "focused" => ExecResult.Ok(data: new JsonObject { ["focused"] = Focused() }),
            "app-metadata" => ExecResult.Ok(data: new JsonObject
            {
                ["version"] = Commands.Build.Version,
                ["name"] = "AkuWM",
            }),
            "tiling-direction" => TilingDirection(),
            "binding-modes" => ExecResult.Ok(data: new JsonObject { ["bindingModes"] = new JsonArray() }),
            "paused" => ExecResult.Ok(data: (JsonNode)Paused),
            _ => ExecResult.Fail($"unrecognized subcommand '{parsed.Verb}'"),
        };
    }

    public ExecResult Command(string line)
    {
        ParsedCommand parsed = GlazeCommandLine.Parse(line);
        DeskWindow? subject = Subject(parsed);

        switch (parsed.Verb.ToLowerInvariant())
        {
            case "focus":
                return Focus(parsed);

            case "move":
                return Move(parsed, subject);

            case "position":
                return Position(parsed, subject);

            case "size":
                return Size(parsed, subject);

            case "resize":
                return Resize(parsed, subject);

            case "toggle-fullscreen":
                return Toggle(subject, w => _desk.SetFullscreen(w.Handle, w.State != WindowState.Fullscreen));

            case "set-fullscreen":
                return Toggle(subject, w => _desk.SetFullscreen(w.Handle, true));

            case "toggle-floating":
                return Toggle(subject, w => _desk.SetFloating(w.Handle, w.State != WindowState.Floating));

            case "set-floating":
                return Toggle(subject, w => _desk.SetFloating(w.Handle, true));

            case "toggle-tiling":
            case "set-tiling":
                return Toggle(subject, w => _desk.SetFloating(w.Handle, false));

            case "toggle-sticky":
                return Toggle(subject, w => _desk.SetSticky(w.Handle, !w.Sticky));

            case "set-sticky":
                return Toggle(subject, w => _desk.SetSticky(w.Handle, true));

            case "unset-sticky":
                return Toggle(subject, w => _desk.SetSticky(w.Handle, false));

            case "set-minimized":
                if (subject is null)
                {
                    return ExecResult.Fail("there is no focused window to minimize");
                }

                _platform.Minimize(subject.Handle);
                return ExecResult.Ok(subject.Id);

            case "toggle-minimized":
                if (subject is null)
                {
                    return ExecResult.Fail("there is no focused window to minimize");
                }

                // Both used to minimise, so a script could never raise a window
                // from the taskbar with the toggle.
                if (subject.State == WindowState.Minimized)
                {
                    _platform.Restore(subject.Handle);
                }
                else
                {
                    _platform.Minimize(subject.Handle);
                }

                return ExecResult.Ok(subject.Id);

            case "toggle-tiling-direction":
                return Toggle(subject, w => _desk.ToggleDirection(w.Handle));

            case "close":
                if (subject is null)
                {
                    return ExecResult.Fail("there is no focused window to close");
                }

                _platform.Close(subject.Handle);
                return ExecResult.Ok(subject.Id);

            case "shell-exec":
                _platform.Exec(string.Join(' ', parsed.Positional));
                return ExecResult.Ok();

            case "wm-redraw":
                // The redraw happens because the command ran at all: every
                // command is followed by one.
                return ExecResult.Ok();

            case "wm-toggle-pause":
                _desk.Paused = !_desk.Paused;
                return ExecResult.Ok();

            case "wm-exit":
                ExitRequested = true;
                return ExecResult.Ok();

            case "wm-reload-config":
                // It used to answer Ok and read nothing, which is the worst of
                // the three possible answers: a person edits a file, presses
                // the chord, is told it worked, and spends the next hour
                // wondering why the change did nothing.
                return _reload is null
                    ? ExecResult.Fail("there is no configuration to reload from here")
                    : _reload();

            case "move-workspace":
                // `lib-repair.ahk` moves a workspace back when it finds one on
                // the wrong monitor -- a thing that happens to the window
                // manager AkuWM replaces after a display change. AkuWM binds
                // every workspace to a monitor ROLE matched by EDID, so a
                // workspace is only ever on the monitor its role names and
                // there is nothing to move. Accepted so the repair loop does
                // not fail on it, and warned about because if it ever fires,
                // the premise above is the thing that is wrong.
                Log.Warn(
                    "'move-workspace' was asked for; AkuWM keeps each workspace on the monitor its " +
                    "role names, so nothing was moved");
                return ExecResult.Ok(parsed.Subject);

            case "ignore":
            case "adjust-borders":
            case "set-title-bar-visibility":
            case "set-transparency":
                // Accepted and not acted on: they exist in the grammar the
                // scripts were written against, and refusing them would fail a
                // gesture that never needed them.
                Log.Debug(() => $"'{parsed.Verb}' is accepted and does nothing in AkuWM");
                return ExecResult.Ok(subject?.Id);

            default:
                return ExecResult.Fail($"unrecognized subcommand '{parsed.Verb}'");
        }
    }

    // ---- the verbs --------------------------------------------------------

    private ExecResult Focus(ParsedCommand parsed)
    {
        if (parsed.Value("workspace") is { Length: > 0 } workspace)
        {
            WindowHandle target = _desk.FocusWorkspace(workspace);
            _desk.WantFocus(target);
            return ExecResult.Ok(_desk.Workspace(workspace)?.Id);
        }

        if (parsed.Value("container-id") is { Length: > 0 } id && Guid.TryParse(id, out Guid container))
        {
            DeskWindow? window = _desk.Windows.FirstOrDefault(w => w.Id == container);
            if (window is null)
            {
                return ExecResult.Fail($"no container with the id {id}");
            }

            ShowTheWorkspaceOf(window);
            _desk.WantFocus(window.Handle);
            return ExecResult.Ok(window.Id);
        }

        if (parsed.Value("direction") is { Length: > 0 } text && ParseDirection(text) is { } direction)
        {
            WindowHandle neighbour = _desk.InDirection(direction);
            if (neighbour.IsNone)
            {
                return ExecResult.Fail($"there is nothing to the {text}");
            }

            _desk.WantFocus(neighbour);
            return ExecResult.Ok(_desk.Window(neighbour)?.Id);
        }

        if (parsed.Number("monitor") is { } index)
        {
            if (index < 0 || index >= _desk.Monitors.Count)
            {
                return ExecResult.Fail($"there is no monitor {index}");
            }

            DeskMonitor monitor = _desk.Monitors[index];
            _desk.WantFocus(monitor.Displayed?.LastFocused ?? WindowHandle.None);
            return ExecResult.Ok(monitor.Id);
        }

        if (parsed.Has("next-workspace") || parsed.Has("prev-workspace"))
        {
            return FocusNeighbouringWorkspace(parsed.Has("next-workspace") ? 1 : -1);
        }

        return ExecResult.Fail("focus needs one of --workspace, --direction, --container-id or --monitor");
    }

    private ExecResult Move(ParsedCommand parsed, DeskWindow? subject)
    {
        if (subject is null)
        {
            return ExecResult.Fail("there is no window to move");
        }

        if (parsed.Value("workspace") is { Length: > 0 } workspace)
        {
            return _desk.MoveToWorkspace(subject.Handle, workspace)
                ? ExecResult.Ok(subject.Id)
                : ExecResult.Fail($"the window could not be moved to {workspace}");
        }

        if (parsed.Value("workspace-in-direction") is { Length: > 0 } sideways)
        {
            return MoveToNeighbouringWorkspace(subject, sideways);
        }

        if (parsed.Value("direction") is { Length: > 0 } text && ParseDirection(text) is { } direction)
        {
            // Moving a window that is not the focused one: the model's move
            // works on the focus, so the focus goes there first. It is where
            // the person is looking in every real case, and the scripts that
            // pass --id pass the focused window's id.
            if (_desk.Focused != subject.Handle)
            {
                _desk.Focus(subject.Handle);
            }

            return _desk.MoveFocused(direction)
                ? ExecResult.Ok(subject.Id)
                : ExecResult.Fail($"there is nowhere to the {text} to move it");
        }

        return ExecResult.Fail("move needs --workspace, --workspace-in-direction or --direction");
    }

    private ExecResult Resize(ParsedCommand parsed, DeskWindow? subject)
    {
        if (subject is null)
        {
            return ExecResult.Fail("there is no window to resize");
        }

        // Both, when both are given. An Alt+drag on a corner sends a width and
        // a height in one command, and honouring only the first turned every
        // diagonal resize into a horizontal one.
        bool any = false;
        bool all = true;

        if (parsed.Number("width") is { } width)
        {
            any = true;
            all &= Change(subject, Direction.Right, width, parsed.InPixels("width"));
        }

        if (parsed.Number("height") is { } height)
        {
            any = true;
            all &= Change(subject, Direction.Down, height, parsed.InPixels("height"));
        }

        if (!any)
        {
            return ExecResult.Fail("resize needs --width or --height");
        }

        return all ? ExecResult.Ok(subject.Id) : ExecResult.Fail("the size could not be changed");
    }

    /// <summary>
    /// <c>position --x-pos N --y-pos N</c>: put a floating window exactly there.
    /// </summary>
    /// <remarks>
    /// The layout journal and the raise-or-launch table both send this, with
    /// coordinates they remembered from the last time the window was seen. It
    /// was unrecognised, so every app launched by a chord lost the geometry it
    /// had, and the repair after a monitor came back placed nothing at all.
    /// </remarks>
    private ExecResult Position(ParsedCommand parsed, DeskWindow? subject)
    {
        if (subject is null)
        {
            return ExecResult.Fail("there is no window to position");
        }

        if (subject.FloatingRect is not { } rect)
        {
            return ExecResult.Fail("only a floating window can be positioned");
        }

        int x = parsed.Number("x-pos") ?? rect.X;
        int y = parsed.Number("y-pos") ?? rect.Y;

        return _desk.SetFloatingRect(subject.Handle, rect with { X = x, Y = y })
            ? ExecResult.Ok(subject.Id)
            : ExecResult.Fail("the window could not be positioned");
    }

    /// <summary>
    /// <c>size --width Npx --height Nph</c>: an exact size, not a change to one.
    /// </summary>
    /// <remarks>
    /// The partner of <c>position</c>, and sent right after it. `resize` says
    /// "by this much", this one says "be this big" -- reading one as the other
    /// gives a window the size of the difference.
    /// </remarks>
    private ExecResult Size(ParsedCommand parsed, DeskWindow? subject)
    {
        if (subject is null)
        {
            return ExecResult.Fail("there is no window to size");
        }

        if (subject.FloatingRect is not { } rect)
        {
            return ExecResult.Fail("only a floating window can be sized");
        }

        int width = parsed.Number("width") ?? rect.Width;
        int height = parsed.Number("height") ?? rect.Height;

        return _desk.SetFloatingRect(
            subject.Handle,
            rect with { Width = Math.Max(1, width), Height = Math.Max(1, height) })
            ? ExecResult.Ok(subject.Id)
            : ExecResult.Fail("the window could not be sized");
    }

    private bool Change(DeskWindow subject, Direction direction, int by, bool pixels) =>
        pixels
            ? _desk.ResizeByPixels(subject.Handle, direction, by)
            : _desk.Resize(subject.Handle, direction, by);

    private ExecResult Toggle(DeskWindow? subject, Func<DeskWindow, bool> change)
    {
        if (subject is null)
        {
            return ExecResult.Fail("there is no focused window");
        }

        return change(subject)
            ? ExecResult.Ok(subject.Id)
            : ExecResult.Fail("the window did not change");
    }

    private ExecResult FocusNeighbouringWorkspace(int by)
    {
        if (_desk.FocusedMonitor is not { } monitor || monitor.Displayed is not { } current)
        {
            return ExecResult.Fail("there is no focused monitor");
        }

        int at = monitor.Workspaces.IndexOf(current) + by;
        if (at < 0 || at >= monitor.Workspaces.Count)
        {
            return ExecResult.Fail("there is no workspace that way");
        }

        Workspace destination = monitor.Workspaces[at];
        _desk.WantFocus(_desk.FocusWorkspace(destination.Name));
        return ExecResult.Ok(destination.Id);
    }

    private ExecResult MoveToNeighbouringWorkspace(DeskWindow subject, string text)
    {
        if (ParseDirection(text) is not { } direction
            || _desk.Workspace(subject.Workspace ?? string.Empty) is not { } from
            || _desk.MonitorOf(from) is not { } monitor)
        {
            return ExecResult.Fail($"'{text}' is not a direction");
        }

        // The SCREEN in that direction first, which is what the chord means:
        // `Hyper+Shift+Left` is "send this window to the monitor on my left".
        // Walking this monitor's own list instead meant the leftmost workspace
        // had nothing to the left of it and the window never moved -- and when
        // it did move, it moved somewhere the person was not looking.
        if (_desk.MonitorInDirection(monitor, direction) is { Displayed: { } there })
        {
            return _desk.MoveToWorkspace(subject.Handle, there.Name)
                ? ExecResult.Ok(subject.Id)
                : ExecResult.Fail($"the window could not be moved to {there.Name}");
        }

        // No screen that way: the workspace next along on this one, which is
        // what it does on a single-monitor desk.
        int at = monitor.Workspaces.IndexOf(from) + (direction.IsBackwards() ? -1 : 1);
        if (at < 0 || at >= monitor.Workspaces.Count)
        {
            return ExecResult.Fail("there is no workspace that way");
        }

        Workspace destination = monitor.Workspaces[at];
        return _desk.MoveToWorkspace(subject.Handle, destination.Name)
            ? ExecResult.Ok(subject.Id)
            : ExecResult.Fail($"the window could not be moved to {destination.Name}");
    }

    // ---- helpers ----------------------------------------------------------

    private DeskWindow? Subject(ParsedCommand parsed)
    {
        if (parsed.Subject is { } id)
        {
            return _desk.Windows.FirstOrDefault(w => w.Id == id);
        }

        return _desk.Window(_desk.Focused) is { Managed: true } focused ? focused : null;
    }

    /// <summary>Brings the workspace a window lives on into view, if it is not.</summary>
    private void ShowTheWorkspaceOf(DeskWindow window)
    {
        if (window.Workspace is { } name && _desk.Workspace(name) is { Displayed: false })
        {
            _desk.FocusWorkspace(name);
        }
    }

    private JsonNode? Focused()
    {
        if (_desk.Window(_desk.Focused) is { Managed: true } window)
        {
            return GlazeView.Window(_desk, _desk.Workspace(window.Workspace ?? string.Empty), window);
        }

        // An empty workspace answers for itself, which is what the scripts
        // expect when nothing is focused.
        DeskMonitor? monitor = _desk.FocusedMonitor;
        return monitor?.Displayed is { } workspace ? GlazeView.Workspace(_desk, monitor, workspace) : null;
    }

    private ExecResult TilingDirection()
    {
        Workspace? workspace = _desk.Window(_desk.Focused) is { Workspace: { } name }
            ? _desk.Workspace(name)
            : _desk.FocusedMonitor?.Displayed;

        SplitDirection direction = workspace?.Tiling.Root?.Direction
                                   ?? workspace?.Direction
                                   ?? SplitDirection.Horizontal;

        return ExecResult.Ok(data: new JsonObject
        {
            ["tilingDirection"] = direction == SplitDirection.Vertical ? "vertical" : "horizontal",
            ["directionContainer"] = workspace?.Id.ToString(),
        });
    }

    private static Direction? ParseDirection(string text) => text.ToLowerInvariant() switch
    {
        "left" => Direction.Left,
        "right" => Direction.Right,
        "up" => Direction.Up,
        "down" => Direction.Down,
        _ => null,
    };
}
