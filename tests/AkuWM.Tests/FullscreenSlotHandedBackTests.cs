using AkuWM.Core.Desk;
using AkuWM.Core.Model;
using Xunit;

namespace AkuWM.Tests;

/// <summary>
/// A second window covering the screen takes the fullscreen slot from the
/// game; when it closes, the game -- still covering the screen -- gets the
/// slot back, and the taskbar mark with it. Without this the game stayed
/// demoted under its own tiles (tests/fullscreen 13, 2026-09-23).
/// </summary>
public class FullscreenSlotHandedBackTests
{
    private static WindowHandle W(long handle) => new(handle);

    [Fact]
    public void The_game_gets_the_slot_back_when_the_window_that_took_it_closes()
    {
        var f = new DeskFixture();
        f.Open(1);
        f.Open(2, resizable: false, frame: new Rect(0, 0, 3840, 2160));
        f.Turn();
        Assert.Equal(WindowState.Fullscreen, f.Managed(2)!.State);
        Assert.Equal(W(2), f.Desk.Workspace("11")!.Fullscreen);

        f.Open(3, resizable: false, frame: new Rect(0, 0, 3840, 2160));
        f.Turn();
        Assert.Equal(W(3), f.Desk.Workspace("11")!.Fullscreen);
        Assert.Equal(WindowState.Tiling, f.Managed(2)!.State);
        Assert.Contains(W(2), f.Desk.Workspace("11")!.Windows);

        f.Close(3);
        Redraw redraw = f.Turn();

        Assert.Equal(W(2), f.Desk.Workspace("11")!.Fullscreen);
        Assert.Equal(WindowState.Fullscreen, f.Managed(2)!.State);
        Assert.Contains((W(2), true), redraw.TaskbarMark);
    }
}
