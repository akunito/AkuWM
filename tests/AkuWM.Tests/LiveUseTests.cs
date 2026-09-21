using AkuWM.Core.Desk;
using AkuWM.Core.Layout;
using AkuWM.Core.Model;
using Xunit;

namespace AkuWM.Tests;

/// <summary>
/// The things a person notices in a day of use, each one reported from the
/// desk or found by the driven suites rather than imagined here.
/// </summary>
public class LiveUseTests
{
    private readonly DeskFixture _fixture = new();

    private Desk Desk => _fixture.Desk;

    // ---- a floating window stays where it is put --------------------------

    [Fact]
    public void A_floating_window_the_person_drags_is_not_dragged_back()
    {
        _fixture.Open(1);
        _fixture.Turn();
        Desk.SetFloating(DeskFixture.W(1), true);
        _fixture.Turn();

        // The person drags it, and Windows tells us where it went.
        var moved = new Rect(700, 400, 900, 600);
        _fixture.Move(1, moved);
        _fixture.Turn();

        Assert.Equal(moved, Desk.Window(DeskFixture.W(1))!.Snapshot.FrameBounds);

        // And it is still there a redraw later, which is the part that was
        // broken: the next pass asked for the rectangle AkuWM remembered.
        _fixture.Turn();
        Assert.Equal(moved, Desk.Window(DeskFixture.W(1))!.Snapshot.FrameBounds);
    }

    [Fact]
    public void A_floating_window_the_person_resizes_keeps_its_new_size()
    {
        _fixture.Open(1);
        _fixture.Turn();
        Desk.SetFloating(DeskFixture.W(1), true);
        _fixture.Turn();

        var resized = new Rect(100, 100, 1500, 900);
        _fixture.Move(1, resized);
        _fixture.Turn();
        _fixture.Turn();

        Assert.Equal(resized, Desk.Window(DeskFixture.W(1))!.Snapshot.FrameBounds);
    }

    [Fact]
    public void A_tiled_window_the_person_drags_goes_back_where_it_belongs()
    {
        // The other half of the rule: only a FLOATING window keeps where it is
        // put. A tiled one belongs to the tree, and Alt+drag on it is a
        // request to re-order, not to leave it hanging.
        _fixture.Open(1);
        _fixture.Open(2);
        _fixture.Turn();

        Rect tiled = Desk.Window(DeskFixture.W(1))!.Snapshot.FrameBounds;
        _fixture.Move(1, new Rect(1234, 567, 400, 300));
        _fixture.Turn();

        Assert.Equal(tiled, Desk.Window(DeskFixture.W(1))!.Snapshot.FrameBounds);
    }

    [Fact]
    public void A_window_AkuWM_moved_itself_is_not_mistaken_for_a_drag()
    {
        _fixture.Open(1);
        _fixture.Turn();
        Desk.SetFloating(DeskFixture.W(1), true);
        _fixture.Turn();

        Rect where = Desk.Window(DeskFixture.W(1))!.Snapshot.FrameBounds;
        Rect? remembered = Desk.Window(DeskFixture.W(1))!.FloatingRect;

        // A window that rounds its own size lands near what was asked for, not
        // on it. Learning that as a move would drift the remembered rectangle
        // a little further on every redraw.
        _fixture.Move(1, where with { Width = where.Width - 3, Height = where.Height - 2 });
        _fixture.Turn();

        Assert.Equal(remembered, Desk.Window(DeskFixture.W(1))!.FloatingRect);
    }

    // ---- a new window opens where the person is looking -------------------

    [Fact]
    public void A_window_opened_while_the_second_monitor_has_the_focus_lands_there()
    {
        _fixture.Open(1);
        _fixture.Turn();

        Desk.FocusWorkspace("21");
        _fixture.Turn();

        // Windows puts it on the primary, as it does; the person meant here.
        _fixture.Open(2);
        _fixture.Turn();

        Assert.Equal("21", Desk.Window(DeskFixture.W(2))!.Workspace);
    }

    [Fact]
    public void The_windows_already_on_the_desk_stay_where_they_are()
    {
        // The first sync is the exception: every window is new then, and
        // piling them all onto one workspace would be the worst possible
        // first impression.
        _fixture.Open(1, sync: false);
        _fixture.Open(
            2,
            monitor: new MonitorHandle(2),
            frame: new Rect(3900, 0, 900, 700),
            sync: false);
        _fixture.Sync();
        _fixture.Turn();

        Assert.Equal("11", Desk.Window(DeskFixture.W(1))!.Workspace);
        Assert.Equal("21", Desk.Window(DeskFixture.W(2))!.Workspace);
    }

    // ---- resize reaches the neighbour it has ------------------------------

    [Fact]
    public void The_last_window_in_a_row_can_still_be_made_wider()
    {
        _fixture.Open(1);
        _fixture.Open(2);
        _fixture.Turn();

        int before = Desk.Window(DeskFixture.W(2))!.Snapshot.FrameBounds.Width;

        // Nothing to its right: the share has to come from the left, which it
        // did not, so `resize --width 10%` on the end of a row did nothing.
        Assert.True(Desk.Resize(DeskFixture.W(2), Direction.Right, 10));
        _fixture.Turn();

        Assert.True(Desk.Window(DeskFixture.W(2))!.Snapshot.FrameBounds.Width > before);
    }

    [Fact]
    public void The_first_window_in_a_row_can_still_be_made_wider()
    {
        _fixture.Open(1);
        _fixture.Open(2);
        _fixture.Turn();

        int before = Desk.Window(DeskFixture.W(1))!.Snapshot.FrameBounds.Width;

        Assert.True(Desk.Resize(DeskFixture.W(1), Direction.Left, 10));
        _fixture.Turn();

        Assert.True(Desk.Window(DeskFixture.W(1))!.Snapshot.FrameBounds.Width > before);
    }
}
