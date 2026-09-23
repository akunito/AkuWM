using AkuWM.Core.Logging;
using AkuWM.Core.Model;
using AkuWM.Core.Platform;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Accessibility;
using Windows.Win32.UI.WindowsAndMessaging;

namespace AkuWM.Platform;

/// <summary>
/// The platform thread: the hooks, and the message loop that feeds them.
/// </summary>
/// <remarks>
/// <para>
/// Everything Windows has to tell AkuWM arrives here, on one thread of its
/// own. Two rules make that thread safe. It must have a message loop, because
/// <c>SetWinEventHook</c> and the low-level hooks deliver through the message
/// queue of the thread that installed them. And a callback must return almost
/// immediately: Windows removes a low-level hook that takes longer than
/// <c>LowLevelHooksTimeout</c> (300 ms by default) without saying so, and the
/// keyboard goes dead until the hook is reinstalled. So the callbacks here
/// translate and enqueue, and nothing else.
/// </para>
/// <para>
/// In M1 only the window events are installed; the keyboard and mouse hooks
/// arrive with the input layer at M3, and the spikes prove them first.
/// </para>
/// </remarks>
public sealed class Win32Hooks : IPlatformEvents, IDisposable
{
    // The accessibility events AkuWM listens to. Named here rather than looked
    // up so the set is one readable list.
    private const uint EventSystemForeground = 0x0003;
    private const uint EventSystemMinimizeStart = 0x0016;
    private const uint EventSystemMinimizeEnd = 0x0017;
    private const uint EventObjectCreate = 0x8000;
    private const uint EventObjectDestroy = 0x8001;
    private const uint EventObjectShow = 0x8002;
    private const uint EventObjectHide = 0x8003;
    private const uint EventObjectReorder = 0x8004;
    private const uint EventObjectLocationChange = 0x800B;
    private const uint EventObjectNameChange = 0x800C;
    private const uint EventObjectCloaked = 0x8017;
    private const uint EventObjectUncloaked = 0x8018;

    private const int ObjectIdWindow = 0;
    private const int ChildIdSelf = 0;

    /// <summary>The callback runs in AkuWM's own process, not injected into the target's.</summary>
    private const uint OutOfContext = 0x0000;

    /// <summary>AkuWM's own windows never produce an event for AkuWM.</summary>
    private const uint SkipOwnProcess = 0x0002;

    private const uint QuitMessage = 0x0012;

    private readonly List<UnhookWinEventSafeHandle> _hooks = [];
    private readonly ManualResetEventSlim _running = new(false);
    private WINEVENTPROC? _callback;
    private Thread? _thread;
    private uint _threadId;
    private Win32MessageWindow? _messages;

    public event Action<PlatformEvent>? Event;

    /// <summary>Starts the platform thread and blocks until its hooks are in.</summary>
    public void Start()
    {
        if (_thread is not null)
        {
            return;
        }

        _thread = new Thread(Run)
        {
            Name = "platform",
            IsBackground = true,
        };

        // The COM objects the shell hands out are apartment-threaded, and this
        // is the thread that will own them.
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();

        if (!_running.Wait(TimeSpan.FromSeconds(5)))
        {
            Log.Error("the platform thread did not come up within five seconds");
        }
    }

    public void Stop()
    {
        if (_thread is null)
        {
            return;
        }

        if (_threadId != 0)
        {
            PInvoke.PostThreadMessage(_threadId, QuitMessage, default, default);
        }

        _thread.Join(TimeSpan.FromSeconds(2));
        _thread = null;
    }

    private void Run()
    {
        _threadId = PInvoke.GetCurrentThreadId();
        _callback = OnWinEvent;

        foreach ((uint from, uint to) in Ranges())
        {
            UnhookWinEventSafeHandle hook = PInvoke.SetWinEventHook(
                from, to, null, _callback, 0, 0,
                OutOfContext | SkipOwnProcess);

            if (hook.IsInvalid)
            {
                Log.Error($"could not hook the window events {from:x}-{to:x}");
            }
            else
            {
                _hooks.Add(hook);
            }
        }

        // The screens only talk to a window, so the platform thread owns one.
        _messages = new Win32MessageWindow();
        _messages.Event += e => Event?.Invoke(e);
        _messages.Create();

        Log.Info($"platform thread up, {_hooks.Count} window-event hooks installed");
        _running.Set();

        // The loop is what makes the hooks fire: out-of-context callbacks are
        // delivered to this thread's queue.
        Win32MessageLoop.Pump();

        _messages?.Dispose();
        _messages = null;

        foreach (UnhookWinEventSafeHandle hook in _hooks)
        {
            hook.Dispose();
        }

        _hooks.Clear();
        Log.Info("platform thread stopped");
    }

    /// <summary>
    /// Contiguous ranges, because one hook can cover several events and each
    /// hook costs a cross-process notification.
    /// </summary>
    private static IEnumerable<(uint From, uint To)> Ranges() =>
    [
        (EventSystemForeground, EventSystemForeground),
        (EventSystemMinimizeStart, EventSystemMinimizeEnd),
        (EventObjectCreate, EventObjectReorder),
        (EventObjectLocationChange, EventObjectNameChange),
        (EventObjectCloaked, EventObjectUncloaked),
    ];

    private void OnWinEvent(
        HWINEVENTHOOK hook, uint eventId, HWND window, int objectId, int childId, uint thread, uint time)
    {
        // Only the window itself; the controls inside it are not AkuWM's
        // business and there are thousands of them.
        if (objectId != ObjectIdWindow || childId != ChildIdSelf || window.IsNull)
        {
            return;
        }

        PlatformEventKind? kind = eventId switch
        {
            EventSystemForeground => PlatformEventKind.ForegroundChanged,
            EventSystemMinimizeStart => PlatformEventKind.WindowMinimizeStart,
            EventSystemMinimizeEnd => PlatformEventKind.WindowMinimizeEnd,
            EventObjectCreate => PlatformEventKind.WindowCreated,
            EventObjectDestroy => PlatformEventKind.WindowDestroyed,
            EventObjectShow => PlatformEventKind.WindowShown,
            EventObjectHide => PlatformEventKind.WindowHidden,
            EventObjectReorder => PlatformEventKind.WindowsReordered,
            EventObjectLocationChange => PlatformEventKind.WindowMoved,
            EventObjectNameChange => PlatformEventKind.WindowTitleChanged,
            EventObjectCloaked => PlatformEventKind.WindowCloaked,
            EventObjectUncloaked => PlatformEventKind.WindowUncloaked,
            _ => null,
        };

        if (kind is null)
        {
            return;
        }

        // Translate and hand over. Whatever is listening decides what it means;
        // doing any of that here would be doing it inside somebody else's
        // notification.
        Event?.Invoke(new PlatformEvent(kind.Value, new WindowHandle(window.Value), Environment.TickCount64));
    }

    public void Dispose()
    {
        Stop();
        _running.Dispose();
    }
}
