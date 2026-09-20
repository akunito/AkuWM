using System.Collections.Concurrent;
using AkuWM.Core.Logging;
using AkuWM.Core.Platform;

namespace AkuWM.Core.Wm;

/// <summary>
/// The one thread that is allowed to change the desk.
/// </summary>
/// <remarks>
/// <para>
/// Everything that wants something done -- a hook callback, a command off the
/// pipe, the bar, later the GUI -- posts it here and waits. Nothing else
/// touches the model. That is not a performance decision: a window manager
/// whose state can be read by one thread while another is halfway through a
/// workspace switch will eventually place a window using a tree that no longer
/// exists, and no amount of locking makes that easy to reason about.
/// </para>
/// <para>
/// <strong>One real thread, not a task.</strong> The first version used
/// <c>async</c> and a channel, which looks the same and is not: after every
/// <c>await</c> the work resumed on whichever thread-pool thread was free.
/// Win32 does not forgive that. A deferred window-position batch belongs to
/// the thread that opened it, and moving three windows at once failed --
/// silently, returning a null batch handle -- for exactly that reason, while
/// the same code from a spike on its own thread worked every time. The desk
/// still moved, because the batch falls back to one call per window, but every
/// workspace switch was visibly reflowing instead of changing at once.
/// </para>
/// <para>
/// A piece of work that throws is logged and refused; the loop survives it.
/// The alternative -- a manager that dies because one rule had a bad rectangle
/// -- leaves a desk full of hidden windows and nobody to give them back.
/// </para>
/// <para>
/// Every pass leaves a heartbeat, which is what the watchdog reads, and an
/// idle pass leaves one too: a quiet desk is not a stuck one.
/// </para>
/// </remarks>
public sealed class WmLoop : IAsyncDisposable
{
    private readonly BlockingCollection<WorkItem> _work = new(new ConcurrentQueue<WorkItem>());
    private readonly Action? _beat;
    private readonly Action<PlatformEvent>? _onEvent;
    private readonly Action? _onBatchEnd;
    private readonly TimeSpan _beatEvery;
    private readonly CancellationTokenSource _stopping = new();

    private Thread? _thread;

    /// <param name="onEvent">What to do with a platform event. Runs on this thread.</param>
    /// <param name="beat">Called once per pass, for the watchdog.</param>
    /// <param name="beatEvery">
    /// How often an idle loop wakes up to say it is still there. A desk nobody
    /// is touching produces no work for minutes at a time, and a loop that
    /// only spoke when it had something to do would be indistinguishable from
    /// a wedged one -- which is how the first version of this got a perfectly
    /// healthy daemon killed ten seconds after it started.
    /// </param>
    /// <param name="onBatchEnd">
    /// Called once after everything waiting has been dealt with, which is
    /// where the desk is redrawn. Windows delivers events in bursts -- moving
    /// one window produces dozens -- and answering each one with its own batch
    /// of window moves would be a desk that never stops twitching.
    /// </param>
    public WmLoop(
        Action<PlatformEvent>? onEvent = null,
        Action? beat = null,
        TimeSpan? beatEvery = null,
        Action? onBatchEnd = null)
    {
        _onEvent = onEvent;
        _beat = beat;
        _beatEvery = beatEvery ?? TimeSpan.FromSeconds(1);
        _onBatchEnd = onBatchEnd;
    }

    /// <summary>How many pieces of work have been through the loop.</summary>
    public long Handled { get; private set; }

    /// <summary>Work that threw and was refused.</summary>
    public long Refused { get; private set; }

    /// <summary>The thread everything above runs on, for the assertions that care.</summary>
    public int ThreadId => _thread?.ManagedThreadId ?? -1;

    public void Start()
    {
        _thread = new Thread(Run)
        {
            Name = "akuwm-wm",
            IsBackground = true,
        };

        _thread.Start();
    }

    /// <summary>
    /// Called from a hook callback. Returns immediately: the callback must be
    /// back in well under a millisecond or Windows removes the hook.
    /// </summary>
    public void Enqueue(PlatformEvent platformEvent) =>
        Offer(new WorkItem(platformEvent.ToString(), null, platformEvent));

    /// <summary>Runs something on the wm thread and waits for the answer.</summary>
    public Task<T> Post<T>(string what, Func<T> work)
    {
        var done = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);

        if (!Offer(new WorkItem(what, () => work(), null, done)))
        {
            return Task.FromException<T>(
                new InvalidOperationException("the window manager is not accepting work"));
        }

        return done.Task.ContinueWith(
            task => (T)task.GetAwaiter().GetResult()!,
            TaskContinuationOptions.ExecuteSynchronously);
    }

    /// <summary>Runs something on the wm thread and waits for it to finish.</summary>
    public Task Post(string what, Action work) =>
        Post<object?>(what, () =>
        {
            work();
            return null;
        });

    private bool Offer(WorkItem item)
    {
        try
        {
            _work.Add(item);
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            return false; // stopped
        }
    }

    private void Run()
    {
        Log.Info("the window-manager loop is running");

        while (!_stopping.IsCancellationRequested)
        {
            _beat?.Invoke();

            try
            {
                // Wait for something to do, giving up regularly so the
                // heartbeat keeps going on a quiet desk.
                if (_work.TryTake(out WorkItem first, (int)_beatEvery.TotalMilliseconds, _stopping.Token))
                {
                    Handle(first);

                    // And then everything else that is already waiting.
                    // Windows delivers events in bursts -- moving one window
                    // produces dozens -- and a batch of window moves per event
                    // is a desk that never stops twitching.
                    while (_work.TryTake(out WorkItem next))
                    {
                        Handle(next);
                    }
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException or ObjectDisposedException)
            {
                break;
            }

            try
            {
                _onBatchEnd?.Invoke();
            }
            catch (Exception ex)
            {
                Refused++;
                Log.Error("redrawing the desk failed; it is left as it was", ex);
            }
        }

        Log.Info($"the window-manager loop has stopped after {Handled} piece(s) of work");
    }

    private void Handle(WorkItem item)
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

            // The caller hears about its own failure. Nobody else is punished
            // for it.
            item.Done?.SetException(ex);
        }
    }

    public ValueTask DisposeAsync()
    {
        _stopping.Cancel();
        _work.CompleteAdding();

        if (_thread is not null && !_thread.Join(TimeSpan.FromSeconds(3)))
        {
            Log.Warn("the window-manager loop did not stop in time");
        }

        _stopping.Dispose();
        _work.Dispose();
        return ValueTask.CompletedTask;
    }

    private readonly record struct WorkItem(
        string What,
        Func<object?>? Work = null,
        PlatformEvent? Event = null,
        TaskCompletionSource<object?>? Done = null);
}
