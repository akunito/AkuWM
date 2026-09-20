using System.Text.Json.Nodes;
using AkuWM.Core.Compat;
using AkuWM.Core.Config;
using AkuWM.Core.Desk;
using AkuWM.Core.Logging;
using AkuWM.Core.Model;
using AkuWM.Core.Platform;
using AkuWM.Core.State;
using AkuWM.Core.Wm;
using AkuWM.Platform;

namespace AkuWM.App;

/// <summary>
/// The running window manager: the desk, the one thread that owns it, and the
/// hooks that tell it what happened.
/// </summary>
/// <remarks>
/// <para>
/// This is the only class where the three halves meet. The model
/// (<see cref="Core.Desk.Desk"/>) decides and holds no Win32; the platform
/// applies and decides nothing; this joins them on a single thread and makes
/// sure the order is always the same -- something happened, look, decide,
/// apply, once.
/// </para>
/// <para>
/// Events from Windows arrive in bursts: moving one window produces dozens of
/// them. Each is applied to the model as it comes, and the desk is redrawn
/// once when the burst is over, which is why a workspace switch is one batch
/// of window moves rather than a dozen.
/// </para>
/// </remarks>
public sealed class WindowManager : IAsyncDisposable
{
    private readonly WindowsPlatform _platform;
    private readonly Win32Hooks _hooks = new();
    private readonly Win32Taskbar _taskbar = new();
    private readonly DeskApplier _applier;
    private readonly GeometryJournal _journal;
    private readonly WmLoop _loop;
    private readonly Desk _desk;

    private readonly GlazeExecutor _executor;
    private readonly GlazeIpcServer _server;

    private bool _dirty;
    private bool _resync;

    /// <summary>What the desk looked like at the last redraw, to say what changed.</summary>
    private WindowHandle _wasFocused = WindowHandle.None;
    private Dictionary<string, string> _wasDisplayed = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<WindowHandle> _wasManaged = [];

    /// <param name="manage">
    /// False leaves AkuWM watching: the model is kept up to date and every
    /// query answers, but nothing on the screen is touched. That is shadow
    /// mode, and it is also what safe mode falls back to.
    /// </param>
    public WindowManager(
        AkuWmConfig config,
        WindowsPlatform platform,
        CloakLedger ledger,
        GeometryJournal journal,
        Watchdog watchdog,
        bool manage,
        int compatPort = GlazeProtocol.Port)
    {
        _platform = platform;
        _journal = journal;
        _applier = new DeskApplier(
            platform, platform, ledger, journal, _taskbar, ImmersiveShell.EveryUncloak);
        Managing = manage;

        _desk = new Desk(config, isOurs: IsOurs)
        {
            CanPositionElevated = Win32Token.HasUiAccess(),
        };

        _applier.ReadsFrom(h => _desk.Window(h)?.Snapshot);

        // A window that has closed is not one AkuWM has to put back.
        _desk.Forgotten += journal.Forget;
        _desk.ChecksHandlesWith(h => Win32Windows.IsWindow(h));

        _loop = new WmLoop(OnEvent, watchdog.Beat, onBatchEnd: Redraw);

        _executor = new GlazeExecutor(_desk, new Win32DeskPlatform());

        // Everything the bar and the scripts say arrives here and is answered
        // on the wm thread, so a query never sees a half-applied workspace
        // switch.
        _server = new GlazeIpcServer(compatPort, request => Ask(request).GetAwaiter().GetResult());
        _server.Traffic += (outbound, text) => Log.Debug(() => (outbound ? "compat > " : "compat < ") + text);
    }

    /// <summary>Whether the bar and the scripts can reach AkuWM.</summary>
    public GlazeIpcServer Compat => _server;

    /// <summary>Raised when a command asked the window manager to stop.</summary>
    public event Action? ExitRequested;

    /// <summary>Whether AkuWM is arranging the desk or only watching it.</summary>
    public bool Managing { get; private set; }

    /// <summary>
    /// Whether hiding a window is something this machine lets AkuWM undo.
    /// </summary>
    /// <remarks>
    /// Proven on the first hide of the run. False means every workspace shows
    /// all of its windows, which is a bad desk and better than a lost window.
    /// </remarks>
    public bool CanHide => _applier.CanHide;

    /// <summary>The last redraw, for the bench and for <c>doctor</c>.</summary>
    public ApplyResult Last { get; private set; }

    public long Redraws { get; private set; }

