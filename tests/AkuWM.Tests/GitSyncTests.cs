using System.Diagnostics;
using System.Text.Json.Nodes;
using AkuWM.Core.Config;
using AkuWM.Core.Git;
using Xunit;

namespace AkuWM.Tests;

/// <summary>The id-keyed three-way merge, without a repository.</summary>
public class GitMergeTests
{
    private static JsonObject Rule(string id, string action, long t = 0) => new()
    {
        ["id"] = id,
        ["name"] = id,
        ["match"] = new JsonArray(new JsonObject { ["process"] = id }),
        ["actions"] = new JsonArray(action),
        ["enabled"] = true,
        ["updated_at"] = t,
    };

    private static JsonArray List(params JsonObject[] items) => new([.. items.Select(i => (JsonNode)i.DeepClone())]);

    private static string[] Ids(JsonArray a) => a.Select(n => n!["id"]!.ToString()).ToArray();

    private static string Action(JsonArray a, int i) => a[i]!["actions"]![0]!.ToString();

    [Fact]
    public void Both_edited_newest_updated_at_wins_tie_ours()
    {
        JsonArray b = List(Rule("a", "base", 0));
        Assert.Equal("theirs", Action(GitSync.MergeSection(b, List(Rule("a", "ours", 10)), List(Rule("a", "theirs", 20))), 0));
        Assert.Equal("ours", Action(GitSync.MergeSection(b, List(Rule("a", "ours", 30)), List(Rule("a", "theirs", 20))), 0));
        Assert.Equal("ours", Action(GitSync.MergeSection(b, List(Rule("a", "ours", 10)), List(Rule("a", "theirs", 10))), 0));
        JsonArray noStamps = GitSync.MergeSection(
            new JsonArray(new JsonObject { ["id"] = "a", ["v"] = 0 }),
            new JsonArray(new JsonObject { ["id"] = "a", ["v"] = 1 }),
            new JsonArray(new JsonObject { ["id"] = "a", ["v"] = 2 }));
        Assert.Equal(1, noStamps[0]!["v"]!.GetValue<int>());
    }

    [Fact]
    public void One_sided_delete_wins_over_untouched()
    {
        JsonArray b = List(Rule("a", "x"), Rule("b", "x"));
        Assert.Equal(["b"], Ids(GitSync.MergeSection(b, List(Rule("b", "x")), b)));
        Assert.Equal(["a"], Ids(GitSync.MergeSection(b, b, List(Rule("a", "x")))));
        Assert.Empty(GitSync.MergeSection(b, new JsonArray(), new JsonArray()));
    }

    [Fact]
    public void Delete_against_edit_keeps_the_edit()
    {
        JsonArray b = List(Rule("a", "x", 0));
        Assert.Equal("y", Action(GitSync.MergeSection(b, new JsonArray(), List(Rule("a", "y", 9))), 0));
        Assert.Equal("y", Action(GitSync.MergeSection(b, List(Rule("a", "y", 9)), new JsonArray()), 0));
        Assert.Equal("y", Action(GitSync.MergeSection(b, new JsonArray(), List(Rule("a", "y", 0))), 0));
    }

    [Fact]
    public void New_on_both_sides_both_kept_same_new_id_newest_wins()
    {
        Assert.Equal(["x", "y"], Ids(GitSync.MergeSection(new JsonArray(), List(Rule("x", "1")), List(Rule("y", "2")))));
        JsonArray b = List(Rule("a", "0"));
        Assert.Equal(["a", "x", "y"], Ids(GitSync.MergeSection(b, List(Rule("a", "0"), Rule("x", "1")), List(Rule("a", "0"), Rule("y", "2")))));
        JsonArray merged = GitSync.MergeSection(new JsonArray(), List(Rule("n", "ours", 1)), List(Rule("n", "theirs", 2)));
        Assert.Single(merged);
        Assert.Equal("theirs", Action(merged, 0));
    }

    [Fact]
    public void One_sided_edit_and_identical_edits()
    {
        JsonArray b = List(Rule("a", "x", 5));
        Assert.Equal("y", Action(GitSync.MergeSection(b, b, List(Rule("a", "y", 1))), 0));
        Assert.Equal("y", Action(GitSync.MergeSection(b, List(Rule("a", "y", 1)), b), 0));
        Assert.Single(GitSync.MergeSection(b, List(Rule("a", "y", 7)), List(Rule("a", "y", 7))));
        Assert.Single(GitSync.MergeSection(new JsonArray(), List(Rule("z", "q")), List(Rule("z", "q"))));
    }

