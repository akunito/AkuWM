using AkuWM.Core.Desk;
using AkuWM.Core.Model;
using AkuWM.Core.Platform;
using AkuWM.Core.State;
using Xunit;

namespace AkuWM.Tests;

internal sealed class FakeTaskbar : ITaskbar
{
    public List<(WindowHandle Window, bool Fullscreen)> Marks { get; } = [];

    /// <summary>What the shell does when explorer has just restarted.</summary>
    public bool Refuses { get; set; }

    public bool MarkFullscreen(WindowHandle window, bool fullscreen)
    {
        Marks.Add((window, fullscreen));
        return !Refuses;
    }

    /// <summary>Which windows have a button on the bar, by handle.</summary>
    public List<(WindowHandle Window, bool Shown)> Buttons { get; } = [];

    public bool ShowInTaskbar(WindowHandle window, bool shown)
    {
        Buttons.Add((window, shown));
        return true;
    }
}

/// <summary>
/// The half that carries out a redraw: the order it does things in, the
/// records it writes before it does them, and what it does when the desk says
/// no.
/// </summary>
/// <remarks>
/// This used to live in the Windows project, where none of it could be
/// exercised. It is the machinery that hides windows -- the one operation with
/// no undo if it goes wrong -- so it is worth more tests than most of the
/// model.
/// </remarks>
public class DeskApplierTests
{
    private readonly TempDir _dir = new();
    private readonly FakePlatform _platform = new();
    private readonly FakeTaskbar _taskbar = new();
    private readonly CloakLedger _ledger;
    private readonly GeometryJournal _journal;
    private readonly DeskApplier _applier;

    public DeskApplierTests()
    {
        _ledger = new CloakLedger(_dir.File("cloaked.bin"));
        _journal = new GeometryJournal(_dir.File("geometry.bin"));
        _applier = new DeskApplier(_platform, _platform, _ledger, _journal, _taskbar);
    }

    private WindowSnapshot Open(long handle, Rect? frame = null)
    {
        WindowSnapshot window = FakePlatform.Window(handle, "zen", frame: frame ?? new Rect(100, 100, 800, 600));
        _platform.WindowList.Add(window);
        return window;
    }

    private static WindowHandle W(long handle) => new(handle);

    [Fact]
    public void Nothing_to_do_costs_nothing()
    {
        ApplyResult result = _applier.Apply(Redraw.Nothing);

        Assert.Equal(0, result.Placed);
        Assert.Empty(_platform.Calls);
    }

    [Fact]
    public void A_window_is_moved_before_it_is_shown()
    {
        Open(1, new Rect(100, 100, 800, 600));
        _platform.SetCloak(W(1), true);
        _platform.Calls.Clear();

        _applier.Apply(new Redraw
        {
            Place = [new Placement(W(1), new Rect(0, 42, 1920, 2118))],
            Show = [W(1)],
        });

        // Placed while still hidden, so it is never seen in the wrong place.
        int placed = _platform.Calls.FindIndex(c => c.StartsWith("place 1"));
        int shown = _platform.Calls.FindIndex(c => c == "cloak 1 False");
        Assert.True(placed >= 0 && shown > placed, string.Join(", ", _platform.Calls));
    }

    [Fact]
    public void The_outgoing_workspace_is_hidden_before_the_incoming_one_is_shown()
    {
        Open(1);
        Open(2);
        _platform.SetCloak(W(2), true);
        _platform.Calls.Clear();

        _applier.Apply(new Redraw { Hide = [W(1)], Show = [W(2)] });

        int hidden = _platform.Calls.FindIndex(c => c == "cloak 1 True");
        int shown = _platform.Calls.FindIndex(c => c == "cloak 2 False");
        Assert.True(hidden >= 0 && shown > hidden, string.Join(", ", _platform.Calls));
    }

    [Fact]
    public void Where_a_window_was_is_written_down_before_it_is_moved()
    {
        Open(1, new Rect(200, 300, 900, 700));

        _applier.Apply(new Redraw { Place = [new Placement(W(1), new Rect(0, 42, 1920, 2118))] });

        OriginalGeometry remembered = Assert.Single(_journal.Entries);
        Assert.Equal(new Rect(200, 300, 900, 700), remembered.Frame);
    }

    [Fact]
    public void A_window_is_written_into_the_ledger_before_it_is_hidden()
    {
        Open(1);

        _applier.Apply(new Redraw { Hide = [W(1)] });

        CloakedWindow recorded = Assert.Single(_ledger.Entries);
        Assert.Equal(1, recorded.Handle);
        Assert.True(_platform.Window(W(1))!.Cloak.HasFlag(CloakKind.Shell));
    }

    [Fact]
    public void A_window_that_comes_back_is_taken_out_of_the_ledger()
    {
        Open(1);
        _applier.Apply(new Redraw { Hide = [W(1)] });
        Assert.Single(_ledger.Entries);

        _applier.Apply(new Redraw { Show = [W(1)] });

        Assert.Empty(_ledger.Entries);
    }

    [Fact]
    public void A_cloak_that_reports_success_and_does_nothing_is_not_believed()
    {
        Open(1);
        _platform.RefusesToUncloak.Add(1);
        _applier.Apply(new Redraw { Hide = [W(1)] });

        ApplyResult result = _applier.Apply(new Redraw { Show = [W(1)] });

        // The shell has a spelling of this call that says yes and does not.
        Assert.Contains(W(1), result.Refused);
        Assert.True(_platform.Window(W(1))!.Cloak.HasFlag(CloakKind.Shell));
    }

