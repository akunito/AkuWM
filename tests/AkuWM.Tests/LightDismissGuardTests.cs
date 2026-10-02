using AkuWM.Core.Desk;
using AkuWM.Core.Model;
using Xunit;

namespace AkuWM.Tests;

/// <summary>
/// The "Open with" dialog closed before the pointer reached it: Windows'
/// native focus-follows-mouse activated whatever the pointer crossed and the
/// light-dismiss popup went with its activation (2026-10-02). While such a
/// popup holds the foreground the native tracking is paused, and only then.
/// </summary>
public class LightDismissGuardTests
{
    private readonly HashSet<long> _popups = [];
    private bool _tracking = true;
    private readonly List<bool> _calls = [];

    private LightDismissGuard Guard() => new(h => _popups.Contains(h.Value), () => _tracking, on => { _tracking = on; _calls.Add(on); });

    [Fact]
    public void A_popup_in_front_pauses_the_tracking_and_the_next_window_resumes_it()
    {
        _popups.Add(9);
        LightDismissGuard g = Guard();
        g.ForegroundChanged(new WindowHandle(1), managed: true);
        Assert.Empty(_calls);
        g.ForegroundChanged(new WindowHandle(9), managed: false);
        Assert.False(_tracking);
        Assert.True(g.Paused);
        // The popup stays in front while the pointer moves: nothing more is sent.
        g.ForegroundChanged(new WindowHandle(9), managed: false);
        Assert.Equal([false], _calls);
        g.ForegroundChanged(new WindowHandle(1), managed: true);
        Assert.True(_tracking);
        Assert.False(g.Paused);
        Assert.Equal([false, true], _calls);
    }

    [Fact]
    public void The_popup_closing_resumes_the_tracking_even_before_a_foreground_change()
    {
        _popups.Add(9);
        LightDismissGuard g = Guard();
        g.ForegroundChanged(new WindowHandle(9), managed: false);
        g.WindowGone(new WindowHandle(5));
        Assert.False(_tracking);
        g.WindowGone(new WindowHandle(9));
        Assert.True(_tracking);
        Assert.Equal([false, true], _calls);
    }

    [Fact]
    public void A_desk_with_the_tracking_off_is_left_alone()
    {
        _popups.Add(9);
        _tracking = false;
        LightDismissGuard g = Guard();
        g.ForegroundChanged(new WindowHandle(9), managed: false);
        g.ForegroundChanged(new WindowHandle(1), managed: true);
        g.Resume();
        Assert.Empty(_calls);
        Assert.False(_tracking);
    }

    [Fact]
    public void A_managed_window_or_an_ordinary_unmanaged_one_never_pauses()
    {
        _popups.Add(9);
        LightDismissGuard g = Guard();
        g.ForegroundChanged(new WindowHandle(9), managed: true);   // managed wins over the shape
        g.ForegroundChanged(new WindowHandle(4), managed: false);  // unmanaged but not a popup
        g.ForegroundChanged(WindowHandle.None, managed: false);
        Assert.Empty(_calls);
    }

    [Fact]
    public void Resume_at_exit_puts_it_back_once()
    {
        _popups.Add(9);
        LightDismissGuard g = Guard();
        g.ForegroundChanged(new WindowHandle(9), managed: false);
        g.Resume();
        g.Resume();
        Assert.Equal([false, true], _calls);
    }
}
