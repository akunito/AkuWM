using AkuWM.Core.Config;
using AkuWM.Core.Desk;
using AkuWM.Core.Model;
using Xunit;

namespace AkuWM.Tests;

/// <summary>
/// The WSLg sudo askpass (zenity through msrdc) is born 370x317 at the corner
/// of the portrait monitor. AkuWM tiled it to the whole screen -- "place
/// 0x600cec msrdc -> 3840,-373 1440x2525" -- and Diego never saw the password
/// box; the deploy waited on sudo for five minutes, three times (2026-10-02).
/// The <c>center</c> action floats a window at its own size in the middle of
/// the monitor the person is on.
/// </summary>
public class CenterRuleTests
{
    private static DeskFixture Desk()
    {
        AkuWmConfig config = DeskFixture.Configuration();
        config.General!.OpenUnderPointer = true; // the desk's setting; the fixture's default is off
        config.Rules =
        [
            new RuleConfig
            {
                Name = "wslg",
                Match = [new MatchCriteria { Process = "msrdc" }],
                Actions = ["center"],
            },
        ];
        var f = new DeskFixture(config);
        f.Open(1);
        f.Turn();
        return f;
    }

    [Fact]
    public void A_dialog_born_on_the_other_monitor_is_centred_where_the_pointer_is_at_its_own_size()
    {
        DeskFixture f = Desk();
        Rect main = f.Desk.Monitors[0].TilingArea;
        f.Platform.Cursor = (main.X + 200, main.Y + 200);
        Rect second = f.Desk.Monitors[1].TilingArea;

        f.Open(2, process: "msrdc", monitor: f.Desk.Monitors[1].Handle, frame: new Rect(second.X, second.Y + 100, 370, 317), className: "RAIL_WINDOW", title: "sudo: Authentication Required (NixOS)");
        Redraw turn = f.Turn();

        DeskWindow w = f.Managed(2)!;
        Assert.Equal(WindowState.Floating, w.State);
        Assert.Equal(f.Desk.Monitors[0].Displayed!.Name, w.Workspace);
        var want = new Rect(main.X + ((main.Width - 370) / 2), main.Y + ((main.Height - 317) / 2), 370, 317);
        Assert.Equal(want, w.FloatingRect);
        Assert.Equal(want, f.FrameOf(2));
        // Never a tile: no placement of it at any other rectangle.
        Assert.DoesNotContain(turn.Place, p => p.Window == new WindowHandle(2) && p.Frame != want);
        // The tiled window keeps the whole workspace.
        Assert.Equal(main, f.FrameOf(1));
    }

    [Fact]
    public void With_the_pointer_on_the_other_monitor_it_is_centred_there()
    {
        DeskFixture f = Desk();
        Rect second = f.Desk.Monitors[1].TilingArea;
        f.Platform.Cursor = (second.X + 100, second.Y + 100);

        f.Open(2, process: "msrdc", monitor: f.Desk.Monitors[0].Handle, frame: new Rect(50, 80, 400, 300), className: "RAIL_WINDOW");
        f.Turn();

        Assert.Equal(new Rect(second.X + ((second.Width - 400) / 2), second.Y + ((second.Height - 300) / 2), 400, 300), f.Managed(2)!.FloatingRect);
        Assert.Equal(f.Desk.Monitors[1].Displayed!.Name, f.Managed(2)!.Workspace);
    }

    [Fact]
    public void A_window_larger_than_the_screen_is_cut_to_it()
    {
        DeskFixture f = Desk();
        Rect main = f.Desk.Monitors[0].TilingArea;
        f.Platform.Cursor = (main.X + 200, main.Y + 200);

        f.Open(2, process: "msrdc", frame: new Rect(0, 0, main.Width + 500, main.Height + 500), className: "RAIL_WINDOW");
        f.Turn();

        Assert.Equal(main, f.Managed(2)!.FloatingRect);
    }

    [Fact]
    public void The_validator_knows_center_and_refuses_it_with_tile()
    {
        AkuWmConfig config = DeskFixture.Configuration();
        config.Rules = [new RuleConfig { Id = "r-a", Match = [new MatchCriteria { Process = "msrdc" }], Actions = ["center"] }];
        Assert.True(ConfigValidator.Validate(config).Ok);
        config.Rules = [new RuleConfig { Id = "r-b", Match = [new MatchCriteria { Process = "msrdc" }], Actions = ["center", "tile"] }];
        Assert.False(ConfigValidator.Validate(config).Ok);
    }
}
