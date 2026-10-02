using AkuWM.Core.Desk;
using AkuWM.Core.Model;
using Xunit;

namespace AkuWM.Tests;

/// <summary>
/// The UAC prompts nobody saw: an elevation asked with a window that is not
/// in front stays a minimised shield on the primary monitor's taskbar
/// (reproduced 2026-10-02, two of them pending while the person worked on the
/// other monitor). The daemon raises each one once, and not into a game.
/// </summary>
public class ElevationPromptRaiserTests
{
    private readonly HashSet<long> _prompts = [];
    private readonly List<long> _raisedNow = [];
    private bool _game;

    private ElevationPromptRaiser Raiser() => new(h => _prompts.Contains(h.Value), () => _game, h => _raisedNow.Add(h.Value));

    [Fact]
    public void A_parked_prompt_is_raised_once()
    {
        _prompts.Add(7);
        ElevationPromptRaiser r = Raiser();
        r.Seen(new WindowHandle(7));
        // The show and the minimise-start events both arrive for the same window.
        r.Seen(new WindowHandle(7));
        Assert.Equal([7], _raisedNow);
        Assert.True(r.Watching);
    }

    [Fact]
    public void Any_other_window_is_left_alone()
    {
        ElevationPromptRaiser r = Raiser();
        r.Seen(new WindowHandle(3));
        r.Seen(WindowHandle.None);
        Assert.Empty(_raisedNow);
        Assert.False(r.Watching);
    }

    [Fact]
    public void Two_prompts_are_both_raised()
    {
        _prompts.Add(7);
        _prompts.Add(8);
        ElevationPromptRaiser r = Raiser();
        r.Seen(new WindowHandle(7));
        r.Seen(new WindowHandle(8));
        Assert.Equal([7, 8], _raisedNow);
    }

    [Fact]
    public void A_game_in_front_holds_the_prompt_back_until_it_lets_go()
    {
        _prompts.Add(7);
        _game = true;
        ElevationPromptRaiser r = Raiser();
        r.Seen(new WindowHandle(7));
        r.ForegroundChanged();
        Assert.Empty(_raisedNow);
        Assert.Equal(1, r.Deferred);

        _game = false;
        r.ForegroundChanged();
        Assert.Equal([7], _raisedNow);
        Assert.Equal(0, r.Deferred);
        // And only once: the next foreground change has nothing left to do.
        r.ForegroundChanged();
        Assert.Equal([7], _raisedNow);
    }

    [Fact]
    public void A_held_prompt_that_was_answered_meanwhile_is_not_raised()
    {
        _prompts.Add(7);
        _game = true;
        ElevationPromptRaiser r = Raiser();
        r.Seen(new WindowHandle(7));
        // The asking process died or the person clicked the shield: no prompt any more.
        _prompts.Remove(7);
        _game = false;
        r.ForegroundChanged();
        Assert.Empty(_raisedNow);
    }

    [Fact]
    public void A_recycled_handle_is_a_new_prompt()
    {
        _prompts.Add(7);
        ElevationPromptRaiser r = Raiser();
        r.Seen(new WindowHandle(7));
        r.WindowGone(new WindowHandle(7));
        Assert.False(r.Watching);
        r.Seen(new WindowHandle(7));
        Assert.Equal([7, 7], _raisedNow);
    }
}
