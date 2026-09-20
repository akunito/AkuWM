using AkuWM.Core.Model;
using AkuWM.Core.Platform;
using AkuWM.Core.Wm;
using Xunit;

namespace AkuWM.Tests;

/// <summary>
/// The single thread that owns the desk: it does things in the order they
/// arrived, it survives work that throws, and it says it is alive.
/// </summary>
public class WmLoopTests
{
    [Fact]
    public async Task Work_is_done_in_the_order_it_arrived()
    {
        var order = new List<int>();
        await using var loop = new WmLoop();
        loop.Start();

        Task[] posted = [.. Enumerable.Range(0, 50).Select(i => loop.Post($"work {i}", () => order.Add(i)))];
        await Task.WhenAll(posted);

        Assert.Equal(Enumerable.Range(0, 50), order);
    }

    [Fact]
    public async Task An_answer_comes_back_to_whoever_asked()
    {
        await using var loop = new WmLoop();
        loop.Start();

        Assert.Equal(42, await loop.Post("the answer", () => 42));
    }

    [Fact]
    public async Task Work_that_throws_is_refused_and_the_loop_lives()
    {
        await using var loop = new WmLoop();
        loop.Start();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => loop.Post<int>("a bad rule", () => throw new InvalidOperationException("bad rectangle")));

        // The whole point: one command with a bad rectangle must not leave a
        // desk full of cloaked windows and nobody to give them back.
        Assert.Equal(7, await loop.Post("the next one", () => 7));
        Assert.Equal(1, loop.Refused);
    }

    [Fact]
    public async Task Platform_events_reach_the_model_on_this_thread()
    {
        var seen = new List<PlatformEventKind>();
        await using var loop = new WmLoop(onEvent: e => seen.Add(e.Kind));
        loop.Start();

        loop.Enqueue(new PlatformEvent(PlatformEventKind.WindowCreated, new WindowHandle(1), 0));
        loop.Enqueue(new PlatformEvent(PlatformEventKind.ForegroundChanged, new WindowHandle(1), 0));
        await loop.Post("drain", () => { });

        Assert.Equal([PlatformEventKind.WindowCreated, PlatformEventKind.ForegroundChanged], seen);
    }

    [Fact]
    public async Task The_loop_says_it_is_alive_while_it_works()
    {
        int beats = 0;
        await using var loop = new WmLoop(beat: () => beats++);
        loop.Start();

        await loop.Post("something", () => { });

        Assert.True(beats > 0);
    }

    [Fact]
    public async Task An_idle_loop_still_says_it_is_alive()
    {
        int beats = 0;
        await using var loop = new WmLoop(beat: () => beats++, beatEvery: TimeSpan.FromMilliseconds(20));
        loop.Start();

        // No work at all, which is what a desk nobody is touching looks like.
        // The first version of this loop went silent here, and the watchdog
        // killed a healthy daemon ten seconds after it started.
        await Task.Delay(200);

        Assert.True(beats > 3, $"an idle loop beat {beats} times in 200 ms");
    }

    [Fact]
    public async Task A_loop_wedged_inside_one_piece_of_work_goes_quiet()
    {
        int beats = 0;
        var wedged = new ManualResetEventSlim(false);
        await using var loop = new WmLoop(beat: () => beats++, beatEvery: TimeSpan.FromMilliseconds(20));
        loop.Start();

        _ = loop.Post("a piece of work that never returns", () => wedged.Wait(TimeSpan.FromSeconds(5)));
        await Task.Delay(100);
        int duringTheStall = beats;
        await Task.Delay(200);

        // This is the silence the watchdog is listening for; the idle case
        // above must not look like it.
        Assert.Equal(duringTheStall, beats);
        wedged.Set();
    }

    [Fact]
    public async Task A_burst_of_events_is_answered_once()
    {
        int redraws = 0;
        await using var loop = new WmLoop(
            beatEvery: TimeSpan.FromSeconds(30), onBatchEnd: () => redraws++);
        loop.Start();

        // Moving one window on a real desk produces dozens of these. A batch
        // of window moves per event would be a desk that never stops
        // twitching.
        for (int i = 0; i < 40; i++)
        {
            loop.Enqueue(new PlatformEvent(PlatformEventKind.WindowMoved, new WindowHandle(1), 0));
        }

        await loop.Post("drain", () => { });
        await Task.Delay(50);

        Assert.InRange(redraws, 1, 3);
    }

    [Fact]
    public async Task A_redraw_that_throws_does_not_stop_the_loop()
    {
        int calls = 0;
        await using var loop = new WmLoop(
            beatEvery: TimeSpan.FromMilliseconds(20),
            onBatchEnd: () =>
            {
                if (++calls == 1)
                {
                    throw new InvalidOperationException("a bad rectangle");
                }
            });
        loop.Start();

        await Task.Delay(150);

        Assert.True(calls > 1, "the loop stopped after the first failure");
    }

    [Fact]
    public async Task A_stopped_loop_refuses_work_instead_of_swallowing_it()
    {
        var loop = new WmLoop();
        loop.Start();
        await loop.DisposeAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => loop.Post("too late", () => 1));
    }
}
