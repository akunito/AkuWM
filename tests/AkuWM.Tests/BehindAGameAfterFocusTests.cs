using AkuWM.Core.Desk;
using AkuWM.Core.Model;
using Xunit;

namespace AkuWM.Tests;

/// <summary>
/// A window that took the focus while the game was minimised sat over it
/// after the restore: Windows had raised it by activating it, and the model,
/// which sends the insert-behind once per (window, game) pair, believed it was
/// still behind (tests/fullscreen 8-gamelike step 2, 0 % direct, 2026-09-23).
/// </summary>
public class BehindAGameAfterFocusTests
{
    private static WindowHandle W(long handle) => new(handle);

    [Fact]
    public void A_window_activated_while_the_game_was_away_goes_behind_it_again()
    {
        var f = new DeskFixture();
        f.Open(1, resizable: false, frame: new Rect(400, 400, 800, 600));
        f.Desk.SetSticky(W(1), true);
        f.Open(2, resizable: false, frame: new Rect(100, 100, 600, 400));
        f.Turn();
        f.Desk.SetFullscreen(W(2), true);
        Assert.Contains((W(1), W(2)), f.Turn().Behind);
        Assert.Empty(f.Turn().Behind); // once per pair

        // The game goes to the taskbar; the person clicks the sticky window
        // (Windows raises it); the game comes back.
        f.Wait(10_000); // past every placement guard: the person acts later
        f.Platform.SetMinimized(W(2), true);
        f.Move(2, new Rect(-32000, -32000, 237, 39));
        f.Turn();
        Assert.True(f.Foreground(1));
        f.Turn();
        f.Platform.SetMinimized(W(2), false);
        f.Move(2, new Rect(0, 0, 3840, 2160));
        Redraw back = f.Turn();

        Assert.Equal(WindowState.Fullscreen, f.Managed(2)!.State);
        Assert.Contains((W(1), W(2)), back.Behind);
    }
}
