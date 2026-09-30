using System.Text.Json.Nodes;
using AkuWM.Core.Config;
using AkuWM.Core.Profiles;
using Xunit;

namespace AkuWM.Tests;

/// <summary>sway-apps' profiles tests, ported: DESK (a) against X13 (b), X13 being this machine.</summary>
public class ProfilesTests : IDisposable
{
    private readonly string _dir;
    private readonly ConfigPaths _paths;
    private DateTimeOffset _now = new(2026, 9, 7, 12, 0, 0, TimeSpan.Zero);
    private readonly Profiles _profiles;

    private const string Common = """
        { "version": 1,
          "rules": [ { "id": "r-common", "name": "kitty", "match": [ { "process": "kitty" } ], "actions": [ "float" ] } ],
          "startup": [ { "id": "s-common", "name": "Mako", "command": "mako" } ],
          "shortcuts": [ { "id": "k-common", "keys": "Hyper+A", "name": "A", "command": "a" } ],
          "tools": [ { "id": "t-common", "name": "Files", "command": "explorer" } ],
          "monitors": [],
          "nodes": [ { "id": "NAS", "ssh": "akunito@192.168.20.200" } ] }
        """;

    private const string Desk = """
        { "version": 1,
          "rules": [ { "id": "r1", "name": "code", "match": [ { "process": "code" } ], "actions": [ "tile" ], "updated_at": 10 },
                     { "id": "r2", "name": "x", "match": [ { "process": "x" } ], "actions": [ "float" ] } ],
          "startup": [ { "id": "s1", "name": "Waybar", "command": "waybar" }, { "id": "s2", "name": "Foo", "command": "foo" } ],
          "shortcuts": [ { "id": "k2", "keys": "Hyper+B", "name": "B" }, { "id": "k3", "keys": "Hyper+C", "name": "C-desk" } ],
          "tools": [ { "id": "t1", "name": "Audio", "command": "pavucontrol", "updated_at": 5 } ],
          "monitors": [ { "id": "main", "match": { "edid": "S" }, "primary": true }, { "id": "second", "match": { "edid": "D" }, "orientation": "vertical" } ],
          "nodes": [ { "id": "VPS", "ssh": "akunito@100.64.0.6:56777" }, { "id": "LOCAL" } ] }
        """;

    private const string X13 = """
        { "version": 1,
          "rules": [ { "id": "r1", "name": "code", "match": [ { "process": "code" } ], "actions": [ "tile" ], "updated_at": 99 },
                     { "id": "r3", "name": "mako", "match": [ { "process": "mako" } ], "actions": [] } ],
          "startup": [ { "id": "s1", "name": "Waybar", "command": "waybar -b top" }, { "id": "s3", "name": "Bar", "command": "bar" } ],
          "shortcuts": [ { "id": "k3", "keys": "Hyper+C", "name": "C-x13" }, { "id": "k4", "keys": "Hyper+D", "name": "D" } ],
          "tools": [ { "id": "t1", "name": "Audio", "command": "pavucontrol", "updated_at": 7 } ],
          "monitors": [ { "id": "main", "match": { "edid": "L" }, "primary": true } ],
          "nodes": [ { "id": "VPS", "ssh": "akunito@100.64.0.6:56777" } ] }
        """;

    private static readonly Dictionary<string, (string[] OnlyA, string[] OnlyB, string[] Changed, string[] Same)> Expected = new()
    {
        ["rules"] = (["r2"], ["r3"], [], ["r1"]),
        ["startup"] = (["s2"], ["s3"], ["s1"], []),
        ["shortcuts"] = (["k2"], ["k4"], ["k3"], []),
        ["tools"] = ([], [], [], ["t1"]),
        ["monitors"] = (["second"], [], ["main"], []),
        ["nodes"] = (["LOCAL"], [], [], ["VPS"]),
        ["workspaces"] = ([], [], [], []),
    };

