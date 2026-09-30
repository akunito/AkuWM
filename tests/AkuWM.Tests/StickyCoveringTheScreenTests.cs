using AkuWM.Core.Config;
using AkuWM.Core.Desk;
using AkuWM.Core.Model;
using Xunit;
using static AkuWM.Tests.DeskFixture;

namespace AkuWM.Tests;

/// <summary>
/// Telegram's media viewer: sticky by the rule that makes Telegram sticky,
/// born borderless over the whole monitor. It must be given the whole
/// monitor, not the work area (2026-09-30 11:44: asked for 0,42 3840x2118
/// every 75 ms for two seconds, the picture jumping between two sizes).
/// </summary>
public class StickyCoveringTheScreenTests
{
    private static DeskFixture Fixture()
    {
        AkuWmConfig config = Configuration();
        config.Rules = [new RuleConfig { Name = "Telegram", Match = [new MatchCriteria { Process = "Telegram" }], Actions = ["float", "sticky"] }];
        return new DeskFixture(config);
    }

    [Fact]
    public void A_sticky_window_born_over_the_whole_monitor_keeps_the_whole_monitor()
    {
        var f = Fixture();
        f.Open(1, process: "Telegram", frame: new Rect(200, 200, 900, 700));
        f.Turn();
        Rect screen = f.Platform.MonitorList[0].Bounds;
        f.Open(2, process: "Telegram", frame: screen, title: "Media viewer", resizable: false);
        Redraw redraw = f.Turn();

        DeskWindow viewer = f.Managed(2)!;
        Assert.True(viewer.Sticky);
        Assert.Equal(screen, f.FrameOf(2));
        Assert.DoesNotContain(redraw.Place, p => p.Window == W(2) && p.Frame != screen);

        // Settled: nothing more is asked of it, turn after turn.
        f.Wait(Desk.SettleMs + 1);
        Assert.DoesNotContain(f.Turn().Place, p => p.Window == W(2));
        f.Wait(Desk.PlacementPatienceMs + 1);
        Assert.DoesNotContain(f.Turn().Place, p => p.Window == W(2));
        Assert.False(viewer.PlacementRefused);
    }

    [Fact]
    public void A_sticky_window_a_little_bigger_than_the_screen_is_still_the_screen()
    {
        var f = Fixture();
        Rect screen = f.Platform.MonitorList[0].Bounds;
        f.Open(2, process: "Telegram", frame: screen.Inflate(8), title: "Media viewer", resizable: false);
        f.Turn();
        Assert.Equal(screen, f.FrameOf(2));
    }

    [Fact]
    public void A_sticky_window_merely_taller_than_the_work_area_is_still_trimmed_to_it()
    {
        var f = Fixture();
        Rect area = f.Platform.MonitorList[0].WorkArea;
        f.Open(2, process: "Telegram", frame: new Rect(100, 0, 1200, area.Height + 300));
        f.Turn();
        Rect frame = f.FrameOf(2);
        Assert.Equal(area.Height, frame.Height);
        Assert.Equal(area.Top, frame.Y);
    }
}
