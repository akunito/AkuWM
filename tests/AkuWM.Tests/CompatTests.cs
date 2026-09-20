using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AkuWM.Core.Compat;
using AkuWM.Core.Config;
using AkuWM.Core.Desk;
using AkuWM.Core.Model;
using Xunit;

namespace AkuWM.Tests;

internal sealed class FakeDeskPlatform : IDeskPlatform
{
    public List<string> Calls { get; } = [];

    public WindowHandle Focused { get; private set; } = WindowHandle.None;

    public bool Focus(WindowHandle window)
    {
        Calls.Add($"focus {window.Value}");
        Focused = window;
        return true;
    }

    public void Minimize(WindowHandle window) => Calls.Add($"minimize {window.Value}");

    public void Restore(WindowHandle window) => Calls.Add($"restore {window.Value}");

    public void Close(WindowHandle window) => Calls.Add($"close {window.Value}");

    public void Exec(string command) => Calls.Add($"exec {command}");
}

/// <summary>
/// The wire format the bar and 1,961 lines of working AutoHotkey already
/// speak, and which M2 has to keep speaking while everything underneath is
/// replaced.
/// </summary>
public class CompatTests
{
    private readonly DeskFixture _fixture = new();
    private readonly FakeDeskPlatform _platform = new();
    private readonly GlazeExecutor _executor;

    public CompatTests() => _executor = new GlazeExecutor(_fixture.Desk, _platform);

    private static string Compact(JsonNode? node) =>
        node?.ToJsonString(new JsonSerializerOptions { WriteIndented = false }) ?? "null";

    // ---- the command line -------------------------------------------------

    [Fact]
    public void A_plain_command_parses()
    {
        ParsedCommand parsed = GlazeCommandLine.Parse("focus --workspace 11");

        Assert.Equal("focus", parsed.Verb);
        Assert.Equal("11", parsed.Value("workspace"));
    }

    [Fact]
    public void A_value_attached_with_an_equals_sign_parses()
    {
        // The scripts send exactly this.
        ParsedCommand parsed = GlazeCommandLine.Parse("toggle-floating --centered=false");

        Assert.Equal("toggle-floating", parsed.Verb);
        Assert.Equal("false", parsed.Value("centered"));
    }

    [Fact]
    public void A_negative_value_is_a_value_and_not_an_option()
    {
        ParsedCommand parsed = GlazeCommandLine.Parse("resize --width -5%");

        Assert.Equal(-5, parsed.Number("width"));
    }

    [Fact]
    public void The_id_prefix_names_the_container_to_act_on()
    {
        var id = Guid.NewGuid();
        ParsedCommand parsed = GlazeCommandLine.Parse($"--id {id} move --direction right");

        Assert.Equal("move", parsed.Verb);
        Assert.Equal(id, parsed.Subject);
        Assert.Equal("right", parsed.Value("direction"));
    }

    // ---- the JSON the scripts parse with regular expressions --------------

    [Fact]
    public void The_workspaces_query_matches_the_pattern_the_scripts_use()
    {
        _fixture.Open(1, process: "zen", title: "a tab", className: "MozillaWindowClass");
        _fixture.Turn();

        string json = Compact(_executor.Query("workspaces").Data);

        // The pattern out of the running scripts, verbatim. It is a regular
        // expression over the raw text, so the ORDER of the properties is the
        // contract -- not the shape of the object.
        const string pattern =
            "\"type\":\"workspace\",\"id\":\"[^\"]+\",\"name\":\"([^\"]+)\""
            + "|\"type\":\"window\",\"id\":\"([^\"]+)\",\"parentId\":\"([^\"]+)\",\"hasFocus\":(true|false)"
            + ".*?\"state\":\\{\"type\":\"([a-z]+)\".*?\"displayState\":\"([a-z]+)\""
            + "(?:,\"sticky\":(true|false))?"
            + ".*?\"handle\":(-?\\d+),\"title\":\"((?:[^\"\\\\]|\\\\.)*)\",\"className\":\"((?:[^\"\\\\]|\\\\.)*)\""
            + ",\"processName\":\"([^\"]*)\"";

        MatchCollection matches = Regex.Matches(json, pattern);

        Assert.Contains(matches.Cast<Match>(), m => m.Groups[1].Value == "11");

        Match window = matches.Cast<Match>().First(m => m.Groups[2].Success && m.Groups[2].Value.Length > 0);
        Assert.Equal("tiling", window.Groups[5].Value);
        Assert.Equal("shown", window.Groups[6].Value);
        Assert.Equal("false", window.Groups[7].Value);
        Assert.Equal("1", window.Groups[8].Value);
        Assert.Equal("a tab", window.Groups[9].Value);
        Assert.Equal("MozillaWindowClass", window.Groups[10].Value);
        Assert.Equal("zen", window.Groups[11].Value);
    }

