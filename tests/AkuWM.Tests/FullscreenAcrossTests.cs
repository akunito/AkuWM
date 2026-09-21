using System.Linq;
using AkuWM.Core.Desk;
using AkuWM.Core.Model;
using Xunit;

namespace AkuWM.Tests;

/// <summary>
/// A window that covers its screen, moved to another one.
/// </summary>
/// <remarks>
/// Diego asked for the state to travel with the window (2026-09-21): still
/// fullscreen, now on the other monitor. This is the command path -- a
/// workspace key, the raise-or-launch table. Dragging a fullscreen window is
/// deliberately not a thing AkuWM does; see the note on the drag.
/// </remarks>
public class FullscreenAcrossTests
{
    [Fact]
    public void A_fullscreen_window_moved_to_another_screen_covers_that_one()
    {
        var fixture = new DeskFixture();
        fixture.Open(1);
        fixture.Turn();
        fixture.Desk.SetFullscreen(DeskFixture.W(1), true);
        fixture.Turn();
        fixture.Turn();

        DeskMonitor main = fixture.Desk.Monitors.Single(m => m.Role == "main");
        Assert.Equal(main.Snapshot.Bounds, fixture.FrameOf(1));

        fixture.Desk.MoveToWorkspace(DeskFixture.W(1), "21");
        fixture.Turn();
        fixture.Turn();

        DeskMonitor second = fixture.Desk.Monitors.Single(m => m.Role == "second");
        Assert.Equal(WindowState.Fullscreen, fixture.Managed(1)!.State);
        Assert.Equal(second.Snapshot.Bounds, fixture.FrameOf(1));
    }

    [Fact]
    public void And_the_workspace_it_left_stops_believing_something_covers_it()
    {
        var fixture = new DeskFixture();
        fixture.Open(1);
        fixture.Open(2);
        fixture.Turn();
        fixture.Desk.SetFullscreen(DeskFixture.W(1), true);
        fixture.Turn();

        fixture.Desk.MoveToWorkspace(DeskFixture.W(1), "21");
        fixture.Turn();
        fixture.Turn();

        // Window 2 is alone on the main monitor and fills it. A workspace that
        // still thought it had a fullscreen window would keep 2 behind one
        // nobody can see.
        DeskMonitor main = fixture.Desk.Monitors.Single(m => m.Role == "main");
        Assert.Equal(main.TilingArea, fixture.FrameOf(2));
    }
}
