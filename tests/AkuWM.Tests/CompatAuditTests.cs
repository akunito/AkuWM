using System.Text.Json.Nodes;
using AkuWM.Core.Compat;
using AkuWM.Core.Config;
using AkuWM.Core.Desk;
using AkuWM.Core.Model;
using Xunit;

namespace AkuWM.Tests;

/// <summary>
/// The rest of the grammar the bar and the scripts can send: every verb the
/// first pass declared and did not exercise, and the shapes a caller gets back
/// when there is nothing to answer with.
/// </summary>
public class CompatAuditTests
{
    private readonly DeskFixture _fixture = new();
    private readonly FakeDeskPlatform _platform = new();
    private readonly GlazeExecutor _executor;

    public CompatAuditTests() => _executor = new GlazeExecutor(_fixture.Desk, _platform);

    private static WindowHandle W(long handle) => new(handle);

    private JsonNode Query(string what)
    {
        ExecResult result = _executor.Query(what);
        Assert.True(result.Success, result.Error);
        return result.Data!;
    }

    // ---- the shapes --------------------------------------------------------

    [Fact]
    public void A_sticky_window_is_listed_once_not_twice()
    {
        _fixture.Open(1, resizable: false, frame: new Rect(3000, 1500, 600, 400));
        _fixture.Desk.SetSticky(W(1), true);
        _fixture.Turn();

        JsonArray windows = Query("windows")["windows"]!.AsArray();

        // It belongs to the monitor and is drawn on the displayed workspace,
        // which is two good reasons to list it and one window to list. A
        // duplicate makes the raise-or-launch scripts count two of something
        // there is one of.
        Assert.Single(windows);
    }

    [Fact]
    public void A_sticky_window_still_appears_under_the_workspace_on_screen()
    {
        _fixture.Open(1, resizable: false, frame: new Rect(3000, 1500, 600, 400));
        _fixture.Desk.SetSticky(W(1), true);
        _fixture.Turn();

        JsonArray workspaces = Query("workspaces")["workspaces"]!.AsArray();
        JsonNode? displayed = workspaces.FirstOrDefault(w => w!["name"]!.GetValue<string>() == "11");

        Assert.Single(displayed!["children"]!.AsArray());
        Assert.True(displayed["children"]![0]!["sticky"]!.GetValue<bool>());
    }

    [Fact]
    public void An_empty_desk_answers_with_the_workspace_rather_than_nothing()
    {
        JsonNode? focused = Query("focused")["focused"];

        // The scripts read this to know where they are. A null would read as
        // "the window manager is not running", which makes them launch a
        // second copy of whatever they were looking for.
        Assert.NotNull(focused);
        Assert.Equal("workspace", focused!["type"]!.GetValue<string>());
        Assert.Equal("11", focused["name"]!.GetValue<string>());
    }

    [Fact]
    public void An_unmanaged_window_is_not_in_any_answer()
    {
        AkuWmConfig config = DeskFixture.Configuration();
        config.Rules =
        [
            new RuleConfig
            {
                Name = "the bar",
                Match = [new MatchCriteria { Process = "zebar" }],
                Actions = ["ignore"],
            },
        ];

        var fixture = new DeskFixture(config);
        var executor = new GlazeExecutor(fixture.Desk, new FakeDeskPlatform());
        fixture.Open(1, process: "zebar");
        fixture.Open(2, process: "zen");
        fixture.Turn();

        JsonArray windows = executor.Query("windows").Data!["windows"]!.AsArray();

        Assert.Single(windows);
        Assert.Equal("zen", windows[0]!["processName"]!.GetValue<string>());
    }

    [Fact]
    public void The_tiling_direction_of_the_workspace_can_be_asked_for()
    {
        _fixture.Open(1);
        _fixture.Open(2);
        _fixture.Turn();
        _fixture.Desk.Focus(W(1));

        Assert.Equal("horizontal", Query("tiling-direction")["tilingDirection"]!.GetValue<string>());

        _executor.Command("toggle-tiling-direction");

        Assert.Equal("vertical", Query("tiling-direction")["tilingDirection"]!.GetValue<string>());
    }

    [Fact]
    public void Pausing_is_remembered_and_can_be_read_back()
    {
        Assert.False(Query("paused").GetValue<bool>());

        Assert.True(_executor.Command("wm-toggle-pause").Success);
        Assert.True(Query("paused").GetValue<bool>());

        _executor.Command("wm-toggle-pause");
        Assert.False(Query("paused").GetValue<bool>());
    }

    [Fact]
    public void Binding_modes_answers_with_an_empty_list_rather_than_a_refusal()
    {
        // AkuWM has none. The bar asks anyway, and a refusal there is an error
        // line in somebody's log for ever.
        Assert.Empty(Query("binding-modes")["bindingModes"]!.AsArray());
    }

