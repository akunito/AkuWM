using System.Text.Json.Nodes;
using AkuWM.Gui.Sections;
using Xunit;

namespace AkuWM.Gui.Tests;

public class SectionLogicTests
{
    private static List<JsonObject> Items(string json) =>
        ((JsonArray)JsonNode.Parse(json)!).Select(n => (JsonObject)n!).ToList();

    [Fact]
    public void Shortcuts_conflicts_are_by_autohotkey_chord_and_scope()
    {
        List<JsonObject> items = Items("""
            [
              { "id": "a", "keys": "Hyper+W", "enabled": true },
              { "id": "b", "keys": "Hyper+w", "enabled": true },
              { "id": "c", "keys": "Hyper+W", "enabled": false },
              { "id": "d", "keys": "Ctrl+Alt+C", "enabled": true, "when": { "process": "WindowsTerminal" } },
              { "id": "e", "keys": "Ctrl+Alt+C", "enabled": true }
            ]
            """);
        Dictionary<string, List<string>> conflicts = ShortcutsSection.Conflicts(items);
        Assert.Equal(["a", "b"], conflicts["^!#w"]);
        Assert.Equal(["d"], conflicts["^!c@windowsterminal"]);
        Assert.Equal(["e"], conflicts["^!c"]);
    }

    [Fact]
    public void Free_hyper_keys_leave_out_the_bound_ones()
    {
        List<string> free = ShortcutsSection.FreeHyperKeys(Items("""[ { "keys": "Hyper+W" }, { "keys": "Hyper+Shift+W" }, { "keys": "Hyper+1" } ]"""));
        Assert.DoesNotContain("Hyper+W", free);
        Assert.DoesNotContain("Hyper+Shift+W", free);
        Assert.DoesNotContain("Hyper+1", free);
        Assert.Contains("Hyper+Shift+1", free);
        Assert.Contains("Hyper+A", free);
    }

    [Fact]
    public void Cheat_sheet_groups_by_category_and_skips_disabled()
    {
        string sheet = ShortcutsSection.CheatSheet(Items("""
            [
              { "keys": "Hyper+L", "name": "Telegram", "category": "Apps", "enabled": true },
              { "keys": "Hyper+Q", "command": "workspace prev", "category": "Workspaces", "enabled": true },
              { "keys": "Hyper+X", "name": "gone", "category": "Apps", "enabled": false }
            ]
            """));
        Assert.Contains("## Apps\nHyper+L  Telegram\n", sheet);
        Assert.Contains("## Workspaces\nHyper+Q  workspace prev\n", sheet);
        Assert.DoesNotContain("gone", sheet);
    }

    [Fact]
    public void Rule_match_summary_reads_the_alternatives()
    {
        var rule = (JsonObject)JsonNode.Parse("""{ "match": [ { "process": "Telegram" }, { "class": "ApplicationFrameWindow", "title": "Calculator" } ] }""")!;
        Assert.Equal("process=Telegram | class=ApplicationFrameWindow title=Calculator", RulesSection.MatchSummary(rule));
        Assert.Equal("(matches nothing)", RulesSection.MatchSummary(new JsonObject()));
    }

    [Fact]
    public void Rule_facts_strip_the_exe()
    {
        var window = (JsonObject)JsonNode.Parse("""{ "processName": "Telegram.exe", "className": "Qt", "title": "Chats" }""")!;
        Assert.Equal(new AkuWM.Core.Matching.WindowFacts("Telegram", "Qt", "Chats"), RulesSection.Facts(window));
    }

    [Fact]
    public void Winget_table_is_cut_at_the_header_columns()
    {
        const string output = """
            Name                     Id                         Version        Available   Source
            --------------------------------------------------------------------------------------
            7-Zip 24.08 (x64)        7zip.7zip                  24.08                      winget
            Alacritty                Alacritty.Alacritty        0.15.1         0.16.0      winget
            Some app with no source  Local.Thing                1.0
            """;
        WingetTable table = WingetTable.Parse(output);
        Assert.Equal(3, table.Rows.Count);
        Assert.Equal(new WingetRow("7-Zip 24.08 (x64)", "7zip.7zip", "24.08", string.Empty), table.Rows[0]);
        Assert.Equal("0.16.0", table.Find("alacritty.alacritty")!.Available);
        Assert.Equal("1.0", table.Rows[2].Version);
        Assert.Empty(WingetTable.Parse("nothing here").Rows);
    }

    [Fact]
    public void Catalogue_lists_package_ids_sorted()
    {
        const string json = """{ "Sources": [ { "Packages": [ { "PackageIdentifier": "Zed.Zed" }, { "PackageIdentifier": "7zip.7zip" } ] } ] }""";
        Assert.Equal(["7zip.7zip", "Zed.Zed"], AppsSection.Catalogue(json));
    }

    [Fact]
    public void Monitor_bounds_parse_the_rect_text()
    {
        Assert.Equal(new AkuWM.Core.Model.Rect(-1920, -706, 1920, 1080), MonitorsSection.ParseBounds("-1920,-706 1920x1080"));
        Assert.Null(MonitorsSection.ParseBounds("nope"));
    }

    [Fact]
    public void Compat_monitors_are_found_under_data_or_bare()
    {
        Assert.Single(WindowsSection.Monitors(JsonNode.Parse("""{ "data": { "monitors": [ { "id": "m" } ] } }""")));
        Assert.Single(WindowsSection.Monitors(JsonNode.Parse("""{ "monitors": [ { "id": "m" } ] }""")));
        Assert.Single(WindowsSection.Monitors(JsonNode.Parse("""[ { "id": "m" } ]""")));
        Assert.Empty(WindowsSection.Monitors(null));
    }

    [Fact]
    public void Log_filter_keeps_matching_lines()
    {
        Assert.Equal("b DBG x\n", LogSection.Filtered("a INF y\nb DBG x\n", "dbg"));
        Assert.Equal("all", LogSection.Filtered("all", string.Empty));
    }
}