    /// <summary>
    /// Runs something against the model on the thread that owns it, and
    /// redraws afterwards.
    /// </summary>
    public Task<T> Do<T>(string what, Func<Desk, T> work) =>
        _loop.Post(what, () =>
        {
            T result = work(_desk);
            _dirty = true;
            return result;
        });

    /// <summary>Reads the model on its own thread, without asking for a redraw.</summary>
    public Task<T> Read<T>(string what, Func<Desk, T> work) => _loop.Post(what, () => work(_desk));

    /// <summary>
    /// Answers one request in the grammar the bar and the scripts speak.
    /// </summary>
    /// <remarks>
    /// A query is read on the wm thread and changes nothing; a command runs
    /// there too and asks for a redraw, so a gesture is one batch of window
    /// moves however many commands it took to express.
    /// </remarks>
    public Task<ExecResult> Ask(string request)
    {
        string verb = request.Split(' ', 2)[0].ToLowerInvariant();
        string rest = request.Length > verb.Length ? request[(verb.Length + 1)..] : string.Empty;

        return verb switch
        {
            "query" => _loop.Post(request, () => _executor.Query(rest)),
            "command" => _loop.Post(request, () =>
            {
                ExecResult result = _executor.Command(rest);
                _dirty = true;

                if (_executor.ExitRequested)
                {
                    ExitRequested?.Invoke();
                }

                return result;
            }),
            _ => Task.FromResult(ExecResult.Fail($"unrecognized subcommand '{verb}'")),
        };
    }

    /// <summary>The same, wrapped in the envelope the callers expect.</summary>
    public JsonObject Envelope(string request) =>
        GlazeProtocol.Reply(request, Ask(request).GetAwaiter().GetResult());

    public void Start()
    {
        _loop.Start();
        _server.Start();

        _hooks.Event += _loop.Enqueue;
        _hooks.Start();

        // The first pass has to happen on the wm thread like every other one,
        // so nothing can be looking at a half-built desk.
        _loop.Post("adopting the desk", () =>
        {
            _desk.SetMonitors(_platform.Monitors());
            _desk.Sync(_platform.Windows());
            _desk.Focus(_platform.Foreground());

            Log.Info($"adopted {_desk}");
            foreach (DeskMonitor monitor in _desk.Monitors)
            {
                Log.Info($"  {monitor}");
            }

            _dirty = true;
        }).GetAwaiter().GetResult();
    }

    /// <summary>Starts arranging a desk it was only watching.</summary>
    public void Manage(bool manage)
    {
        Managing = manage;
        _dirty = true;
    }

    private void OnEvent(PlatformEvent platformEvent)
    {
        switch (WmEvents.Decide(platformEvent.Kind))
        {
            case EventResponse.ReadTheDesk:
                _resync = true;
                break;

            case EventResponse.ReadTheDeskIfItCouldBeOurs:
                // Three cheap calls against a hundred-window enumeration.
                if (!Win32Windows.CouldBeManaged(platformEvent.Handle))
                {
                    return;
                }

                _resync = true;
                break;

            case EventResponse.ReadTheDeskIfWeKnowIt:
                // A dictionary lookup, and never the candidate test: a
                // destroyed window fails that, and would never be forgotten.
                if (_desk.Window(platformEvent.Handle) is null)
                {
                    return;
                }

                _resync = true;
                break;

            case EventResponse.TheFocusMoved:
                // Dirty either way: a focus change moves nothing, and the bar
                // still has to be told. Desk.Focus has already asked for the
                // keyboard back when it refused.
                if (!_desk.Focus(platformEvent.Handle))
                {
                    Log.Debug(() => $"refused the focus for the hidden window {platformEvent.Handle}");
                }

                _dirty = true;
                return;

            case EventResponse.TheScreensChanged:
                _desk.SetMonitors(_platform.Monitors());
                _resync = true;
                break;

            case EventResponse.Nothing:
                return;

            default:
                UpdateOne(platformEvent.Handle);
                break;
        }

        _dirty = true;
    }

    /// <summary>One window changed; read that one rather than the whole desk.</summary>
    private void UpdateOne(WindowHandle handle)
    {
        if (_desk.Window(handle) is null)
        {
            // Not a window AkuWM knows: it may have become one (a splash
            // screen that turned into an application window).
            _resync = true;
            return;
        }

        if (_platform.Window(handle) is { } snapshot)
        {
            _desk.Observe(snapshot);
        }
        else
        {
            _desk.Forget(handle);
        }
    }