    [Fact]
    public void A_hidden_window_says_so()
    {
        _fixture.Open(1);
        _fixture.Turn();
        _fixture.Desk.FocusWorkspace("12");
        _fixture.Turn();

        string json = Compact(_executor.Query("workspaces").Data);

        Assert.Contains("\"displayState\":\"hidden\"", json);
    }

    [Fact]
    public void A_title_with_quotes_in_it_does_not_break_the_json()
    {
        _fixture.Open(1, title: "a \"quoted\" title \\ with a backslash");
        _fixture.Turn();

        string json = Compact(_executor.Query("windows").Data);

        // Parsing it back is the real assertion: a hand-built JSON object that
        // escapes badly is a bar that goes blank.
        JsonNode? parsed = JsonNode.Parse(json);
        Assert.Equal(
            "a \"quoted\" title \\ with a backslash",
            parsed!["windows"]![0]!["title"]!.GetValue<string>());
    }

    [Fact]
    public void The_monitors_query_answers_with_identity_and_geometry()
    {
        _fixture.Open(1);
        JsonNode? data = _executor.Query("monitors").Data;

        JsonArray monitors = data!["monitors"]!.AsArray();
        Assert.Equal(2, monitors.Count);
        Assert.Equal("SAM7233", monitors[0]!["hardwareId"]!.GetValue<string>());
        Assert.Equal(3840, monitors[0]!["width"]!.GetValue<int>());
        Assert.Equal(1.5, monitors[0]!["scaleFactor"]!.GetValue<double>());
        // AkuWM's own addition: what the monitor is, not where it happens to be.
        Assert.Equal("main", monitors[0]!["role"]!.GetValue<string>());
    }

    [Fact]
    public void A_window_points_at_the_workspace_it_is_on()
    {
        _fixture.Open(1);
        _fixture.Turn();

        JsonNode? windows = _executor.Query("windows").Data;
        string parentId = windows!["windows"]![0]!["parentId"]!.GetValue<string>();

        // Flat, so this is the workspace and not a split container -- which is
        // what the scripts that read it were working around.
        Assert.Equal(_fixture.Desk.Workspace("11")!.Id.ToString(), parentId);
    }

    // ---- the commands -----------------------------------------------------

    [Fact]
    public void Focus_workspace_switches_and_asks_for_the_focus_afterwards()
    {
        _fixture.Open(1);
        _fixture.Desk.FocusWorkspace("12");
        _fixture.Open(2);
        _fixture.Turn();

        Assert.True(_executor.Command("focus --workspace 11").Success);
        Redraw redraw = _fixture.Turn();

        Assert.Equal("11", _fixture.Desk.MonitorByRole("main")!.Displayed!.Name);
        // The focus travels with the redraw: focusing a window while it is
        // still cloaked hands the keyboard to something nobody can see.
        Assert.Equal(DeskFixture.W(1), redraw.Focus);
    }

    [Fact]
    public void Focus_direction_moves_between_windows()
    {
        _fixture.Open(1);
        _fixture.Open(2);
        _fixture.Turn();
        _fixture.Desk.Focus(DeskFixture.W(1));

        Assert.True(_executor.Command("focus --direction right").Success);
        Redraw redraw = _fixture.Turn();

        Assert.Equal(DeskFixture.W(2), redraw.Focus);
    }

    [Fact]
    public void Focus_direction_says_no_when_there_is_nothing_that_way()
    {
        _fixture.Open(1);
        _fixture.Turn();
        _fixture.Desk.Focus(DeskFixture.W(1));

        ExecResult result = _executor.Command("focus --direction left");

        Assert.False(result.Success);
        Assert.Contains("left", result.Error);
    }

