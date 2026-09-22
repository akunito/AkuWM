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
    private readonly GlazeEvents _events = new();
    private readonly ConfigPaths? _paths;

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
        int compatPort = GlazeProtocol.Port,
        ConfigPaths? paths = null,
        PlacementJournal? placements = null)
    {
        _paths = paths;
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

        // The anti-cheat policy's one hook into the platform: no attach, no
        // injection while the foreground is a window covering its screen.
        // Read on the wm thread, which is where the applier calls Focus from.
        // It was declared and never assigned, so the F24 route ran regardless.
        Win32Focus.GameInFront = () =>
            _desk.Window(_platform.Foreground()) is { } front
            && (_desk.LooksLikeAGame(front) || front.Marked || front.Snapshot.IsElevated);

        // A window that has closed is not one AkuWM has to put back -- and
        // not one to keep a cloak record for: Windows reuses the handle, and
        // the next start would uncloak whatever holds it now if the process
        // name happens to match.
        _desk.Forgotten += journal.Forget;
        _desk.Forgotten += ledger.Forget;

        if (placements is not null)
        {
            _desk.RemembersPlacementsWith(placements);
            _desk.Forgotten += placements.Forget;
        }
        _desk.ChecksHandlesWith(h => Win32Windows.IsWindow(h));
        _desk.ReadsTheCursorWith(() => _platform.CursorPosition());

        _loop = new WmLoop(OnEvent, watchdog.Beat, onBatchEnd: Redraw);

        _executor = new GlazeExecutor(_desk, new Win32DeskPlatform(), Reload);

        // Everything the bar and the scripts say arrives here and is answered
        // on the wm thread, so a query never sees a half-applied workspace
        // switch.
        _server = new GlazeIpcServer(compatPort, request => Ask(request).GetAwaiter().GetResult());
        // The verb and the size, never the frame: the bar asks for every
        // window on every event, and logging each reply in full rolled the
        // 4 MB log every fifteen minutes on this desk (6.7 MB of replies in
        // twenty, 2026-09-22), taking the trace of anything that mattered
        // with it. `akuwm daemon --capture <file>` is where whole frames go.
        _server.Traffic += (outbound, text) => Log.Debug(() => outbound
            ? $"compat > {text.Length} chars"
            : $"compat < {(text.Length > 80 ? text[..80] : text)}");
    }

    /// <summary>Whether the bar and the scripts can reach AkuWM.</summary>
    public GlazeIpcServer Compat => _server;

    /// <summary>Raised when a command asked the window manager to stop.</summary>
    public event Action? ExitRequested;

    /// <summary>Raised once when Windows says the session is ending.</summary>
    public event Action? SessionEnding;

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

        // A command is the only one with anything to do afterwards: the desk
        // has changed and the loop has to redraw it.
        if (verb != "command")
        {
            return _loop.Post(request, () => _executor.Ask(request));
        }

        return _loop.Post(request, () =>
        {
            ExecResult result = _executor.Command(rest);
            _dirty = true;

            if (_executor.ExitRequested)
            {
                ExitRequested?.Invoke();
            }

            // Before the reply goes back, not after the batch. A script that
            // resizes and then measures -- which is what tests/wm does, and
            // what any act-then-check gesture does -- would otherwise read the
            // rectangles the window had before the command it just made.
            // Same thread, and the batch-end pass finds nothing left to do.
            Redraw();

            return result;
        });
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
            IReadOnlyList<MonitorSnapshot> screens = _platform.Monitors();
            _desk.SetMonitors(screens);
            _screens.Prime(screens);
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
        if (platformEvent.Kind == PlatformEventKind.SessionEnding)
        {
            if (!_sessionEnding)
            {
                _sessionEnding = true;
                SessionEnding?.Invoke();
            }

            return;
        }

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
                    Log.Debug(() => $"refused the focus for {platformEvent.Handle} (hidden, or a window moved under a still pointer)");
                }
                else
                {
                    ReassertDecorationLater();
                }

                _dirty = true;
                return;

            case EventResponse.TheScreensChanged:
                IReadOnlyList<MonitorSnapshot> now = _platform.Monitors();
                _desk.SetMonitors(now);
                _screens.Prime(now);
                Log.Info($"the screens changed (notified); {Describe(now)}");
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
            // screen that turned into an application window). The same three
            // cheap calls that gate WindowCreated gate this: a tooltip, a
            // menu, an IME candidate window and a taskbar thumbnail all move
            // and rename at mouse rate, and each one cost a hundred-window
            // enumeration.
            if (Win32Windows.CouldBeManaged(handle))
            {
                _resync = true;
            }

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

    /// <summary>
    /// The screens, checked from time to time in case the notification never
    /// came. See <see cref="ScreenWatch"/> -- it was dead for a while and
    /// nothing said so.
    /// </summary>
    private readonly ScreenWatch _screens = new();

    // Work area is the bar-aware rectangle: a taskbar or app bar that moved,
    // grew or hid shows up here as the only difference (measured 2026-09-22:
    // an 80 px app bar on the left made the main work area 80,42 3760x2118
    // within 0.7 s, through the broadcast).
    private static string Describe(IReadOnlyList<MonitorSnapshot> screens)
    {
        var text = new System.Text.StringBuilder(screens.Count * 48);
        text.Append(screens.Count).Append(" now:");
        for (int i = 0; i < screens.Count; i++)
        {
            MonitorSnapshot s = screens[i];
            text.Append(' ').Append(s.HardwareId).Append(" work ").Append(s.WorkArea).Append(" of ").Append(s.Bounds);
        }

        return text.ToString();
    }

    /// <summary>
    /// Sends the focused window's decoration once more, a beat after the
    /// focus landed, for the applications that paint over it on activation.
    /// </summary>
    /// <remarks>
    /// One timer, re-armed on every focus change, so a burst of focus events
    /// costs one re-send. The work runs on the wm thread like everything that
    /// touches the model.
    /// </remarks>
    private void ReassertDecorationLater()
    {
        int after = _desk.Config.Effects?.ReassertMs ?? 0;
        if (after <= 0)
        {
            return;
        }

        // Three times, at t, 4t and 10t: Windows Terminal and VS Code paint
        // over the border more than once while they finish activating, and
        // one re-send at 300 ms lost to the second coat (live, 15:15).
        _reassertsLeft = 3;
        _reassertNext = after;
        _reassert ??= new Timer(
            _ => _loop.Post("reassert decoration", () =>
            {
                _desk.Redecorate(_desk.Focused);
                _dirty = true;
                Redraw();

                if (--_reassertsLeft > 0)
                {
                    _reassertNext = _reassertsLeft == 2 ? after * 4 - after : after * 10 - after * 4;
                    _reassert!.Change(_reassertNext, Timeout.Infinite);
                }
            }),
            null,
            Timeout.Infinite,
            Timeout.Infinite);
        _reassert.Change(after, Timeout.Infinite);
    }

    private int _reassertsLeft;
    private int _reassertNext;

    private Timer? _reassert;
    private Timer? _settle;
    private bool _sessionEnding;

    /// <summary>Once per burst: look if anything appeared, decide, apply.</summary>
    private void Redraw()
    {
        // Before the early return: an idle desk is exactly when a monitor gets
        // plugged in, and an idle pass is the only thing running then.
        if (_screens.Changed(_platform.Monitors))
        {
            Log.Info($"the screens changed without a notification; {Describe(_screens.Last)}");
            _desk.SetMonitors(_screens.Last);
            _resync = true;
            _dirty = true;
        }

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
                _desk.Applied(redraw, Last.Refused, Last.Unmarked, Last.Undecorated, Last.FocusRefused, Last.Unbanded);

                // The platform proves, once, that a window it hides can be
                // brought back. If it cannot, the model stops asking.
                _desk.CanHide = _applier.CanHide;
            }

            // A floating window still in the person's hand is left alone
            // until it rests; the desk asks to be looked at again then.
            // Outside the block above: when the ONLY thing to do was the
            // deferred placement, the redraw was "nothing" and this never
            // ran -- a fetched terminal came back rescaled and stayed so.
            if (_desk.Unsettled)
            {
                _settle ??= new Timer(
                    _ => _loop.Post("a window has come to rest", () =>
                    {
                        _dirty = true;
                        Redraw();
                    }),
                    null,
                    Timeout.Infinite,
                    Timeout.Infinite);
                _settle.Change(Desk.SettleMs + 20, Timeout.Infinite);
            }
        }

        // Always, and last: a focus change moves no window, and the bar still
        // has to hear about it. Publishing only after a redraw that did
        // something meant clicking between two windows told the bar nothing,
        // and a watching run told it nothing at all.
        Publish();
    }

    /// <summary>
    /// Reads the configuration files again and hands them to the desk.
    /// </summary>
    /// <remarks>
    /// On the wm thread, because the executor is: nothing else may be looking
    /// at the model while its workspaces are being reconciled. A file that
    /// does not parse, or does not validate, leaves the running configuration
    /// exactly where it was -- the alternative is a desk in a state neither
    /// file describes, from a person who was only editing a colour.
    /// </remarks>
    private ExecResult Reload()
    {
        if (_paths is null)
        {
            return ExecResult.Fail("this window manager was started without a configuration path");
        }

        LoadedConfig loaded;
        try
        {
            loaded = ConfigStore.Load(_paths);
        }
        catch (ConfigException ex)
        {
            Log.Warn($"the configuration was not reloaded: {ex.Message}");
            return ExecResult.Fail(ex.Message);
        }

        if (!loaded.Validation.Ok)
        {
            string why = string.Join("; ", loaded.Validation.Errors.Select(e => e.ToString()));
            Log.Warn($"the configuration was not reloaded: {why}");
            return ExecResult.Fail(why);
        }

        foreach (ValidationIssue issue in loaded.Validation.Warnings)
        {
            Log.Warn(issue.ToString());
        }

        Desk.ReloadResult result = _desk.Reload(loaded.Effective);
        _dirty = true;
        Log.Info($"configuration reloaded: {result}");

        return ExecResult.Ok(data: new JsonObject
        {
            ["workspacesAdded"] = result.Added,
            ["workspacesRemoved"] = result.Removed,
            ["windowsRehomed"] = result.Rehomed,

            // Said out loud rather than left to be discovered: rules decide
            // when a window is adopted.
            ["rules"] = "applied to windows opened from now on",
        });
    }

    /// <summary>Tells the bar what changed. The diff itself lives in Core, where it is tested.</summary>
    private void Publish() => _events.Since(_desk, _server.Connections > 0, Fire);

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
        _reassert?.Dispose();
        _settle?.Dispose();
        _hooks.Dispose();
        await _server.DisposeAsync().ConfigureAwait(false);
        await _loop.DisposeAsync().ConfigureAwait(false);
        _taskbar.Dispose();
    }
}
