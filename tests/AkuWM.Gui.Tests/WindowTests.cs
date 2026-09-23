using System.Text.Json.Nodes;
using AkuWM.Gui;
using AkuWM.Gui.Sections;
using AkuWM.Gui.Services;
using AkuWM.Gui.Shell;
using Avalonia.Headless.XUnit;
using Xunit;

namespace AkuWM.Gui.Tests;

public class WindowTests
{
    [AvaloniaFact]
    public void Every_section_opens()
    {
        using var f = new Fixture();
        var window = new MainWindow(f.Services);
        window.Show();
        foreach (Section section in window.Sections)
        {
            window.ShowSection(section.Key);
            Assert.Same(section, window.Current);
            Assert.True(section.View.IsVisible, section.Key);
        }

        Assert.Equal(LaunchArgs.Sections, window.Sections.Select(s => s.Key));
    }

    [AvaloniaFact]
    public void A_rule_saved_in_the_editor_is_in_the_file_and_the_daemon_is_told()
    {
        using var f = new Fixture();
        var window = new MainWindow(f.Services);
        window.Show();
        window.ShowSection("rules");
        var rules = (RulesSection)window.Current!;
        Assert.Equal(3, rules.Items.Count);

        var draft = new JsonObject
        {
            ["name"] = "Calculator",
            ["enabled"] = true,
            ["match"] = new JsonArray(new JsonObject { ["class"] = "ApplicationFrameWindow", ["title"] = "Calculator" }),
            ["actions"] = new JsonArray("float", "sticky"),
        };
        rules.Edit(draft, "common");
        Assert.Null(rules.Save());

        JsonArray written = (JsonArray)f.CommonJson()["rules"]!;
        Assert.Equal(3, written.Count);
        Assert.Equal("Calculator", written[2]!["name"]!.ToString());
        Assert.Contains("compat command wm-reload-config", f.Daemon.Sent);
        Assert.Contains("saved and applied", f.Toasts);
        Assert.Equal(4, rules.Items.Count);

        Assert.Null(rules.Delete());
        Assert.Equal(2, ((JsonArray)f.CommonJson()["rules"]!).Count);
    }

    [AvaloniaFact]
    public void An_edit_in_the_profile_layer_stays_there()
    {
        using var f = new Fixture();
        var window = new MainWindow(f.Services);
        window.Show();
        window.ShowSection("rules");
        var rules = (RulesSection)window.Current!;
        (JsonObject telegram, string layer) = rules.Items[0];
        Assert.Equal("profile", layer);
        JsonObject draft = ConfigService.Clone(telegram);
        draft["enabled"] = true;
        rules.Edit(draft, layer);
        Assert.Null(rules.Save());
        JsonArray profile = (JsonArray)f.ProfileJson()["rules"]!;
        Assert.True(profile[0]!["enabled"]!.GetValue<bool>());
        Assert.Equal("Telegram", profile[0]!["name"]!.ToString());
        Assert.Equal("kept", ((JsonArray)f.CommonJson()["rules"]!)[0]!["future_key"]!.ToString());
    }

    [AvaloniaFact]
    public void The_rule_tester_uses_the_engines_matcher_over_the_daemons_windows()
    {
        using var f = new Fixture();
        f.Daemon.Answers("query windows --all", """{ "windows": [ { "processName": "Telegram.exe", "className": "Qt", "title": "Chats" }, { "processName": "zen.exe", "className": "MozillaWindowClass", "title": "Zen" } ] }""");
        var rules = new RulesSection(f.Services);
        var draft = new JsonObject { ["match"] = new JsonArray(new JsonObject { ["process"] = "re:^tele" }), ["actions"] = new JsonArray("float") };
        string result = rules.TestAgainstOpenWindows(draft);
        Assert.StartsWith("matches 1 of 2", result);
        Assert.Contains("Telegram.exe", result);
    }

