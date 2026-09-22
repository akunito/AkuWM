using System.Text.Json.Nodes;
using AkuWM.Core.Config;
using AkuWM.Core.Ipc;
using AkuWM.Core.Model;
using AkuWM.Core.Platform;

namespace AkuWM.Core.Commands;

/// <summary>
/// <c>akuwm query ...</c>: what AkuWM sees, without changing any of it.
/// </summary>
/// <remarks>
/// In M1 every answer is computed from a fresh set of snapshots, because there
/// is no long-lived model yet: the point of shadow mode is to check the
/// decisions, not the bookkeeping. From M2 the same commands are answered from
/// the model on the wm thread, and the shapes below do not change.
/// </remarks>
public sealed class QueryCommands
{
    /// <summary>
    /// Read here rather than on every window of every enumeration: it is one
    /// out-of-process COM call to the shell, and this is its only consumer.
    /// </summary>
    private Func<WindowHandle, string?>? _virtualDesktopOf;

    public void ReadsVirtualDesktopWith(Func<WindowHandle, string?> read) => _virtualDesktopOf = read;

    private readonly IPlatform _platform;
    private readonly ConfigPaths _paths;

    public QueryCommands(IPlatform platform, ConfigPaths paths)
    {
        _platform = platform;
        _paths = paths;
    }

    public CommandResponse Execute(string line, string[] tokens)
    {
        if (tokens.Length < 2)
        {
            return CommandResponse.Fail(line, "query needs a subject: monitors, windows, focused");
        }

        return tokens[1].ToLowerInvariant() switch
        {
            "monitors" => Monitors(line),
            "windows" => Windows(line, CommandLine.Options(tokens, 2)),
            "focused" => Focused(line),
            _ => CommandResponse.Fail(line, $"'{tokens[1]}' is not monitors, windows or focused"),
        };
    }

    /// <summary>The desk as AkuWM sees it, built fresh.</summary>
    public ShadowView View()
    {
        AkuWmConfig config = ConfigStore.Load(_paths).Effective;
        return ShadowModel.Build(
            config,
            _platform.Monitors(),
            _platform.Windows(),
            _platform.Foreground());
    }

    private CommandResponse Monitors(string line)
    {
        ShadowView view = View();
        return CommandResponse.Ok(line, view.Monitors.Select(monitor => new
        {
            role = view.Roles.GetValueOrDefault(monitor.Handle),
            handle = monitor.Handle.Value,
            device = monitor.DeviceName,
            name = monitor.FriendlyName,
            hardwareId = monitor.HardwareId,
            x = monitor.Bounds.X,
            y = monitor.Bounds.Y,
            width = monitor.Bounds.Width,
            height = monitor.Bounds.Height,
            workArea = new
            {
                x = monitor.WorkArea.X,
                y = monitor.WorkArea.Y,
                width = monitor.WorkArea.Width,
                height = monitor.WorkArea.Height,
            },
            dpi = monitor.Dpi,
            scaleFactor = monitor.ScaleFactor,
            primary = monitor.IsPrimary,
            vertical = monitor.IsVertical,
        }).ToList());
    }

    private CommandResponse Windows(string line, Dictionary<string, string?> options)
    {
        ShadowView view = View();
        bool all = options.ContainsKey("all");

        IEnumerable<ManagedWindow> windows = all ? view.Windows : view.Managed;

        return CommandResponse.Ok(line, windows.Select(w => Describe(w, _virtualDesktopOf)).ToList());
    }

    private CommandResponse Focused(string line)
    {
        ShadowView view = View();
        ManagedWindow? focused = view.Windows.FirstOrDefault(w => w.Window.Handle == view.Foreground);

        return focused is null
            ? CommandResponse.Ok(line, new { handle = view.Foreground.Value, managed = false })
            : CommandResponse.Ok(line, Describe(focused, _virtualDesktopOf));
    }

    public static JsonObject Describe(ManagedWindow managed, Func<WindowHandle, string?>? virtualDesktopOf = null)
    {
        WindowSnapshot window = managed.Window;
        (int top, int right, int bottom, int left) = window.BorderDelta;

        return new JsonObject
        {
            ["handle"] = window.Handle.Value,
            ["processName"] = window.ProcessName,
            ["processId"] = window.ProcessId,
            ["className"] = window.ClassName,
            ["title"] = window.Title,
            ["managed"] = managed.Managed,
            ["unmanagedReason"] = managed.Managed ? null : managed.Reason.ToString(),
            ["unmanagedBy"] = managed.ReasonDetail,
            ["state"] = managed.State.ToString().ToLowerInvariant(),
            ["sticky"] = managed.Sticky,
            ["monitor"] = managed.MonitorRole,
            ["monitorHandle"] = window.Monitor.Value,
            ["x"] = window.FrameBounds.X,
            ["y"] = window.FrameBounds.Y,
            ["width"] = window.FrameBounds.Width,
            ["height"] = window.FrameBounds.Height,
            ["borderDelta"] = new JsonObject
            {
                ["top"] = top,
                ["right"] = right,
                ["bottom"] = bottom,
                ["left"] = left,
            },
            ["cloak"] = window.Cloak.ToString(),
            ["onCurrentDesktop"] = window.OnCurrentVirtualDesktop,
            ["virtualDesktop"] = virtualDesktopOf?.Invoke(window.Handle) ?? window.VirtualDesktop,
            ["minimized"] = window.IsMinimized,
            ["maximized"] = window.IsMaximized,
            ["topmost"] = window.IsTopmost,
            ["resizable"] = window.IsResizable,
            ["elevated"] = window.IsElevated,
            ["perMonitorDpi"] = window.PerMonitorDpi,
            ["rules"] = new JsonArray([.. managed.Rules.Select(r => (JsonNode)r!)]),
        };
    }
}
