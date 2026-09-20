using AkuWM.Core.Logging;

namespace AkuWM.Core.State;

/// <summary>
/// Notices when the window-manager loop has stopped answering, and gives the
/// desk back.
/// </summary>
/// <remarks>
/// <para>
/// The dangerous failure is not a crash -- a crash runs the exit handlers, and
/// what those miss the next start recovers from the journals. The dangerous
/// one is a process that is still alive and no longer doing anything: the
/// windows it cloaked stay invisible, the windows it moved stay where it put
/// them, and nothing on the machine is going to notice on its own.
/// </para>
/// <para>
/// So the loop leaves a heartbeat, and a thread that shares nothing with it
/// watches the clock. If the beat stops for longer than a person would wait,
/// the desk is restored from the journals and the process ends. Ending is the
/// point: a manager that cannot manage should be absent, not present and
/// frozen, and the old stack is one reboot away by design.
/// </para>
/// <para>
/// A stall fires the rescue once. A second one cannot: the desk has already
/// been given back, and repeating it would fight whoever picked it up next.
/// </para>
/// </remarks>
public sealed class Watchdog : IDisposable
{
    private readonly TimeSpan _stallAfter;
    private readonly Action _onStall;
    private readonly Func<DateTimeOffset> _clock;
    private readonly CancellationTokenSource _stopping = new();

    private long _lastBeat;
    private int _fired;
    private Thread? _thread;

    /// <param name="stallAfter">How long the loop may be silent before it counts as stuck.</param>
    /// <param name="onStall">What to do about it. Must not touch the stuck loop.</param>
    /// <param name="clock">Injected by the tests; the real one is the wall clock.</param>
    public Watchdog(TimeSpan stallAfter, Action onStall, Func<DateTimeOffset>? clock = null)
    {
        _stallAfter = stallAfter;
        _onStall = onStall;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _lastBeat = _clock().ToUnixTimeMilliseconds();
    }

    /// <summary>True once the watchdog has decided the loop is gone.</summary>
    public bool Fired => Volatile.Read(ref _fired) != 0;

    /// <summary>Called from the window-manager loop, once per pass.</summary>
    public void Beat() => Interlocked.Exchange(ref _lastBeat, _clock().ToUnixTimeMilliseconds());

    /// <summary>How long since the last beat.</summary>
    public TimeSpan Silence =>
        TimeSpan.FromMilliseconds(_clock().ToUnixTimeMilliseconds() - Interlocked.Read(ref _lastBeat));

    /// <summary>
    /// One look at the clock. The thread calls it on an interval; the tests
    /// call it directly, which is why the sleeping is not in here.
    /// </summary>
    /// <returns>True when this call is the one that fired the rescue.</returns>
    public bool Check()
    {
        if (Silence < _stallAfter)
        {
            return false;
        }

        if (Interlocked.Exchange(ref _fired, 1) != 0)
        {
            return false;
        }

        Log.Error(
            $"the window-manager loop has not answered for {Silence.TotalSeconds:F0}s; "
            + "giving the desk back and stopping");

        try
        {
            _onStall();
        }
        catch (Exception ex)
        {
            Log.Error("the watchdog's rescue itself failed", ex);
        }

        return true;
    }

    /// <summary>Starts the thread that shares nothing with the loop it watches.</summary>
    public void Start(TimeSpan? every = null)
    {
        TimeSpan interval = every ?? TimeSpan.FromSeconds(1);

        _thread = new Thread(() =>
        {
            while (!_stopping.IsCancellationRequested)
            {
                if (Check())
                {
                    return;
                }

                _stopping.Token.WaitHandle.WaitOne(interval);
            }
        })
        {
            IsBackground = true,
            Name = "akuwm-watchdog",
        };

        _thread.Start();
    }

    public void Dispose()
    {
        _stopping.Cancel();
        _thread?.Join(TimeSpan.FromSeconds(2));
        _stopping.Dispose();
    }
}