    [Fact]
    public void A_window_that_refused_to_hide_is_not_left_in_the_ledger()
    {
        Open(1);
        _platform.RefusesToCloak.Add(1);

        ApplyResult result = _applier.Apply(new Redraw { Hide = [W(1)] });

        // It is not hidden, so there is nothing to give back, and an entry
        // saying otherwise sends the next start looking for a window that was
        // never lost.
        Assert.Contains(W(1), result.Refused);
        Assert.Empty(_ledger.Entries);
    }

    [Fact]
    public void A_window_that_has_closed_between_the_decision_and_the_call_is_skipped()
    {
        _applier.Apply(new Redraw { Hide = [W(404)] });

        Assert.Empty(_ledger.Entries);
    }

    [Fact]
    public void The_taskbar_is_told_both_ways()
    {
        Open(1);

        _applier.Apply(new Redraw { TaskbarMark = [(W(1), true)] });
        _applier.Apply(new Redraw { TaskbarMark = [(W(1), false)] });

        Assert.Equal([(W(1), true), (W(1), false)], _taskbar.Marks);
    }

    [Fact]
    public void A_mark_the_shell_refuses_is_reported_rather_than_assumed()
    {
        Open(1);
        _taskbar.Refuses = true;

        ApplyResult result = _applier.Apply(new Redraw { TaskbarMark = [(W(1), true)] });

        Assert.Equal([W(1)], result.Unmarked!);
    }

    [Fact]
    public void Nothing_is_allocated_for_marks_that_worked()
    {
        Open(1);

        ApplyResult result = _applier.Apply(new Redraw { TaskbarMark = [(W(1), true)] });

        Assert.Null(result.Unmarked);
    }

    [Fact]
    public void The_focus_is_the_last_thing_that_happens()
    {
        Open(1);
        _platform.SetCloak(W(1), true);
        _platform.Calls.Clear();

        _applier.Apply(new Redraw
        {
            Place = [new Placement(W(1), new Rect(0, 42, 1920, 2118))],
            Show = [W(1)],
            Focus = W(1),
        });

        Assert.Equal("focus 1", _platform.Calls.Last());
    }

    // ---- the round trip ----------------------------------------------------

    [Fact]
    public void The_first_hide_of_a_run_proves_the_window_can_come_back()
    {
        Open(1);

        _applier.Apply(new Redraw { Hide = [W(1)] });

        // Hidden, shown, hidden: two extra calls, once, against losing
        // somebody's work.
        Assert.Equal(["cloak 1 True", "cloak 1 False", "cloak 1 True"], _platform.Calls);
        Assert.True(_applier.CanHide);
        Assert.True(_platform.Window(W(1))!.Cloak.HasFlag(CloakKind.Shell));
    }

    [Fact]
    public void The_proof_is_only_done_once()
    {
        Open(1);
        Open(2);

        _applier.Apply(new Redraw { Hide = [W(1)] });
        _platform.Calls.Clear();
        _applier.Apply(new Redraw { Hide = [W(2)] });

        Assert.Equal(["cloak 2 True"], _platform.Calls);
    }

    [Fact]
    public void When_a_hidden_window_will_not_come_back_nothing_else_is_hidden()
    {
        Open(1);
        Open(2);
        _platform.RefusesToUncloak.Add(1);

        ApplyResult first = _applier.Apply(new Redraw { Hide = [W(1), W(2)] });

        // The proof failed on the first window. The second is not hidden at
        // all, and neither is anything else for the rest of the run.
        Assert.False(_applier.CanHide);
        Assert.Contains(W(2), first.Refused);
        Assert.False(_platform.Window(W(2))!.Cloak.HasFlag(CloakKind.Shell));

        _platform.Calls.Clear();
        ApplyResult second = _applier.Apply(new Redraw { Hide = [W(2)] });

        Assert.Contains(W(2), second.Refused);
        Assert.Empty(_platform.Calls);
    }

    [Fact]
    public void Showing_still_works_after_hiding_has_been_given_up_on()
    {
        Open(1);
        Open(2);
        _platform.RefusesToUncloak.Add(1);
        _applier.Apply(new Redraw { Hide = [W(1)] });
        Assert.False(_applier.CanHide);

        // Whatever is already hidden must still be recoverable, or giving up
        // would strand exactly the windows this exists to protect.
        _platform.RefusesToUncloak.Clear();
        _platform.SetCloak(W(2), true);
        ApplyResult result = _applier.Apply(new Redraw { Show = [W(2)] });

        Assert.DoesNotContain(W(2), result.Refused);
        Assert.False(_platform.Window(W(2))!.Cloak.HasFlag(CloakKind.Shell));
    }

    [Fact]
    public void Every_other_way_of_undoing_a_cloak_is_tried_before_giving_up()
    {
        Open(1);
        _platform.RefusesToUncloak.Add(1);
        var tried = new List<string>();

        var applier = new DeskApplier(
            _platform,
            _platform,
            _ledger,
            _journal,
            _taskbar,
            lastResort: handle =>
            {
                tried.Add($"last resort for {handle.Value}");
                return [("something else entirely", null)];
            });

        applier.Apply(new Redraw { Hide = [W(1)] });

        Assert.Equal(["last resort for 1"], tried);
        Assert.False(applier.CanHide);
    }

    [Fact]
    public void A_shell_that_will_not_hide_anything_at_all_is_reported_rather_than_retried()
    {
        Open(1);
        _platform.RefusesToCloak.Add(1);

        _applier.Apply(new Redraw { Hide = [W(1)] });

        Assert.False(_applier.CanHide);
        Assert.Empty(_ledger.Entries);
    }
}