    /// <summary>Once per burst: look if anything appeared, decide, apply.</summary>
    private void Redraw()
    {
        if (!_dirty && !_resync)
        {
            return;
        }

        if (_resync)
        {
            _resync = false;
            _desk.Sync(_platform.Windows());
        }

        _dirty = false;

        if (Managing)
        {
            Redraw redraw = _desk.Compute();
            if (!redraw.IsNothing)
            {
                Redraws++;
                Last = _applier.Apply(redraw);
                _desk.Applied(redraw, Last.Refused, Last.Unmarked);

                // The platform proves, once, that a window it hides can be
                // brought back. If it cannot, the model stops asking.
                _desk.CanHide = _applier.CanHide;
            }
        }

        // Always, and last: a focus change moves no window, and the bar still
        // has to hear about it. Publishing only after a redraw that did
        // something meant clicking between two windows told the bar nothing,
        // and a watching run told it nothing at all.
        Publish();
    }

    /// <summary>
    /// Tells the bar what changed, by comparing the desk with what it was.
    /// </summary>
    /// <remarks>
    /// Worked out by difference rather than raised at each call site, so a
    /// gesture that moves three windows and switches a workspace produces the
    /// events that describe the result, not a running commentary on how it was
    /// reached.
    /// </remarks>
    private void Publish()
    {
        if (_server.Connections == 0)
        {
            return;
        }

        foreach (DeskMonitor monitor in _desk.Monitors)
        {
            string? now = monitor.Displayed?.Name;
            _wasDisplayed.TryGetValue(monitor.Role, out string? before);

            if (now is null || string.Equals(now, before, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (before is not null && _desk.Workspace(before) is { } left)
            {
                Fire("workspace_deactivated", new JsonObject
                {
                    ["deactivatedWorkspace"] = GlazeView.Workspace(_desk, monitor, left),
                });
            }

            Fire("workspace_activated", new JsonObject
            {
                ["activatedWorkspace"] = GlazeView.Workspace(_desk, monitor, monitor.Displayed!),
            });

            _wasDisplayed[monitor.Role] = now;
        }

        HashSet<WindowHandle> managed = _desk.Windows
            .Where(w => w.Managed)
            .Select(w => w.Handle)
            .ToHashSet();

        foreach (WindowHandle handle in managed.Except(_wasManaged))
        {
            if (_desk.Window(handle) is { } window)
            {
                Fire("window_managed", new JsonObject
                {
                    ["managedWindow"] = GlazeView.Window(
                        _desk, _desk.Workspace(window.Workspace ?? string.Empty), window),
                });
            }
        }

        foreach (WindowHandle handle in _wasManaged.Except(managed))
        {
            Fire("window_unmanaged", new JsonObject
            {
                ["unmanagedHandle"] = handle.Value,
            });
        }

        _wasManaged = managed;

        if (_desk.Focused != _wasFocused)
        {
            _wasFocused = _desk.Focused;

            if (_desk.Window(_wasFocused) is { Managed: true } focused)
            {
                Fire("focus_changed", new JsonObject
                {
                    ["focusedContainer"] = GlazeView.Window(
                        _desk, _desk.Workspace(focused.Workspace ?? string.Empty), focused),
                });
            }
        }
    }

    /// <summary>
    /// Sends an event without waiting for it.
    /// </summary>
    /// <remarks>
    /// The wm thread must never wait on a socket. A bar that has stopped
    /// reading would otherwise stop the desk, which is the wrong way round:
    /// the desk is the thing that has to keep working.
    /// </remarks>
    private Task _publishing = Task.CompletedTask;

    private void Fire(string eventType, JsonObject payload) =>
        _publishing = _publishing.ContinueWith(
            _ => _server.Publish(eventType, payload),
            TaskScheduler.Default).Unwrap().ContinueWith(
            task => Log.Warn($"the {eventType} event could not be sent: {task.Exception?.Message}"),
            TaskContinuationOptions.OnlyOnFaulted);

    /// <summary>AkuWM's own windows are not AkuWM's to arrange.</summary>
    private static bool IsOurs(WindowSnapshot window) =>
        window.ProcessId == Environment.ProcessId;

    public async ValueTask DisposeAsync()
    {
        _hooks.Dispose();
        await _server.DisposeAsync().ConfigureAwait(false);
        await _loop.DisposeAsync().ConfigureAwait(false);
        _taskbar.Dispose();
    }
}
