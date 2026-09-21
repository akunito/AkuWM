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
}
