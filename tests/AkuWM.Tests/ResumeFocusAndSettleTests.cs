using AkuWM.Core.Desk;
using AkuWM.Core.Model;
using Xunit;

namespace AkuWM.Tests;

/// <summary>
/// What the morning after a suspend showed (live desk 2026-09-23 07:40-07:44,
/// plan 10.30): the vertical monitor switched to an empty workspace took the
/// elevated console on the MAIN monitor as the focus, and the resume brought
/// the screens back one at a time over 17 s while the layout chased every
/// intermediate shape.
/// </summary>
public class ResumeFocusAndSettleTests
{
    private static WindowHandle W(long handle) => new(handle);

    [Fact]
    public void A_screen_switched_to_an_empty_workspace_does_not_take_the_focus_from_another_screen()
    {
        var f = new DeskFixture();
        f.Open(1);
        f.Turn();
        f.Foreground(1);
        f.Desk.FocusWorkspace("21");
        f.Open(2, monitor: new MonitorHandle(2), frame: new Rect(3900, 0, 800, 600));
        f.Turn();
        f.Foreground(2);
        f.Turn();
        f.Platform.Calls.Clear();

        f.Desk.WantFocus(f.Desk.FocusWorkspace("22"));
        Redraw switching = f.Turn();
        Assert.True(switching.Unfocus);
        Assert.True(f.IsHidden(2));

        // The foreground re-read after the redraw still names the hidden window.
        Assert.False(f.Desk.Focus(W(2)));
        Redraw after = f.Turn();

        Assert.Equal(WindowHandle.None, after.Focus);
        Assert.DoesNotContain(W(1), after.Raise);
        Assert.Equal(WindowHandle.None, f.Desk.Focused);
        Assert.DoesNotContain(f.Platform.Calls, c => c.StartsWith("focus 1", StringComparison.Ordinal));
    }

    [Fact]
    public void The_fallback_across_screens_still_exists_when_no_screen_is_being_looked_at()
    {
        var f = new DeskFixture();
        f.Open(1);
        f.Turn();
        f.Foreground(1);
        f.Turn();
        Assert.True(f.Desk.FocusSomethingVisible());
    }

    [Fact]
    public void While_a_screen_is_away_the_layout_waits_longer_than_for_a_screen_that_changed_shape()
    {
        var f = new DeskFixture();
        f.Open(1);
        f.Desk.FocusWorkspace("21");
        f.Open(2, monitor: new MonitorHandle(2), frame: new Rect(3900, 0, 800, 600));
        f.Turn();
        f.Turn();

        f.Platform.MonitorList.RemoveAll(m => m.Handle.Value == 2);
        f.Desk.SetMonitors(f.Platform.Monitors());
        f.Wait(Desk.MonitorSettleMs);
        Assert.Empty(f.Turn().Place);

        f.Wait(Desk.MonitorReturnMs - Desk.MonitorSettleMs);
        Assert.NotEmpty(f.Turn().Place);
    }

    [Fact]
    public void A_placeholder_screen_is_not_laid_out_on()
    {
        var f = new DeskFixture();
        f.Open(1);
        f.Turn();
        f.Turn();

        MonitorSnapshot main = f.Platform.MonitorList[0];
        f.Platform.MonitorList[0] = main with { HardwareId = "Default_Monitor", Bounds = new Rect(0, 0, 1920, 1080), WorkArea = new Rect(0, 42, 1920, 1038) };
        f.Desk.SetMonitors(f.Platform.Monitors());
        f.Wait(Desk.MonitorSettleMs);
        Assert.Empty(f.Turn().Place);

        // Still there after the long wait: then it is the screen there is.
        f.Wait(Desk.MonitorReturnMs - Desk.MonitorSettleMs);
        Assert.NotEmpty(f.Turn().Place);
    }
}