    [Fact]
    public void An_unknown_query_is_refused_the_way_the_scripts_expect()
    {
        ExecResult result = _executor.Query("the-meaning-of-it-all");

        Assert.False(result.Success);
        Assert.Contains("unrecognized subcommand", result.Error);
    }

    // ---- the verbs ---------------------------------------------------------

    [Fact]
    public void Focus_monitor_takes_an_index_and_lands_on_that_screen()
    {
        _fixture.Open(1);
        _fixture.Open(2, monitor: new MonitorHandle(2));
        _fixture.Turn();
        _fixture.Desk.Focus(W(1));

        Assert.True(_executor.Command("focus --monitor 1").Success);
        Redraw redraw = _fixture.Turn();

        Assert.Equal(W(2), redraw.Focus);
    }

    [Fact]
    public void Focus_monitor_says_no_to_a_screen_that_is_not_there()
    {
        ExecResult result = _executor.Command("focus --monitor 7");

        Assert.False(result.Success);
        Assert.Contains("no monitor 7", result.Error);
    }

    [Fact]
    public void The_next_and_previous_workspace_are_the_ones_beside_it()
    {
        _fixture.Open(1);
        _fixture.Turn();
        _fixture.Desk.Focus(W(1));

        Assert.True(_executor.Command("focus --next-workspace").Success);
        Assert.Equal("12", _fixture.Desk.MonitorByRole("main")!.Displayed!.Name);

        Assert.True(_executor.Command("focus --prev-workspace").Success);
        Assert.Equal("11", _fixture.Desk.MonitorByRole("main")!.Displayed!.Name);
    }

    [Fact]
    public void There_is_no_workspace_before_the_first_one()
    {
        _fixture.Open(1);
        _fixture.Turn();
        _fixture.Desk.Focus(W(1));

        ExecResult result = _executor.Command("focus --prev-workspace");

        Assert.False(result.Success);
        Assert.Contains("no workspace that way", result.Error);
    }

    [Fact]
    public void Close_is_handed_to_the_platform_for_the_focused_window()
    {
        _fixture.Open(1);
        _fixture.Desk.Focus(W(1));

        Assert.True(_executor.Command("close").Success);

        Assert.Contains("close 1", _platform.Calls);
    }

    [Fact]
    public void Shell_exec_runs_what_it_was_given()
    {
        Assert.True(_executor.Command("shell-exec wt.exe --title thing").Success);

        Assert.Contains(_platform.Calls, c => c.StartsWith("exec wt.exe"));
    }

    [Fact]
    public void Set_tiling_puts_a_floating_window_back_in_the_layout()
    {
        _fixture.Open(1);
        _fixture.Open(2, resizable: false, frame: new Rect(500, 500, 400, 300));
        _fixture.Turn();
        _fixture.Desk.Focus(W(2));

        Assert.True(_executor.Command("set-tiling").Success);
        _fixture.Turn();

        Assert.Equal(WindowState.Tiling, _fixture.Managed(2)!.State);
        Assert.True(_fixture.FrameOf(1).Width < 3000, "the two should be sharing the screen");
    }

    [Fact]
    public void Set_and_unset_sticky_are_not_the_same_as_toggling_it()
    {
        _fixture.Open(1);
        _fixture.Turn();
        _fixture.Desk.Focus(W(1));

        Assert.True(_executor.Command("set-sticky").Success);
        Assert.True(_fixture.Managed(1)!.Sticky);

        // Asking again for what is already true changes nothing, and says so.
        Assert.False(_executor.Command("set-sticky").Success);

        Assert.True(_executor.Command("unset-sticky").Success);
        Assert.False(_fixture.Managed(1)!.Sticky);
    }

    [Fact]
    public void A_command_with_no_focused_window_is_refused_rather_than_guessed()
    {
        foreach (string verb in new[] { "toggle-fullscreen", "toggle-floating", "close", "set-minimized" })
        {
            ExecResult result = _executor.Command(verb);
            Assert.False(result.Success);
            Assert.NotNull(result.Error);
        }
    }

    [Fact]
    public void An_id_that_names_nothing_falls_back_to_the_focused_window()
    {
        _fixture.Open(1);
        _fixture.Turn();
        _fixture.Desk.Focus(W(1));

        // Not a container id at all: the option parser leaves it alone, and
        // the command acts on the focus, which is what a person meant.
        Assert.True(_executor.Command("--id not-a-guid toggle-fullscreen").Success);
        Assert.Equal(WindowState.Fullscreen, _fixture.Managed(1)!.State);
    }

    [Fact]
    public void An_id_that_names_a_container_that_is_gone_is_refused()
    {
        _fixture.Open(1);
        _fixture.Turn();
        _fixture.Desk.Focus(W(1));

        ExecResult result = _executor.Command($"focus --container-id {Guid.NewGuid()}");

        Assert.False(result.Success);
        Assert.Contains("no container", result.Error);
    }

