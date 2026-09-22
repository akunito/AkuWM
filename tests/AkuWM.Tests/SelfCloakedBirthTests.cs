using AkuWM.Core.Desk;
using AkuWM.Core.Model;
using Xunit;

namespace AkuWM.Tests;

/// <summary>
/// Zen (Firefox) shows a new window with DWM_CLOAKED_APP set for its first
/// ~200 ms of paint (Ctrl+N on the desk, 2026-09-22: visible and cloaked at
/// 661 ms, uncloaked at 863 ms). AkuWM saw it at exactly that instant, refused
/// it as self-cloaked, and never looked again: the window floated where Zen
/// put it, ignored toggle-floating, and was tiled only by the next restart.
/// </summary>
public class SelfCloakedBirthTests
{
    private static WindowHandle W(long handle) => new(handle);

    [Fact]
    public void A_window_born_cloaked_by_itself_is_adopted_the_moment_the_cloak_lifts()
    {
        var f = new DeskFixture();
        f.Open(1);
        f.Turn();

        f.Platform.WindowList.Add(FakePlatform.Window(2, "zen", className: "MozillaWindowClass", title: "Zen Browser", cloak: CloakKind.App));
        f.Sync();
        Assert.False(f.Managed(2)!.Managed);
        Assert.Equal(UnmanagedReason.SelfCloaked, f.Managed(2)!.Reason);
        Assert.DoesNotContain(f.Turn().Place, p => p.Window == W(2));

        // EVENT_OBJECT_UNCLOAKED: one window read by itself, as the daemon does.
        f.Platform.Uncloak(2);
        f.Desk.Observe(f.Platform.Window(W(2))!);
        Redraw redraw = f.Turn();

        Assert.True(f.Managed(2)!.Managed);
        Assert.Equal(WindowState.Tiling, f.Managed(2)!.State);
        Assert.Equal("11", f.Managed(2)!.Workspace);
        Assert.Contains(redraw.Place, p => p.Window == W(2));
        Assert.Contains(redraw.Place, p => p.Window == W(1));
        // And it answers the chord.
        Assert.True(f.Desk.SetFloating(W(2), true));
    }

    [Fact]
    public void The_same_through_a_full_sync()
    {
        var f = new DeskFixture();
        f.Platform.WindowList.Add(FakePlatform.Window(2, "zen", cloak: CloakKind.App));
        f.Sync();
        f.Turn();
        f.Platform.Uncloak(2);
        f.Sync();
        f.Turn();

        Assert.True(f.Managed(2)!.Managed);
        Assert.Equal(WindowState.Tiling, f.Managed(2)!.State);
    }

    [Fact]
    public void A_window_that_stays_cloaked_by_itself_is_left_alone()
    {
        var f = new DeskFixture();
        f.Platform.WindowList.Add(FakePlatform.Window(2, "tray-app", cloak: CloakKind.App));
        f.Sync();
        f.Turn();
        f.Desk.Observe(f.Platform.Window(W(2))!);
        f.Sync();
        Redraw redraw = f.Turn();

        Assert.False(f.Managed(2)!.Managed);
        Assert.Equal(UnmanagedReason.SelfCloaked, f.Managed(2)!.Reason);
        Assert.DoesNotContain(redraw.Place, p => p.Window == W(2));
    }

    [Fact]
    public void Cloaked_by_itself_AND_by_the_shell_waits_for_both()
    {
        var f = new DeskFixture();
        f.Platform.WindowList.Add(FakePlatform.Window(2, "zen", cloak: CloakKind.App | CloakKind.Shell));
        f.Sync();
        f.Turn();

        int at = f.Platform.WindowList.FindIndex(w => w.Handle.Value == 2);
        f.Platform.WindowList[at] = f.Platform.WindowList[at] with { Cloak = CloakKind.App };
        f.Desk.Observe(f.Platform.Window(W(2))!);
        Assert.False(f.Managed(2)!.Managed);

        f.Platform.Uncloak(2);
        f.Desk.Observe(f.Platform.Window(W(2))!);
        Assert.True(f.Managed(2)!.Managed);
    }
}
