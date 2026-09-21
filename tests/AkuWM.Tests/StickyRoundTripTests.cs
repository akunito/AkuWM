using AkuWM.Core.Compat;
using AkuWM.Core.Desk;
using Xunit;
namespace AkuWM.Tests;
/// <summary>
/// Sticky, and back. The suite on the desk reported the window landing on the
/// wrong workspace after `unset-sticky`; the model does not, so the cause is
/// somewhere in the rest of that case -- kept as the regression test for the
/// part that IS decided here.
/// </summary>
public class StickyRoundTripTests
{
    [Fact]
    public void Unsticking_leaves_it_on_the_workspace_that_is_showing()
    {
        var f = new DeskFixture();
        var x = new GlazeExecutor(f.Desk, new FakeDeskPlatform());
        f.Open(1);
        f.Turn();
        f.Desk.Focus(DeskFixture.W(1));

        Assert.True(x.Command("set-sticky").Success);
        f.Turn();

        // Follow it to the next workspace and back, as the suite does.
        f.Desk.FocusWorkspace("13"); f.Turn();
        f.Desk.FocusWorkspace("11"); f.Turn();

        Assert.True(x.Command("unset-sticky").Success);
        f.Turn();

        string? where = f.Desk.Window(DeskFixture.W(1))!.Workspace;

        f.Desk.FocusWorkspace("13");
        f.Turn();

        Assert.Equal("11", where);
        Assert.Equal("11", f.Desk.Window(DeskFixture.W(1))!.Workspace);
    }

    [Fact]
    public void And_switching_away_afterwards_does_not_take_it_along()
    {
        // The suite's exact order: unstick, then switch workspace, THEN look.
        // Reported from the desk as landing on the workspace switched TO.
        var f = new DeskFixture();
        var x = new GlazeExecutor(f.Desk, new FakeDeskPlatform());
        f.Open(1);
        f.Turn();
        f.Desk.Focus(DeskFixture.W(1));

        Assert.True(x.Command("set-sticky").Success);
        f.Turn();
        Assert.True(x.Command("unset-sticky").Success);
        f.Turn();

        f.Desk.FocusWorkspace("13");
        f.Turn();
        f.Turn();

        Assert.Equal("11", f.Desk.Window(DeskFixture.W(1))!.Workspace);
        Assert.True(f.IsHidden(1), "it should have gone away with its workspace");
    }

    [Fact]
    public void Even_with_a_fullscreen_window_opened_and_closed_in_between()
    {
        var f = new DeskFixture();
        var x = new GlazeExecutor(f.Desk, new FakeDeskPlatform());
        f.Open(1);
        f.Turn();
        f.Desk.Focus(DeskFixture.W(1));
        Assert.True(x.Command("set-sticky").Success);
        f.Turn();

        // The game: fullscreen on this workspace, then gone. The suite does
        // this between sticking and unsticking, and it is the only step the
        // model was not being asked about.
        f.Open(2);
        f.Turn();
        f.Desk.SetFullscreen(DeskFixture.W(2), true);
        f.Turn();
        f.Close(2);
        f.Turn();

        Assert.True(x.Command("unset-sticky").Success);
        f.Turn();

        Assert.Equal("11", f.Desk.Window(DeskFixture.W(1))!.Workspace);
    }
}
