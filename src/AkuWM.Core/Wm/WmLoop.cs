using System.Threading.Channels;
using AkuWM.Core.Logging;
using AkuWM.Core.Platform;

namespace AkuWM.Core.Wm;

/// <summary>
/// The one thread that is allowed to change the desk.
/// </summary>
/// <remarks>
/// <para>
/// Everything that wants something done -- a hook callback, a command off the
/// pipe, the IPC, later the GUI -- posts it here and waits. Nothing else
/// touches the model. That is not a performance decision: a window manager
/// whose state can be read by one thread while another is halfway through a
/// workspace switch will eventually place a window using a tree that no
/// longer exists, and no amount of locking makes that easy to reason about.
/// One consumer, no locks in the model, and every decision made in the order
/// it arrived.
/// </para>
/// <para>
/// A piece of work that throws is logged and refused; the loop survives it.
/// The alternative -- a manager that dies because one rule had a bad
/// rectangle -- leaves a desk full of cloaked windows and nobody to give them
/// back.
/// </para>
/// <para>
/// Every pass leaves a heartbeat, which is what the watchdog reads. A loop
/// that stops answering is the one failure nothing else on the machine would
/// notice.
/// </para>
/// </remarks>
public sealed class WmLoop : IAsyncDisposable
{
    private readonly Channel<WorkItem> _work = Channel.CreateUnbounded<WorkItem>(
        new UnboundedChannelOptions { SingleReader = true });

    private readonly Action? _beat;
    private readonly Action<PlatformEvent>? _onEvent;
    private readonly TimeSpan _beatEvery;
    private readonly CancellationTokenSource _stopping = new();
    private Task? _loop;

    /// <param name="onEvent">What to do with a platform event. Runs on this thread.</param>
    /// <param name="beat">Called once per pass, for the watchdog.</param>
    /// <param name="beatEvery">
    /// How often an idle loop wakes up to say it is still there. A desk nobody
    /// is touching produces no work for minutes at a time, and a loop that
    /// only spoke when it had something to do would be indistinguishable from
    /// a wedged one -- which is how the first version of this got a perfectly
    /// healthy daemon killed ten seconds after it started.
    /// </param>
    public WmLoop(Action<PlatformEvent>? onEvent = null, Action? beat = null, TimeSpan? beatEvery = null)
    {
        _onEvent = onEvent;
        _beat = beat;
        _beatEvery = beatEvery ?? TimeSpan.FromSeconds(1);
    }

    /// <summary>How many pieces of work have been through the loop.</summary>
    public long Handled { get; private set; }

    /// <summary>Work that threw and was refused.</summary>
    public long Refused { get; private set; }

    public void Start()
    {
        _loop = Task.Factory.StartNew(
            Run,
            _stopping.Token,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default).Unwrap();
    }

    /// <summary>
    /// Called from a hook callback. Returns immediately: the callback must be
    /// back in well under a millisecond or Windows removes the hook.
    /// </summary>
    public void Enqueue(PlatformEvent platformEvent) =>
        _work.Writer.TryWrite(new WorkItem(platformEvent.ToString(), null, platformEvent));

    /// <summary>Runs something on the wm thread and waits for the answer.</summary>
    public Task<T> Post<T>(string what, Func<T> work)
    {
        var done = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var item = new WorkItem(what, () => work(), null, done);

        if (!_work.Writer.TryWrite(item))
        {
            return Task.FromException<T>(new InvalidOperationException("the window manager is not accepting work"));
        }

        return done.Task.ContinueWith(
            t => (T)t.GetAwaiter().GetResult()!,
            TaskContinuationOptions.ExecuteSynchronously);
    }

    /// <summary>Runs something on the wm thread and waits for it to finish.</summary>
    public Task Post(string what, Action work) =>
        Post<object?>(what, () =>
        {
            work();
            return null;
        });

    private async Task Run()
    {
        Log.Info("the window-manager loop is running");

        try
        {
            while (!_stopping.IsCancellationRequested)
            {
                // Idle counts as alive: a quiet desk is not a stuck one.
                _beat?.Invoke();

                while (_work.Reader.TryRead(out WorkItem item))
                {
                    _beat?.Invoke();
                    Handled++;

                    try
                    {
                        if (item.Event is { } platformEvent)
                        {
                            _onEvent?.Invoke(platformEvent);
                        }

                        item.Done?.SetResult(item.Work?.Invoke());
                    }
                    catch (Exception ex)
                    {
                        Refused++;
                        Log.Error($"'{item.What}' failed and was refused; the desk is unchanged", ex);

                        // The caller hears about its own failure. Nobody else
                        // is punished for it.
                        item.Done?.SetException(ex);
                    }
                }

                if (!await WaitForWork().ConfigureAwait(false))
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Stopping.
        }

        Log.Info($"the window-manager loop has stopped after {Handled} piece(s) of work");
    }

    /// <summary>
    /// Waits for something to do, and gives up regularly so the heartbeat
    /// keeps going.
    /// </summary>
    /// <returns>False when the loop is finished for good.</returns>
    private async Task<bool> WaitForWork()
    {
        using var wake = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
        wake.CancelAfter(_beatEvery);

        try
        {
            return await _work.Reader.WaitToReadAsync(wake.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!_stopping.IsCancellationRequested)
        {
            return true; // just the heartbeat coming round
        }
    }

    public async ValueTask DisposeAsync()
    {
        _work.Writer.TryComplete();
        _stopping.Cancel();

        if (_loop is not null)
        {
            try
            {
                await _loop.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
                Log.Warn("the window-manager loop did not stop in time");
            }
        }

        _stopping.Dispose();
    }

    private readonly record struct WorkItem(
        string What,
        Func<object?>? Work = null,
        PlatformEvent? Event = null,
        TaskCompletionSource<object?>? Done = null);
}