    [Fact]
    public void Focusing_a_window_on_a_hidden_workspace_brings_that_workspace_out()
    {
        _fixture.Open(1);
        _fixture.Turn();
        Guid id = _fixture.Managed(1)!.Id;
        _fixture.Desk.FocusWorkspace("12");
        _fixture.Turn();
        Assert.True(_fixture.IsHidden(1));

        Assert.True(_executor.Command($"focus --container-id {id}").Success);
        Redraw redraw = _fixture.Turn();

        Assert.Equal("11", _fixture.Desk.MonitorByRole("main")!.Displayed!.Name);
        Assert.False(_fixture.IsHidden(1));
        Assert.Equal(W(1), redraw.Focus);
    }

    [Fact]
    public void Resize_needs_to_be_told_which_way()
    {
        _fixture.Open(1);
        _fixture.Desk.Focus(W(1));

        ExecResult result = _executor.Command("resize --depth 5%");

        Assert.False(result.Success);
        Assert.Contains("--width or --height", result.Error);
    }

    [Fact]
    public void Focus_needs_to_be_told_what_to_focus()
    {
        ExecResult result = _executor.Command("focus");

        Assert.False(result.Success);
        Assert.Contains("--workspace", result.Error);
    }

    [Fact]
    public void An_empty_line_is_refused_without_throwing()
    {
        Assert.False(_executor.Command(string.Empty).Success);
        Assert.False(_executor.Query(string.Empty).Success);
    }

    [Fact]
    public void Height_resizes_downwards()
    {
        _fixture.Open(1);
        _fixture.Open(2);
        _fixture.Turn();
        _fixture.Desk.Focus(W(1));
        _executor.Command("toggle-tiling-direction");
        _fixture.Turn();

        Assert.True(_executor.Command("resize --height 5%").Success);
        _fixture.Turn();

        Assert.True(_fixture.FrameOf(1).Height > _fixture.FrameOf(2).Height);
    }

    [Fact]
    public void A_windows_path_survives_shell_exec()
    {
        // A backslash inside quotes used to escape the next character, so every
        // app-launch chord whose target lives under Program Files arrived with
        // its separators eaten.
        const string path = @"C:\Program Files\Mozilla Firefox\firefox.exe";

        Assert.True(_executor.Command("shell-exec \"" + path + "\"").Success);

        Assert.Contains("exec " + path, _platform.Calls);
    }

    [Fact]
    public void An_escaped_quote_is_still_an_escaped_quote()
    {
        // a "say \"hi\""
        const string line = "a \"say \\\"hi\\\"\"";

        Assert.Equal(["a", "say \"hi\""], AkuWM.Core.Ipc.CommandLine.Split(line));
    }

    [Fact]
    public void A_path_that_ends_in_a_separator_survives_a_round_trip()
    {
        // Ambiguous to read on its own -- CommandLineToArgvW would call the
        // last backslash an escape too -- so the pair that matters is Join
        // then Split.
        string[] arguments = ["shell-exec", @"C:\Users\diego\"];

        Assert.Equal(arguments, AkuWM.Core.Ipc.CommandLine.Split(AkuWM.Core.Ipc.CommandLine.Join(arguments)));
    }

    [Fact]
    public void Toggle_minimized_brings_a_minimised_window_back()
    {
        _fixture.Open(1);
        _fixture.Turn();
        _fixture.Desk.Focus(W(1));

        Assert.True(_executor.Command("toggle-minimized").Success);
        Assert.Contains("minimize 1", _platform.Calls);

        _fixture.Platform.SetMinimized(W(1), true);
        _fixture.Sync();

        // Pressing the toggle on an already-minimised window used to minimise
        // it again, so a script could never raise one from the taskbar.
        Assert.True(_executor.Command($"toggle-minimized --id {_fixture.Managed(1)!.Id}").Success);
        Assert.Contains("restore 1", _platform.Calls);
    }

    [Fact]
    public void A_paused_desk_moves_nothing()
    {
        _fixture.Open(1);
        _fixture.Open(2);
        _fixture.Turn();
        _fixture.Desk.Focus(W(1));

        Assert.True(_executor.Command("wm-toggle-pause").Success);
        Assert.True(_executor.Command("focus --workspace 12").Success);

        // A script pauses to drag something, or to take a screenshot. It used
        // to get success and a window manager that kept arranging.
        Assert.True(_fixture.Desk.Compute().IsNothing);
        Assert.False(_fixture.IsHidden(1));

        _executor.Command("wm-toggle-pause");
        _fixture.Turn();
        Assert.True(_fixture.IsHidden(1));
    }

    [Fact]
    public void Exit_is_asked_for_rather_than_taken()
    {
        Assert.False(_executor.ExitRequested);

        Assert.True(_executor.Command("wm-exit").Success);

        Assert.True(_executor.ExitRequested);
    }
}
