using AkuWM.Core.Compat;
using AkuWM.Core.Config;
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

    // ---- the taskbar follows the workspace --------------------------------

    private static AkuWmConfig WithTaskbar(bool showAll)
    {
        AkuWmConfig config = DeskFixture.Configuration();
        config.General!.ShowAllInTaskbar = showAll;
        return config;
    }

    [Fact]
    public void A_window_on_a_workspace_nobody_is_looking_at_loses_its_button()
    {
        var fixture = new DeskFixture(WithTaskbar(showAll: false));
        fixture.Open(1);
        fixture.Turn();

        fixture.Desk.FocusWorkspace("12");
        Redraw redraw = fixture.Desk.Compute();

        // Windows has never heard of a workspace: a cloaked window keeps its
        // button unless somebody takes it off.
        Assert.Contains((DeskFixture.W(1), false), redraw.TaskbarButton);
    }

    [Fact]
    public void It_comes_back_with_the_window()
    {
        var fixture = new DeskFixture(WithTaskbar(showAll: false));
        fixture.Open(1);
        fixture.Turn();
        fixture.Desk.FocusWorkspace("12");
        fixture.Turn();

        fixture.Desk.FocusWorkspace("11");
        Redraw redraw = fixture.Desk.Compute();

        // A window with no pixels AND no button is one nobody can reach.
        Assert.Contains((DeskFixture.W(1), true), redraw.TaskbarButton);
    }

    [Fact]
    public void With_show_all_set_the_bar_keeps_every_window()
    {
        var fixture = new DeskFixture(WithTaskbar(showAll: true));
        fixture.Open(1);
        fixture.Turn();

        fixture.Desk.FocusWorkspace("12");
        Redraw redraw = fixture.Desk.Compute();

        Assert.DoesNotContain(redraw.TaskbarButton, b => b.Window == DeskFixture.W(1));
    }

    [Fact]
    public void A_window_AkuWM_never_hid_is_never_taken_off_the_bar()
    {
        var fixture = new DeskFixture(WithTaskbar(showAll: false));
        fixture.Open(1);

        // The very first pass must not go around removing buttons from windows
        // that are perfectly visible.
        Assert.Empty(fixture.Desk.Compute().TaskbarButton);
    }

    // ---- what the scripts send every day ----------------------------------

    [Fact]
    public void A_remembered_position_and_size_are_given_back_to_a_floating_window()
    {
        var executor = new GlazeExecutor(Desk, new FakeDeskPlatform());
        _fixture.Open(1);
        _fixture.Turn();
        Desk.SetFloating(DeskFixture.W(1), true);
        _fixture.Turn();

        Guid id = Desk.Window(DeskFixture.W(1))!.Id;

        // Verbatim from lib-repair.ahk and lib-app-toggle.ahk: both were
        // "unrecognized subcommand", so every app launched by a chord lost the
        // geometry it had and the repair after a monitor nap placed nothing.
        Assert.True(executor.Command($"--id {id} position --x-pos 400 --y-pos 300").Success);
        Assert.True(executor.Command($"--id {id} size --width 1100px --height 720px").Success);
        _fixture.Turn();

        Rect where = Desk.Window(DeskFixture.W(1))!.Snapshot.FrameBounds;
        Assert.Equal(new Rect(400, 300, 1100, 720), where);
    }

    [Fact]
    public void A_resize_written_in_pixels_is_not_read_as_a_percentage()
    {
        var executor = new GlazeExecutor(Desk, new FakeDeskPlatform());
        _fixture.Open(1);
        _fixture.Open(2);
        _fixture.Turn();

        int before = Desk.Window(DeskFixture.W(1))!.Snapshot.FrameBounds.Width;
        Guid id = Desk.Window(DeskFixture.W(1))!.Id;

        // What Alt+drag sends. `300px` parsed as null before, so the drop did
        // nothing; parsed as a percentage it would move the split three times
        // the width of the screen.
        Assert.True(executor.Command($"--id {id} resize --width 300px").Success);
        _fixture.Turn();

        int after = Desk.Window(DeskFixture.W(1))!.Snapshot.FrameBounds.Width;
        Assert.InRange(after - before, 240, 360);
    }

    [Fact]
    public void A_diagonal_resize_moves_both_axes()
    {
        var executor = new GlazeExecutor(Desk, new FakeDeskPlatform());
        _fixture.Open(1);
        _fixture.Turn();
        Desk.SetFloating(DeskFixture.W(1), true);
        _fixture.Turn();

        Rect before = Desk.Window(DeskFixture.W(1))!.Snapshot.FrameBounds;
        Guid id = Desk.Window(DeskFixture.W(1))!.Id;

        // A corner drag sends both, and only the first was honoured -- every
        // diagonal resize came out horizontal.
        Assert.True(executor.Command($"--id {id} resize --width 100px --height 80px").Success);
        _fixture.Turn();

        Rect after = Desk.Window(DeskFixture.W(1))!.Snapshot.FrameBounds;
        Assert.Equal(before.Width + 100, after.Width);
        Assert.Equal(before.Height + 80, after.Height);
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