    [Fact]
    public void Move_workspace_moves_the_focused_window()
    {
        _fixture.Open(1);
        _fixture.Open(2);
        _fixture.Turn();
        _fixture.Desk.Focus(DeskFixture.W(2));

        Assert.True(_executor.Command("move --workspace 13").Success);
        _fixture.Turn();

        Assert.Equal("13", _fixture.Managed(2)!.Workspace);
        Assert.True(_fixture.IsHidden(2));
    }

    [Fact]
    public void A_command_with_an_id_acts_on_that_window_not_the_focused_one()
    {
        _fixture.Open(1);
        _fixture.Open(2);
        _fixture.Turn();
        _fixture.Desk.Focus(DeskFixture.W(1));

        Guid id = _fixture.Managed(2)!.Id;
        Assert.True(_executor.Command($"--id {id} move --workspace 13").Success);
        _fixture.Turn();

        Assert.Equal("13", _fixture.Managed(2)!.Workspace);
        Assert.Equal("11", _fixture.Managed(1)!.Workspace);
    }

    [Fact]
    public void Resize_changes_the_share_by_the_points_asked_for()
    {
        _fixture.Open(1);
        _fixture.Open(2);
        _fixture.Turn();
        _fixture.Desk.Focus(DeskFixture.W(1));

        Assert.True(_executor.Command("resize --width 5%").Success);
        _fixture.Turn();

        Assert.True(_fixture.FrameOf(1).Width > _fixture.FrameOf(2).Width);
    }

    [Fact]
    public void Resize_with_a_negative_value_goes_the_other_way()
    {
        _fixture.Open(1);
        _fixture.Open(2);
        _fixture.Turn();
        _fixture.Desk.Focus(DeskFixture.W(1));

        Assert.True(_executor.Command("resize --width -5%").Success);
        _fixture.Turn();

        Assert.True(_fixture.FrameOf(1).Width < _fixture.FrameOf(2).Width);
    }

    [Fact]
    public void Toggle_fullscreen_goes_both_ways()
    {
        _fixture.Open(1);
        _fixture.Turn();
        _fixture.Desk.Focus(DeskFixture.W(1));

        _executor.Command("toggle-fullscreen");
        _fixture.Turn();
        Assert.Equal(new Rect(0, 0, 3840, 2160), _fixture.FrameOf(1));

        _executor.Command("toggle-fullscreen");
        _fixture.Turn();
        Assert.Equal(new Rect(0, 42, 3840, 2118), _fixture.FrameOf(1));
    }

    [Fact]
    public void Toggle_floating_goes_both_ways()
    {
        _fixture.Open(1);
        _fixture.Open(2);
        _fixture.Turn();
        _fixture.Desk.Focus(DeskFixture.W(2));

        _executor.Command("toggle-floating --centered=false");
        _fixture.Turn();
        Assert.Equal(WindowState.Floating, _fixture.Managed(2)!.State);

        _executor.Command("toggle-floating --centered=false");
        _fixture.Turn();
        Assert.Equal(WindowState.Tiling, _fixture.Managed(2)!.State);
    }

    [Fact]
    public void Set_minimized_is_handed_to_the_platform()
    {
        _fixture.Open(1);
        _fixture.Desk.Focus(DeskFixture.W(1));

        Assert.True(_executor.Command("set-minimized").Success);

        // Minimising is not something a model can do; it is a window message.
        Assert.Contains("minimize 1", _platform.Calls);
    }

    [Fact]
    public void Toggle_sticky_pins_the_window_to_its_monitor()
    {
        _fixture.Open(1);
        _fixture.Turn();
        _fixture.Desk.Focus(DeskFixture.W(1));

        Assert.True(_executor.Command("toggle-sticky").Success);

        Assert.True(_fixture.Managed(1)!.Sticky);
        Assert.Equal("main", _fixture.Managed(1)!.StickyMonitor);
    }

    [Fact]
    public void Move_workspace_in_direction_goes_to_the_next_one_along()
    {
        _fixture.Open(1);
        _fixture.Turn();
        _fixture.Desk.Focus(DeskFixture.W(1));

        Assert.True(_executor.Command("move --workspace-in-direction right").Success);

        Assert.Equal("12", _fixture.Managed(1)!.Workspace);
    }