    [Fact]
    public void Ordering_ours_first_then_theirs()
    {
        JsonArray b = List(Rule("a", "0"), Rule("b", "0"), Rule("c", "0"));
        JsonArray ours = List(Rule("c", "0"), Rule("a", "0"));
        JsonArray theirs = List(Rule("a", "0"), Rule("b", "0"), Rule("c", "0"), Rule("d", "0"));
        Assert.Equal(["c", "a", "d"], Ids(GitSync.MergeSection(b, ours, theirs)));
        Assert.Equal(["d"], Ids(GitSync.MergeSection(b, new JsonArray(), theirs)));
    }

    [Fact]
    public void Top_level_keys_of_a_merged_layer()
    {
        string ours = """{ "version": 1, "rules": [ { "id": "a", "actions": ["o"], "updated_at": 1 } ], "settings": { "a": 1, "b": 2 }, "custom": { "k": 1 } }""";
        string theirs = """{ "version": 3, "rules": [ { "id": "b", "actions": ["t"], "updated_at": 1 } ], "tools": [ { "id": "t1", "name": "T" } ], "settings": { "b": 9, "c": 3 } }""";
        string merged = GitSync.MergeStateJson(string.Empty, ours, theirs);
        Assert.EndsWith("\n", merged);
        var m = (JsonObject)JsonNode.Parse(merged)!;
        Assert.Equal(3, m["version"]!.GetValue<int>());
        Assert.Equal(1, m["custom"]!["k"]!.GetValue<int>());
        Assert.Equal((1, 2, 3), (m["settings"]!["a"]!.GetValue<int>(), m["settings"]!["b"]!.GetValue<int>(), m["settings"]!["c"]!.GetValue<int>()));
        Assert.Equal(["a", "b"], Ids((JsonArray)m["rules"]!));
        Assert.Equal("T", m["tools"]![0]!["name"]!.ToString());
        foreach (string section in GitSync.Sections)
        {
            Assert.IsType<JsonArray>(m[section]);
        }

        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(GitSync.MergeStateJson(ours, ours, ours)), JsonNode.Parse(GitSync.MergeStateJson(string.Empty, ours, ours))));
    }
}

/// <summary>Real repositories in a temporary directory: a bare origin and clones A and B. Skipped where git is not installed.</summary>
public class GitSyncRepoTests : IDisposable
{
    private readonly string _tmp;
    private readonly string _origin;
    private readonly string _a;
    private readonly string _b;
    private const string Rel = "templates/windows/DESK_W11/akuwm";