    [AvaloniaFact]
    public void Toggle_hides_and_shows_and_a_section_launch_switches()
    {
        using var f = new Fixture();
        var window = new MainWindow(f.Services);
        window.Handle(new LaunchArgs(null, null, true, false, null));
        Assert.True(window.IsVisible);
        window.Handle(new LaunchArgs("log", null, false, false, null));
        Assert.Equal("log", window.Current!.Key);
        window.Hide();
        window.Handle(new LaunchArgs("doctor", null, true, false, null));
        Assert.True(window.IsVisible);
        Assert.Equal("doctor", window.Current!.Key);
        window.Handle(new LaunchArgs(null, null, false, true, null));
        Assert.True(window.IsVisible);
    }

    [AvaloniaFact]
    public void Closing_hides_unless_quitting()
    {
        using var f = new Fixture();
        var window = new MainWindow(f.Services);
        window.Show();
        window.Close();
        Assert.False(window.IsVisible);
        window.Show();
        window.Quitting = true;
        window.Close();
        Assert.False(window.IsVisible);
    }

    [AvaloniaFact]
    public void Startup_moves_within_its_layer_and_runs_now()
    {
        using var f = new Fixture();
        var window = new MainWindow(f.Services);
        window.Show();
        window.ShowSection("startup");
        var startup = (StartupSection)window.Current!;
        (JsonObject b, string layer) = startup.Items[1];
        startup.Edit(ConfigService.Clone(b), layer);
        Assert.Null(startup.Move(startup.Draft!, -1));
        Assert.Equal(["u-b", "u-a"], ((JsonArray)f.CommonJson()["startup"]!).Select(s => s!["id"]!.ToString()));
        Assert.Equal("u-b", ConfigService.IdOf(startup.Items[0].Item));
    }

    [AvaloniaFact]
    public void Windows_renders_the_compat_tree_and_doctor_its_checks()
    {
        using var f = new Fixture();
        f.Daemon.Answers("doctor", """{ "ok": false, "failures": 1, "warnings": 0, "checks": [ { "name": "pipe", "status": "ok", "detail": "ours" }, { "name": "hooks", "status": "fail", "detail": "dead" } ] }""");
        var window = new MainWindow(f.Services);
        window.Show();
        window.ShowSection("doctor");
        Assert.Contains("doctor", f.Daemon.Sent);

        var windows = (WindowsSection)window.SectionOf("windows");
        windows.Render(WindowsSection.Monitors(JsonNode.Parse("""
            { "monitors": [ { "deviceName": "\\\\.\\DISPLAY1", "width": 3840, "height": 2160, "x": 0, "y": 0, "scaleFactor": 1.5,
              "children": [ { "name": "11", "isDisplayed": true, "hasFocus": true, "tilingDirection": "horizontal",
                "children": [ { "id": "w1", "title": "Zen", "processName": "zen", "hasFocus": true, "state": { "type": "tiling" } } ] } ] } ] }
            """)));
        Assert.True(windows.View.IsVisible);
    }

    [AvaloniaFact]
    public void Log_switch_asks_the_daemon_or_sets_the_marker()
    {
        using var f = new Fixture();
        var log = new LogSection(f.Services);
        log.SetDebug(true);
        Assert.Contains("debug on", f.Daemon.Sent);
        f.Daemon.Running = false;
        log.SetDebug(true);
        Assert.True(File.Exists(f.Paths.DebugMarkerFile));
        log.SetDebug(false);
        Assert.False(File.Exists(f.Paths.DebugMarkerFile));
    }

    [AvaloniaFact]
    public void Tools_launch_through_the_starter()
    {
        using var f = new Fixture();
        var window = new MainWindow(f.Services);
        window.Show();
        window.ShowSection("tools");
        var tools = (ToolsSection)window.Current!;
        Assert.Single(tools.Items);
        Assert.Equal(1, tools.Launchers);
        tools.Edit(ConfigService.Clone(tools.Items[0].Item), "common");
        Assert.Equal("Display settings", ConfigService.Str(tools.Draft!, "name"));
        Assert.Null(f.Services.Start(ConfigService.Str(tools.Draft!, "command")));
        Assert.Equal(["ms-settings:display"], f.Started);
    }
}
