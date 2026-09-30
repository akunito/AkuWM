using AkuWM.Core.Desk;
using AkuWM.Core.Model;
using Xunit;
using static AkuWM.Tests.DeskFixture;

namespace AkuWM.Tests;

/// <summary>
/// A window the shell has no view for (a notification toast) cannot be
/// hidden: the shell answers with an error. It was asked again on every
/// redraw -- 2199 warnings in one morning (2026-09-30). Asked once now.
/// </summary>
public class UncloakableWindowTests
{
    [Fact]
    public void A_window_whose_hide_errors_is_asked_once_and_stays_visible()
    {
        var f = new DeskFixture();
        f.Open(1);
        f.Turn();
        f.Platform.CloakErrors.Add(1);

        f.Desk.FocusWorkspace("12");
        Redraw first = f.Turn();
        Assert.Contains(W(1), first.Hide);
        Assert.True(f.Managed(1)!.CloakRefused);
        Assert.False(f.IsHidden(1));

        int asked = f.Platform.Calls.Count(c => c == "cloak 1 True");
        f.Wait(Desk.SettleMs + 1);
        Assert.DoesNotContain(W(1), f.Turn().Hide);
        f.Wait(Desk.SettleMs + 1);
        Assert.DoesNotContain(W(1), f.Turn().Hide);
        Assert.Equal(asked, f.Platform.Calls.Count(c => c == "cloak 1 True"));
        Assert.True(f.Managed(1)!.Managed);
    }

    [Fact]
    public void A_silent_refusal_is_still_retried()
    {
        var f = new DeskFixture();
        f.Open(1);
        f.Turn();
        f.Platform.RefusesToCloak.Add(1);

        f.Desk.FocusWorkspace("12");
        Assert.Contains(W(1), f.Turn().Hide);
        Assert.False(f.Managed(1)!.CloakRefused);
        f.Wait(Desk.SettleMs + 1);
        Assert.Contains(W(1), f.Turn().Hide);
    }
}