    public GitSyncRepoTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), "akuwm-git-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmp);
        string gitconfig = Path.Combine(_tmp, "gitconfig");
        File.WriteAllText(gitconfig, "[user]\n\tname = T\n\temail = t@example.invalid\n[init]\n\tdefaultBranch = main\n[commit]\n\tgpgsign = false\n[pull]\n\trebase = false\n");
        Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", gitconfig);
        Environment.SetEnvironmentVariable("GIT_CONFIG_NOSYSTEM", "1");
        _origin = Path.Combine(_tmp, "origin.git");
        _a = Path.Combine(_tmp, "A");
        _b = Path.Combine(_tmp, "B");
        Git(_tmp, "init", "--quiet", "--bare", "--initial-branch=main", _origin);
        Git(_tmp, "clone", "--quiet", _origin, _a);
        Write(Path.Combine(_a, Rel, "common.json"), Doc(rules: [Rule("x", "float"), Rule("y", "float")]));
        Write(Path.Combine(_a, Rel, "TEST.json"), Doc(shortcuts: [new JsonObject { ["id"] = "k1", ["keys"] = "Hyper+A", ["name"] = "A", ["updated_at"] = 0 }]));
        File.WriteAllText(Path.Combine(_a, "README.md"), "hello\n");
        Git(_a, "add", "-A");
        Git(_a, "commit", "--quiet", "-m", "init");
        Git(_a, "push", "--quiet", "-u", "origin", "main");
        Git(_tmp, "clone", "--quiet", _origin, _b);
    }

    public void Dispose()
    {
        // git writes its objects read-only; on Windows Directory.Delete
        // refuses those (UnauthorizedAccessException, CI 2026-09-30), and a
        // Dispose that throws fails the test that just passed.
        try
        {
            foreach (string file in Directory.EnumerateFiles(_tmp, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(_tmp, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static string Git(string cwd, params string[] args)
    {
        Run r = GitSync.Runner(args, cwd);
        return r.Code == 0 ? r.Out : throw new InvalidOperationException($"git {string.Join(' ', args)}: {r.Text}");
    }

    private static JsonObject Rule(string id, string action, long t = 0) => new()
    {
        ["id"] = id,
        ["name"] = id,
        ["match"] = new JsonArray(new JsonObject { ["process"] = id }),
        ["actions"] = new JsonArray(action),
        ["enabled"] = true,
        ["updated_at"] = t,
    };

    private static JsonObject Doc(JsonObject[]? rules = null, JsonObject[]? shortcuts = null)
    {
        var doc = new JsonObject { ["version"] = 1 };
        foreach (string s in GitSync.Sections)
        {
            doc[s] = new JsonArray();
        }

        doc["settings"] = new JsonObject();
        if (rules is not null)
        {
            doc["rules"] = new JsonArray([.. rules.Select(r => (JsonNode)r)]);
        }

        if (shortcuts is not null)
        {
            doc["shortcuts"] = new JsonArray([.. shortcuts.Select(r => (JsonNode)r)]);
        }

        return doc;
    }

    // Single-line JSON: any two edits to one file are a textual conflict, so the sync tests reach the merge.
    private static void Write(string path, JsonObject doc)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, doc.ToJsonString() + "\n");
    }

    private static JsonObject Read(string path) => (JsonObject)JsonNode.Parse(File.ReadAllText(path))!;

    private static GitSync Use(string clone) => new(new ConfigPaths(Path.Combine(clone, Rel), "TEST", Path.Combine(clone, "state")));

    private static string Head(string clone) => Git(clone, "rev-parse", "HEAD").Trim();

    private string OriginMain() => Git(_origin, "rev-parse", "main").Trim();

    private static void SetRule(string clone, string id, string action, long t)
    {
        string file = Path.Combine(clone, Rel, "common.json");
        JsonObject d = Read(file);
        var rules = (JsonArray)d["rules"]!;
        JsonObject? hit = rules.OfType<JsonObject>().FirstOrDefault(r => r["id"]!.ToString() == id);
        if (hit is not null)
        {
            hit["actions"] = new JsonArray(action);
            hit["updated_at"] = t;
        }
        else
        {
            rules.Add((JsonNode)Rule(id, action, t));
        }

        Write(file, d);
    }

    private static Dictionary<string, string> Actions(string clone) =>
        ((JsonArray)Read(Path.Combine(clone, Rel, "common.json"))["rules"]!).OfType<JsonObject>().ToDictionary(r => r["id"]!.ToString(), r => r["actions"]![0]!.ToString());

    [Fact]
    public void Commit_only_stages_the_given_files()
    {
        SetRule(_a, "x", "sticky", 5);
        File.WriteAllText(Path.Combine(_a, "README.md"), "changed\n");
        string? sha = Use(_a).Commit([Path.Combine(_a, Rel, "common.json")], "edit x");
        Assert.Matches("^[0-9a-f]{7,}$", sha);
        Assert.Equal("akuwm: edit x", Git(_a, "log", "-1", "--format=%s").Trim());
        Assert.Equal([Rel + "/common.json"], Git(_a, "show", "--name-only", "--format=", "HEAD").Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.Equal(" M README.md", Git(_a, "status", "--porcelain").TrimEnd('\n'));
        Assert.Null(Use(_a).Commit([Path.Combine(_a, Rel, "common.json")], "nothing"));
    }

    [Fact]
    public void Commit_takes_a_snapshot_directory_and_returns_null_outside_a_repo()
    {
        string snaps = Path.Combine(_a, Rel, "snapshots", "20260907-120000-TEST-manual");
        Directory.CreateDirectory(snaps);
        File.WriteAllText(Path.Combine(snaps, "common.json"), "{}\n");
        Assert.NotNull(Use(_a).Commit([snaps], "snapshot"));
        Assert.Contains(Rel + "/snapshots/20260907-120000-TEST-manual/common.json", Git(_a, "show", "--name-only", "--format=", "HEAD"));

        string outside = Path.Combine(_tmp, "outside", Rel);
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "common.json"), "{}");
        var lone = new GitSync(new ConfigPaths(outside, "TEST", Path.Combine(_tmp, "outside", "state")));
        Assert.False(lone.InRepo());
        Assert.Null(lone.Commit([Path.Combine(outside, "common.json")], "x"));
        Assert.False(lone.Status().Repo);
    }

    [Fact]
    public void Status_reports_dirty_ahead_and_behind()
    {
        GitStatus st = Use(_a).Status();
        Assert.Equal((true, "main", "origin/main"), (st.Repo, st.Branch, st.Upstream));
        Assert.Equal((0, 0), (st.Ahead, st.Behind));
        Assert.Empty(st.Dirty);

        string profile = Path.Combine(_a, Rel, "TEST.json");
        JsonObject d = Read(profile);
        d["shortcuts"]![0]!["name"] = "B";
        Write(profile, d);
        Assert.Equal(["M " + Rel + "/TEST.json"], Use(_a).Status().Dirty);
        Use(_a).Commit([profile], "rename");
        st = Use(_a).Status();
        Assert.Equal((1, 0), (st.Ahead, st.Behind));
        Git(_a, "push", "--quiet");
        Assert.Equal(0, Use(_a).Status().Ahead);
        Assert.Equal(0, Use(_b).Status().Behind);
        Git(_b, "fetch", "--quiet");
        Assert.Equal((0, 1), (Use(_b).Status().Ahead, Use(_b).Status().Behind));
    }

    [Fact]
    public void Push_and_pull_against_origin_pull_is_ff_only()
    {
        SetRule(_a, "x", "sticky", 5);
        Use(_a).Commit([Path.Combine(_a, Rel, "common.json")], "edit x");
        Assert.NotEqual(OriginMain(), Head(_a));
        Use(_a).Push();
        Assert.Equal(OriginMain(), Head(_a));
        string pulled = Use(_b).Pull();
        Assert.Contains("Fast-forward", pulled);
        Assert.Equal(Head(_a), Head(_b));

        SetRule(_a, "x", "tile", 6);
        Use(_a).Commit([Path.Combine(_a, Rel, "common.json")], "x again");
        Use(_a).Push();
        SetRule(_b, "y", "sticky", 7);
        Use(_b).Commit([Path.Combine(_b, Rel, "common.json")], "y");
        string bHead = Head(_b);
        Assert.Throws<InvalidOperationException>(() => Use(_b).Pull());
        Assert.Equal(bHead, Head(_b));
        Assert.Equal("0", Git(_b, "rev-list", "--count", "--merges", "HEAD").Trim());
    }

    [Fact]
    public void Sync_fast_forwards_when_only_the_remote_changed()
    {
        SetRule(_a, "x", "sticky", 5);
        Use(_a).Commit([Path.Combine(_a, Rel, "common.json")], "edit x");
        Use(_a).Push();
        SyncResult res = Use(_b).Sync();
        Assert.Equal((true, 1, 0, false), (res.Ok, res.Behind, res.Ahead, res.Pushed));
        Assert.Empty(res.Merged);
        Assert.Equal(Head(_a), Head(_b));
        Assert.Equal("sticky", Actions(_b)["x"]);
    }

    [Fact]
    public void Sync_pushes_when_only_local_changed()
    {
        SetRule(_a, "x", "sticky", 5);
        Use(_a).Commit([Path.Combine(_a, Rel, "common.json")], "edit x");
        string head = Head(_a);
        SyncResult res = Use(_a).Sync(pushAfter: false);
        Assert.Equal((true, 0, 1, false), (res.Ok, res.Behind, res.Ahead, res.Pushed));
        Assert.NotEqual(OriginMain(), head);
        res = Use(_a).Sync();
        Assert.Equal((true, 0, 1, true), (res.Ok, res.Behind, res.Ahead, res.Pushed));
        Assert.Equal(OriginMain(), head);
        res = Use(_a).Sync();
        Assert.Equal((true, 0, 0, false), (res.Ok, res.Behind, res.Ahead, res.Pushed));
    }

    [Fact]
    public void Sync_merges_the_same_file_by_id_and_keeps_history_linear()
    {
        SetRule(_a, "x", "sticky", 10);
        Use(_a).Commit([Path.Combine(_a, Rel, "common.json")], "A edits x");
        Use(_a).Push();
        SetRule(_b, "y", "tile", 11);
        Use(_b).Commit([Path.Combine(_b, Rel, "common.json")], "B edits y");
        SyncResult res = Use(_b).Sync();
        Assert.True(res.Ok, res.Error);
        Assert.Equal((1, 1, true), (res.Behind, res.Ahead, res.Pushed));
        Assert.Equal([Rel + "/common.json"], res.Merged);
        Assert.Equal(new Dictionary<string, string> { ["x"] = "sticky", ["y"] = "tile" }, Actions(_b));
        Assert.Equal(OriginMain(), Head(_b));
        Assert.Equal("0", Git(_b, "rev-list", "--count", "--merges", "HEAD").Trim());
        Assert.False(Directory.Exists(Path.Combine(_b, ".git", "rebase-merge")));
        res = Use(_a).Sync();
        Assert.Equal((true, 1), (res.Ok, res.Behind));
        Assert.Empty(res.Merged);
        Assert.Equal(Actions(_b), Actions(_a));
    }

    [Fact]
    public void Sync_conflicting_item_newest_updated_at_wins_on_both_clones()
    {
        SetRule(_a, "x", "A-x", 10);
        SetRule(_a, "y", "A-y", 30);
        Use(_a).Commit([Path.Combine(_a, Rel, "common.json")], "A");
        Use(_a).Push();
        SetRule(_b, "x", "B-x", 20);
        SetRule(_b, "y", "B-y", 5);
        Use(_b).Commit([Path.Combine(_b, Rel, "common.json")], "B");
        SyncResult res = Use(_b).Sync();
        Assert.True(res.Ok, res.Error);
        Assert.Equal(new Dictionary<string, string> { ["x"] = "B-x", ["y"] = "A-y" }, Actions(_b));
        Assert.True(Use(_a).Sync().Ok);
        Assert.Equal(Actions(_b), Actions(_a));
    }

    [Fact]
    public void Sync_refuses_dirty_state_and_unpushed_foreign_commits()
    {
        SetRule(_a, "x", "sticky", 10);
        Use(_a).Commit([Path.Combine(_a, Rel, "common.json")], "A");
        Use(_a).Push();
        SetRule(_b, "y", "dirty", 1);
        SyncResult res = Use(_b).Sync();
        Assert.False(res.Ok);
        Assert.Contains("uncommitted", res.Error);
        Git(_b, "checkout", "--", Rel + "/common.json");

        File.WriteAllText(Path.Combine(_b, "README.md"), "human work\n");
        Git(_b, "commit", "--quiet", "-am", "human: readme");
        SetRule(_b, "y", "tile", 11);
        Use(_b).Commit([Path.Combine(_b, Rel, "common.json")], "B");
        string head = Head(_b);
        res = Use(_b).Sync();
        Assert.False(res.Ok);
        Assert.Contains("non-state files", res.Error);
        Assert.Contains("README.md", res.Error);
        Assert.Equal(head, Head(_b));
        Assert.NotEqual(OriginMain(), head);
        Assert.Equal(string.Empty, Git(_b, "status", "--porcelain"));
    }

    [Fact]
    public void Sync_converges_both_clones_across_two_files()
    {
        SetRule(_a, "x", "A-x", 10);
        string profileA = Path.Combine(_a, Rel, "TEST.json");
        JsonObject d = Read(profileA);
        d["shortcuts"]![0]!["name"] = "A-name";
        d["shortcuts"]![0]!["updated_at"] = 10;
        Write(profileA, d);
        Use(_a).Commit([Path.Combine(_a, Rel, "common.json"), profileA], "A");
        Use(_a).Push();

        SetRule(_b, "z", "B-z", 20);
        string profileB = Path.Combine(_b, Rel, "TEST.json");
        d = Read(profileB);
        ((JsonArray)d["shortcuts"]!).Add((JsonNode)new JsonObject { ["id"] = "k2", ["keys"] = "Hyper+B", ["name"] = "B", ["updated_at"] = 20 });
        Write(profileB, d);
        Use(_b).Commit([Path.Combine(_b, Rel, "common.json"), profileB], "B");

        SyncResult res = Use(_b).Sync();
        Assert.True(res.Ok, res.Error);
        Assert.Equal([Rel + "/TEST.json", Rel + "/common.json"], res.Merged.OrderBy(m => m, StringComparer.Ordinal).ToArray());
        Assert.True(res.Pushed);
        res = Use(_a).Sync();
        Assert.Equal((true, 1, 0, false), (res.Ok, res.Behind, res.Ahead, res.Pushed));
        Assert.Equal(Head(_b), Head(_a));
        Assert.Equal(OriginMain(), Head(_a));
        Assert.Equal(File.ReadAllText(Path.Combine(_a, Rel, "common.json")), File.ReadAllText(Path.Combine(_b, Rel, "common.json")));
        Assert.Equal(new Dictionary<string, string> { ["x"] = "A-x", ["y"] = "float", ["z"] = "B-z" }, Actions(_a));
        JsonObject prof = Read(profileA);
        Assert.Equal(["A-name", "B"], ((JsonArray)prof["shortcuts"]!).Select(s => s!["name"]!.ToString()).ToArray());
        Assert.Equal("0", Git(_a, "rev-list", "--count", "--merges", "HEAD").Trim());
    }
}