    [Fact]
    public void An_unknown_command_is_refused_the_way_the_scripts_expect()
    {
        ExecResult result = _executor.Command("make-coffee");

        Assert.False(result.Success);
        Assert.Contains("unrecognized subcommand", result.Error);
    }

    [Fact]
    public void Commands_that_AkuWM_has_no_use_for_are_accepted_rather_than_refused()
    {
        _fixture.Open(1);
        _fixture.Desk.Focus(DeskFixture.W(1));

        // They exist in the grammar the scripts were written against; failing
        // them would fail a gesture that never needed them.
        Assert.True(_executor.Command("set-transparency --opacity 0.9").Success);
        Assert.True(_executor.Command("wm-redraw").Success);
    }

    [Fact]
    public void The_repair_that_moves_a_workspace_between_monitors_is_accepted_and_does_nothing()
    {
        _fixture.Open(1);
        Guid workspace = _fixture.Desk.Workspace("11")!.Id;

        // `lib-repair.ahk` sends this when it believes a workspace is on the
        // wrong monitor. Under AkuWM it cannot be, and the repair loop must
        // not fail on the attempt.
        ExecResult result = _executor.Command($"--id {workspace} move-workspace --direction right");

        Assert.True(result.Success);
        Assert.Equal("main", _fixture.Desk.Workspace("11")!.MonitorRole);
    }

    [Fact]
    public void The_monitors_query_matches_the_pattern_the_repair_reads_it_with()
    {
        _fixture.Open(1);
        _fixture.Turn();

        string json = Compact(_executor.Query("monitors").Data);

        // GlazeMonitors() out of lib-glaze.ahk, verbatim: one alternation
        // walked left to right, so the ORDER of the keys is the contract.
        // `deviceName` after the monitor's children, a workspace's
        // `isDisplayed` after its own, and the three workspace keys adjacent.
        const string pattern =
            "\"type\":\"monitor\""
            + "|\"type\":\"workspace\",\"id\":\"[^\"]+\",\"name\":\"([^\"]+)\""
            + "|\"isDisplayed\":(true|false)"
            + "|\"deviceName\":\"((?:[^\"\\\\]|\\\\.)*)\"";

        List<(string Device, List<(string Name, bool Displayed)> Workspaces)> monitors = [];

        foreach (Match match in Regex.Matches(json, pattern))
        {
            if (match.Value.Contains("\"type\":\"monitor\"", StringComparison.Ordinal))
            {
                monitors.Add((string.Empty, []));
            }
            else if (monitors.Count == 0)
            {
                continue;
            }
            else if (match.Groups[1].Success)
            {
                monitors[^1].Workspaces.Add((match.Groups[1].Value, false));
            }
            else if (match.Groups[2].Success && monitors[^1].Workspaces.Count > 0)
            {
                List<(string Name, bool Displayed)> found = monitors[^1].Workspaces;
                found[^1] = (found[^1].Name, match.Groups[2].Value == "true");
            }
            else if (match.Groups[3].Success && monitors[^1].Device.Length == 0)
            {
                monitors[^1] = (match.Groups[3].Value.Replace("\\\\", "\\", StringComparison.Ordinal),
                    monitors[^1].Workspaces);
            }
        }

        Assert.Equal(2, monitors.Count);

        // The backslashes survive the JSON escaping the script undoes by hand.
        Assert.Equal(@"\\.\DISPLAY2", monitors[0].Device);
        Assert.Equal(@"\\.\DISPLAY1", monitors[1].Device);

        Assert.Equal(["11", "12", "13"], monitors[0].Workspaces.Select(w => w.Name));
        Assert.Single(monitors[0].Workspaces, w => w.Displayed);
        Assert.Equal("11", monitors[0].Workspaces.First(w => w.Displayed).Name);
        Assert.Single(monitors[1].Workspaces, w => w.Displayed);
    }

    [Fact]
    public void The_app_answers_who_it_is()
    {
        JsonNode? data = _executor.Query("app-metadata").Data;

        Assert.Equal("AkuWM", data!["name"]!.GetValue<string>());
    }
}
