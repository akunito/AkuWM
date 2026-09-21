using AkuWM.Core.Desk;
using AkuWM.Core.Model;
using Xunit;

namespace AkuWM.Tests;

/// <summary>
/// Dragging a window from one monitor to the other.
/// </summary>
/// <remarks>
/// Reported from the desk 2026-09-21: "no puedo pasar una ventana de un
/// monitor a otro". The gesture released the window on the main monitor and it
/// snapped straight back to the vertical one, within a frame or two.
///
/// Nothing was pulling it: a floating window keeps the rectangle the person
/// gave it. Its WORKSPACE stayed on the monitor it came from, and a placement
/// is clamped into the work area of the workspace's monitor -- so the
/// rectangle was dragged back to the left edge of the screen it had just left.
/// The trace shows exactly that, 3082 asked for and 3832 arrived at, which is
/// that monitor's left edge less the border.
///
/// What every tiling manager does instead, and what this asserts: the window
/// joins the workspace displayed on the monitor it was dropped on.
/// </remarks>
public class AcrossMonitorsTests
{
    private static readonly Rect OnTheMain = new(400, 300, 900, 700);
    private static readonly Rect OnTheSecond = new(4000, 200, 900, 700);

    [Fact]
    public void A_floating_window_dropped_on_the_other_monitor_joins_its_workspace()
    {
        var fixture = new DeskFixture();
        fixture.Open(1);
        fixture.Desk.SetFloating(DeskFixture.W(1), true);
        fixture.Turn();

        fixture.Move(1, OnTheSecond);
        fixture.Turn();

        DeskWindow window = fixture.Desk.Window(DeskFixture.W(1))!;
        Assert.Equal("21", window.Workspace);
    }

    [Fact]
    public void And_stays_where_it_was_dropped()
    {
        var fixture = new DeskFixture();
        fixture.Open(1);
        fixture.Desk.SetFloating(DeskFixture.W(1), true);
        fixture.Turn();

        fixture.Move(1, OnTheSecond);

        // Twice: the first turn is where the old model put it back, and a
        // person dragging a window sees the second one.
        fixture.Turn();
        fixture.Turn();

        Assert.Equal(OnTheSecond, fixture.Platform.Window(DeskFixture.W(1))!.FrameBounds);
    }

    [Fact]
    public void And_the_same_way_back()
    {
        var fixture = new DeskFixture();
        fixture.Open(1, frame: OnTheSecond, monitor: new MonitorHandle(2));
        fixture.Desk.SetFloating(DeskFixture.W(1), true);
        fixture.Turn();

        fixture.Move(1, OnTheMain);
        fixture.Turn();
        fixture.Turn();

        DeskWindow window = fixture.Desk.Window(DeskFixture.W(1))!;
        Assert.Equal("11", window.Workspace);
        Assert.Equal(OnTheMain, fixture.Platform.Window(DeskFixture.W(1))!.FrameBounds);
    }

    [Fact]
    public void A_window_moved_within_its_own_monitor_does_not_change_workspace()
    {
        var fixture = new DeskFixture();
        fixture.Open(1);
        fixture.Desk.SetFloating(DeskFixture.W(1), true);
        fixture.Desk.MoveToWorkspace(DeskFixture.W(1), "12");
        fixture.Turn();

        fixture.Move(1, new Rect(1500, 600, 900, 700));
        fixture.Turn();

        // Not "11", which is what re-homing on every move would give: the
        // workspace a person chose on this monitor is theirs to keep.
        Assert.Equal("12", fixture.Desk.Window(DeskFixture.W(1))!.Workspace);
    }

    [Fact]
    public void A_window_AkuWM_itself_placed_on_the_other_monitor_is_not_re_homed()
    {
        var fixture = new DeskFixture();
        fixture.Open(1);
        fixture.Desk.SetFloating(DeskFixture.W(1), true);
        fixture.Turn();

        // The desk's own doing: a move to a workspace of the second monitor
        // lands the window there, and that must not read as a person dragging
        // it and send it round again.
        fixture.Desk.MoveToWorkspace(DeskFixture.W(1), "22");
        fixture.Turn();
        fixture.Turn();

        Assert.Equal("22", fixture.Desk.Window(DeskFixture.W(1))!.Workspace);
    }
}
