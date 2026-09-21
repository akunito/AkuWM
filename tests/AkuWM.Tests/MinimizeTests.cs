using AkuWM.Core.Desk;
using AkuWM.Core.Model;
using Xunit;

namespace AkuWM.Tests;

/// <summary>
/// A minimised window stays minimised. tests/wm reported the opposite twice:
/// an app-toggle that minimised the focused window and found it still on
/// screen, and one that asked for a hidden window back and got nothing.
/// </summary>
public class MinimizeTests
{
    [Fact]
    public void A_tiled_window_that_is_minimised_stays_minimised()
    {
        var fixture = new DeskFixture();
        fixture.Open(1);
        fixture.Open(2);
        fixture.Turn();

        fixture.Platform.SetMinimized(DeskFixture.W(1), true);
        fixture.Sync();
        fixture.Turn();
        fixture.Turn();

        Assert.Equal(WindowState.Minimized, fixture.Desk.Window(DeskFixture.W(1))!.State);
        Assert.True(fixture.Platform.Window(DeskFixture.W(1))!.IsMinimized);
    }

    [Fact]
    public void A_floating_window_that_is_minimised_stays_minimised()
    {
        var fixture = new DeskFixture();
        fixture.Open(1);
        fixture.Turn();
        fixture.Desk.SetFloating(DeskFixture.W(1), true);
        fixture.Turn();

        // Its place in the floating band is kept on purpose -- that is where
        // it goes back to -- so the redraw still walks it, and placing a
        // minimised window is what un-minimises it.
        fixture.Platform.SetMinimized(DeskFixture.W(1), true);
        fixture.Sync();
        fixture.Turn();
        fixture.Turn();

        Assert.True(fixture.Platform.Window(DeskFixture.W(1))!.IsMinimized);
    }

    [Fact]
    public void And_nothing_is_asked_of_it_while_it_is_down()
    {
        var fixture = new DeskFixture();
        fixture.Open(1);
        fixture.Turn();
        fixture.Desk.SetFloating(DeskFixture.W(1), true);
        fixture.Turn();

        fixture.Platform.SetMinimized(DeskFixture.W(1), true);
        fixture.Sync();
        fixture.Turn();

        Redraw redraw = fixture.Desk.Compute();
        Assert.DoesNotContain(redraw.Place, p => p.Window == DeskFixture.W(1));
        Assert.DoesNotContain(DeskFixture.W(1), redraw.Show);
    }
}