    public ProfilesTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "akuwm-profiles-" + Guid.NewGuid().ToString("N"));
        string config = Path.Combine(_dir, "akuwm");
        Directory.CreateDirectory(config);
        _paths = new ConfigPaths(config, "X13", Path.Combine(_dir, "state"));
        File.WriteAllText(Path.Combine(config, "common.json"), Common);
        File.WriteAllText(Path.Combine(config, "DESK.json"), Desk);
        File.WriteAllText(Path.Combine(config, "X13.json"), X13);
        _profiles = new Profiles(_paths, () => _now);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string FileOf(string layer) => Path.Combine(_paths.ConfigDir, layer + ".json");

    private JsonObject ReadLayer(string layer) => Profiles.Read(FileOf(layer));

    private static string[] Ids(IEnumerable<JsonObject> items) => items.Select(i => Profiles.IdOf(i)!).ToArray();

    [Fact]
    public void Diff_every_section_kind()
    {
        foreach ((string section, (string[] onlyA, string[] onlyB, string[] changed, string[] same)) in Expected)
        {
            SectionDiff d = _profiles.Diff("DESK", "X13", section);
            Assert.Equal(onlyA, Ids(d.OnlyA));
            Assert.Equal(onlyB, Ids(d.OnlyB));
            Assert.Equal(changed, d.Changed.Select(c => Profiles.IdOf(c.A)!).ToArray());
            Assert.Equal(changed, d.Changed.Select(c => Profiles.IdOf(c.B)!).ToArray());
            Assert.Equal(same, Ids(d.Same));
        }

        (JsonObject a, JsonObject b) = _profiles.Diff("DESK", "X13", "shortcuts").Changed[0];
        Assert.Equal(("C-desk", "C-x13"), (a["name"]!.ToString(), b["name"]!.ToString()));

        SectionDiff rev = _profiles.Diff("X13", "DESK", "monitors");
        Assert.Empty(rev.OnlyA);
        Assert.Equal(["second"], Ids(rev.OnlyB));

        SectionDiff none = _profiles.Diff("DESK", "NOPE", "nodes");
        Assert.Equal(["VPS", "LOCAL"], Ids(none.OnlyA));
        Assert.Empty(none.OnlyB);
        Assert.False(File.Exists(FileOf("NOPE")));
    }

    [Fact]
    public void Diff_ignores_an_updated_at_only_change()
    {
        Assert.Equal(["r1"], Ids(_profiles.Diff("DESK", "X13", "rules").Same));
        Assert.Equal(["t1"], Ids(_profiles.Diff("DESK", "X13", "tools").Same));

        JsonObject x13 = ReadLayer("X13");
        ((JsonObject)x13["rules"]![0]!)["actions"] = new JsonArray("float");
        File.WriteAllText(FileOf("X13"), x13.ToJsonString());
        SectionDiff rules = _profiles.Diff("DESK", "X13", "rules");
        Assert.Equal(["r1"], rules.Changed.Select(c => Profiles.IdOf(c.A)!).ToArray());
        Assert.Empty(rules.Same);
    }

    [Fact]
    public void Summary_counts_match_the_diffs_and_a_missing_profile_is_all_only_a()
    {
        var summary = _profiles.Summary("DESK", "X13");
        Assert.Equal(Profiles.Sections, summary.Keys.ToArray());
        foreach ((string section, (string[] onlyA, string[] onlyB, string[] changed, string[] same)) in Expected)
        {
            Assert.Equal((onlyA.Length, onlyB.Length, changed.Length, same.Length), summary[section]);
        }

        var nothing = _profiles.Summary("DESK", "NOPE");
        Assert.Equal((2, 0, 0, 0), nothing["rules"]);
        Assert.Equal((2, 0, 0, 0), nothing["monitors"]);
        Assert.Equal((2, 0, 0, 0), nothing["nodes"]);
    }

    [Fact]
    public void Label_formats()
    {
        JsonObject desk = ReadLayer("DESK");
        Assert.Equal("Hyper+B → B", Profiles.Label("shortcuts", (JsonObject)desk["shortcuts"]![0]!));
        Assert.Equal("Hyper+Z → foo", Profiles.Label("shortcuts", new JsonObject { ["id"] = "k", ["keys"] = "Hyper+Z", ["command"] = "foo" }));
        Assert.Equal("? → ", Profiles.Label("shortcuts", new JsonObject { ["id"] = "k" }));
        Assert.Equal("Waybar (waybar)", Profiles.Label("startup", (JsonObject)desk["startup"]![0]!));
        Assert.Equal("Audio", Profiles.Label("tools", (JsonObject)desk["tools"]![0]!));
        Assert.Equal("t9", Profiles.Label("tools", new JsonObject { ["id"] = "t9" }));
        Assert.Equal("main = S", Profiles.Label("monitors", (JsonObject)desk["monitors"]![0]!));
        Assert.Equal("second = D (vertical)", Profiles.Label("monitors", (JsonObject)desk["monitors"]![1]!));
        Assert.Equal("VPS akunito@100.64.0.6:56777", Profiles.Label("nodes", (JsonObject)desk["nodes"]![0]!));
        Assert.Equal("LOCAL local", Profiles.Label("nodes", (JsonObject)desk["nodes"]![1]!));
        Assert.Equal("code", Profiles.Label("rules", (JsonObject)desk["rules"]![0]!));
        Assert.Equal("r9", Profiles.Label("rules", new JsonObject { ["id"] = "r9" }));
    }

    [Fact]
    public void Copy_selected_ids_merges_without_touching_others()
    {
        byte[] deskBefore = File.ReadAllBytes(FileOf("DESK"));
        CopyResult result = _profiles.CopyItems("DESK", "X13", "shortcuts", ids: ["k2"]);
        Assert.Equal(["k2"], result.Copied);
        Assert.Equal(("shortcuts", "DESK", "X13", 3), (result.Section, result.From, result.To, result.DestinationTotal));
        JsonObject x13 = ReadLayer("X13");
        Assert.Equal(["k3", "k4", "k2"], Ids(Profiles.Items(x13, "shortcuts")));
        Assert.Equal("C-x13", x13["shortcuts"]![0]!["name"]!.ToString());
        Assert.Equal(deskBefore, File.ReadAllBytes(FileOf("DESK")));
        Assert.Equal(["r1", "r3"], Ids(Profiles.Items(x13, "rules")));

        // an existing id is overwritten in place
        _profiles.CopyItems("DESK", "X13", "shortcuts", ids: ["k3"]);
        x13 = ReadLayer("X13");
        Assert.Equal(["k3", "k4", "k2"], Ids(Profiles.Items(x13, "shortcuts")));
        Assert.Equal("C-desk", x13["shortcuts"]![0]!["name"]!.ToString());
    }

    [Fact]
    public void Copy_replace_drops_unlisted_destination_items()
    {
        _profiles.CopyItems("DESK", "X13", "startup", ids: ["s2"], replace: true);
        Assert.Equal(["s2"], Ids(Profiles.Items(ReadLayer("X13"), "startup")));
        _profiles.CopyItems("DESK", "X13", "startup", replace: true);
        Assert.Equal(["s1", "s2"], Ids(Profiles.Items(ReadLayer("X13"), "startup")));
        Assert.Equal("waybar", ReadLayer("X13")["startup"]![0]!["command"]!.ToString());
    }

    [Fact]
    public void Copy_common_to_profile_and_profile_to_profile()
    {
        _profiles.CopyItems("common", "X13", "tools");
        Assert.Equal(["t1", "t-common"], Ids(Profiles.Items(ReadLayer("X13"), "tools")));
        _profiles.CopyItems("X13", "DESK", "rules", ids: ["r3"]);
        Assert.Equal(["r1", "r2", "r3"], Ids(Profiles.Items(ReadLayer("DESK"), "rules")));
        // a destination that does not exist yet is created with the schema version
        _profiles.CopyItems("DESK", "NEW", "nodes");
        JsonObject created = ReadLayer("NEW");
        Assert.Equal(1, created["version"]!.GetValue<int>());
        Assert.Equal(["VPS", "LOCAL"], Ids(Profiles.Items(created, "nodes")));
    }

    [Fact]
    public void Copy_stamps_updated_at_on_the_copied_items_only()
    {
        _now = new DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.Zero);
        _profiles.CopyItems("DESK", "X13", "shortcuts", ids: ["k2"]);
        JsonObject x13 = ReadLayer("X13");
        JsonObject k2 = Profiles.Items(x13, "shortcuts").First(i => Profiles.IdOf(i) == "k2");
        Assert.Equal(_now.ToUnixTimeSeconds(), k2["updated_at"]!.GetValue<long>());
        JsonObject k4 = Profiles.Items(x13, "shortcuts").First(i => Profiles.IdOf(i) == "k4");
        Assert.Null(k4["updated_at"]);
        // the source keeps its own stamp
        Assert.Null(Profiles.Items(ReadLayer("DESK"), "shortcuts")[0]["updated_at"]);
    }

    [Fact]
    public void Copy_and_restore_take_a_snapshot_first()
    {
        Assert.Empty(_profiles.Snapshots());
        CopyResult result = _profiles.CopyItems("DESK", "X13", "shortcuts", ids: ["k2"]);
        List<SnapshotInfo> snaps = _profiles.Snapshots();
        Assert.Single(snaps);
        Assert.Equal(result.Snapshot, snaps[0].Id);
        Assert.Equal("copy-shortcuts-DESK-to-X13", snaps[0].Reason);
        // the snapshot holds X13 as it was BEFORE the copy
        JsonObject before = Profiles.Read(Path.Combine(snaps[0].Path, "X13.json"));
        Assert.Equal(["k3", "k4"], Ids(Profiles.Items(before, "shortcuts")));

        _now = _now.AddSeconds(5);
        _profiles.Restore(snaps[0].Id);
        snaps = _profiles.Snapshots();
        Assert.Equal(2, snaps.Count);
        Assert.StartsWith("before-restore-", snaps[1].Reason);
        Assert.Equal(["k3", "k4"], Ids(Profiles.Items(ReadLayer("X13"), "shortcuts")));
    }

    [Fact]
    public void Snapshot_dir_name_and_meta()
    {
        string id = _profiles.Snapshot("manual");
        Assert.Equal("20260907-120000-X13-manual", id);
        string dir = Path.Combine(_profiles.SnapshotDir, id);
        Assert.Equal(["DESK.json", "META.json", "X13.json", "common.json"], Directory.EnumerateFiles(dir).Select(f => Path.GetFileName(f)!).OrderBy(n => n, StringComparer.Ordinal).ToArray());
        SnapshotInfo info = _profiles.Snapshots().Single();
        Assert.Equal(("manual", "X13", _now.ToUnixTimeSeconds()), (info.Reason, info.Profile, info.Time));
        Assert.Equal(["DESK.json", "X13.json", "common.json"], info.Files);
        Assert.Equal(File.ReadAllText(FileOf("DESK")), File.ReadAllText(Path.Combine(dir, "DESK.json")));
    }

    [Fact]
    public void Two_snapshots_in_one_second_get_distinct_names()
    {
        string first = _profiles.Snapshot("x");
        string second = _profiles.Snapshot("x");
        Assert.Equal("20260907-120000-X13-x", first);
        Assert.Equal("20260907-120000-2-X13-x", second);
        Assert.Equal(2, _profiles.Snapshots().Count);
    }

    [Fact]
    public void Snapshot_reason_is_sanitised()
    {
        Assert.Equal("20260907-120000-X13-copy-shortcuts-DESK--to-X13", _profiles.Snapshot("copy shortcuts/DESK->to:X13"));
        _now = _now.AddSeconds(1);
        Assert.EndsWith("-X13-manual", _profiles.Snapshot("///"));
        _now = _now.AddSeconds(1);
        string longer = _profiles.Snapshot(new string('a', 60) + "!!!");
        Assert.EndsWith("-X13-" + new string('a', 40), longer);
    }

    [Fact]
    public void Prune_keeps_thirty_oldest_first()
    {
        for (int i = 0; i < 33; i++)
        {
            _now = _now.AddSeconds(1);
            _profiles.Snapshot("s");
        }

        List<SnapshotInfo> kept = _profiles.Snapshots();
        Assert.Equal(30, kept.Count);
        Assert.Equal("20260907-120004-X13-s", kept[0].Id);
        Assert.Equal("20260907-120033-X13-s", kept[^1].Id);
        Assert.Empty(_profiles.Prune());
        List<string> removed = _profiles.Prune(keep: 2);
        Assert.Equal(28, removed.Count);
        Assert.Equal(2, _profiles.Snapshots().Count);
    }

    [Fact]
    public void Restore_all_files_one_file_and_one_section()
    {
        string id = _profiles.Snapshot("before");
        _now = _now.AddSeconds(1);
        JsonObject desk = ReadLayer("DESK");
        desk["shortcuts"] = new JsonArray();
        desk["rules"] = new JsonArray();
        File.WriteAllText(FileOf("DESK"), desk.ToJsonString());
        JsonObject x13 = ReadLayer("X13");
        x13["tools"] = new JsonArray();
        File.WriteAllText(FileOf("X13"), x13.ToJsonString());

        List<string> touched = _profiles.Restore(id, files: ["DESK.json"], sections: ["shortcuts"]);
        Assert.Equal(["DESK.json"], touched);
        desk = ReadLayer("DESK");
        Assert.Equal(["k2", "k3"], Ids(Profiles.Items(desk, "shortcuts")));
        Assert.Empty(Profiles.Items(desk, "rules"));
        Assert.Empty(Profiles.Items(ReadLayer("X13"), "tools"));

        _now = _now.AddSeconds(1);
        touched = _profiles.Restore(id, files: ["X13.json"]);
        Assert.Equal(["X13.json"], touched);
        Assert.Equal(["t1"], Ids(Profiles.Items(ReadLayer("X13"), "tools")));

        _now = _now.AddSeconds(1);
        touched = _profiles.Restore(id);
        Assert.Equal(["DESK.json", "X13.json", "common.json"], touched);
        Assert.Equal(["r1", "r2"], Ids(Profiles.Items(ReadLayer("DESK"), "rules")));
    }

    [Fact]
    public void Restore_of_an_unknown_snapshot_throws_and_a_unique_fragment_resolves()
    {
        Assert.Throws<FileNotFoundException>(() => _profiles.Restore("nope"));
        string id = _profiles.Snapshot("only");
        Assert.Equal(Path.Combine(_profiles.SnapshotDir, id), _profiles.SnapshotPath("X13-only"));
        _now = _now.AddSeconds(1);
        _profiles.Snapshot("only-too");
        Assert.Throws<FileNotFoundException>(() => _profiles.SnapshotPath("X13-only"));
    }

    [Fact]
    public void Diff_snapshot_counts()
    {
        string id = _profiles.Snapshot("s");
        JsonObject desk = ReadLayer("DESK");
        var shortcuts = (JsonArray)desk["shortcuts"]!;
        shortcuts.RemoveAt(0); // k2 gone
        ((JsonObject)shortcuts[0]!)["name"] = "changed"; // k3 changed
        shortcuts.Add(new JsonObject { ["id"] = "k9", ["keys"] = "Hyper+Z" }); // k9 new
        File.WriteAllText(FileOf("DESK"), desk.ToJsonString());
        var d = _profiles.DiffSnapshot(id);
        Assert.Equal((1, 1, 1), d["DESK.json"]["shortcuts"]);
        Assert.Equal((0, 0, 0), d["DESK.json"]["rules"]);
        Assert.Equal((0, 0, 0), d["X13.json"]["shortcuts"]);
    }

    [Fact]
    public void Files_include_the_snapshot_folder_only_when_present()
    {
        List<string> files = _profiles.Files();
        Assert.Equal(3, files.Count);
        Assert.DoesNotContain(_profiles.SnapshotDir, files);
        _profiles.Snapshot("s");
        Assert.Contains(_profiles.SnapshotDir, _profiles.Files());
    }
}
