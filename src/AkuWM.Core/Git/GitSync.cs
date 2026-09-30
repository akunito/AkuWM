using System.Diagnostics;
using System.Text.Json.Nodes;
using AkuWM.Core.Config;

namespace AkuWM.Core.Git;

public readonly record struct Run(int Code, string Out, string Err)
{
    public string Text => (Out + Err).Trim();
}

public sealed record GitStatus(bool Repo, string Branch, List<string> Dirty, int? Ahead, int? Behind, string? Upstream);

public sealed record SyncResult(bool Ok, int Behind, int Ahead, List<string> Merged, bool Pushed, string? Error, string? Skipped = null);

/// <summary>
/// The configuration files under version control: commit only them, push,
/// pull fast-forward, and <see cref="Sync"/> -- fetch, rebase our own commits
/// onto upstream merging the layers by id when both sides touched one, push.
/// The port of sway-apps' gitsync. Git itself is behind <see cref="Runner"/>
/// so the merge is tested without a repository and the rest against real
/// repositories in a temporary directory.
/// </summary>
public sealed class GitSync
{
    public static readonly string[] Sections = ["rules", "startup", "monitors", "workspaces", "shortcuts", "tools", "nodes"];

    private readonly ConfigPaths _paths;
    private readonly Func<string[], string, Run> _git;
    private string? _top;

    public GitSync(ConfigPaths paths, Func<string[], string, Run>? runner = null)
    {
        _paths = paths;
        _git = runner ?? Runner;
    }

    /// <summary>`git` with the given arguments, in <paramref name="cwd"/>.</summary>
    public static Run Runner(string[] args, string cwd)
    {
        var info = new ProcessStartInfo("git")
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string a in args)
        {
            info.ArgumentList.Add(a);
        }

        try
        {
            using Process process = Process.Start(info)!;
            string output = process.StandardOutput.ReadToEnd();
            string error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            return new Run(process.ExitCode, output, error);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new Run(127, string.Empty, ex.Message);
        }
    }

    private Run Git(params string[] args) => _git(args, Top);

    private Run GitIn(string cwd, params string[] args) => _git(args, cwd);

    /// <summary>The work tree the config directory sits in; the config directory itself when it is not in one.</summary>
    public string Top
    {
        get
        {
            if (_top is null)
            {
                Run r = GitIn(_paths.ConfigDir, "rev-parse", "--show-toplevel");
                _top = r.Code == 0 && r.Out.Trim().Length > 0 ? Path.GetFullPath(r.Out.Trim()) : _paths.ConfigDir;
            }

            return _top;
        }
    }

    public bool InRepo() => Directory.Exists(_paths.ConfigDir) && GitIn(_paths.ConfigDir, "rev-parse", "--is-inside-work-tree").Code == 0;

    private string Rel(string path)
    {
        string full = Path.GetFullPath(path);
        string top = Top.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return full.StartsWith(top, StringComparison.Ordinal) && full.Length > top.Length
            ? full[(top.Length + 1)..].Replace('\\', '/')
            : full.Replace('\\', '/');
    }

    /// <summary>What a commit takes: the layers and the snapshot folder.</summary>
    public List<string> StateFiles()
    {
        var files = new List<string>();
        if (Directory.Exists(_paths.ConfigDir))
        {
            files.AddRange(Directory.EnumerateFiles(_paths.ConfigDir, "*.json").OrderBy(f => f, StringComparer.Ordinal));
            string snapshots = Path.Combine(_paths.ConfigDir, "snapshots");
            if (Directory.Exists(snapshots))
            {
                files.Add(snapshots);
            }
        }

        return files;
    }

    /// <summary>Whether a repository path is one of ours: a JSON layer or a snapshot under the config directory.</summary>
    public bool IsStateFile(string rel)
    {
        string dir = Rel(_paths.ConfigDir);
        return rel.StartsWith(dir + "/", StringComparison.Ordinal) && (rel.EndsWith(".json", StringComparison.Ordinal) || rel.Contains("/snapshots/", StringComparison.Ordinal));
    }

    public GitStatus Status()
    {
        if (!InRepo())
        {
            return new GitStatus(false, string.Empty, [], null, null, null);
        }

        List<string> files = StateFiles().Where(File.Exists).ToList();
        var dirty = new List<string>();
        if (files.Count > 0)
        {
            Run status = Git(["status", "--porcelain", "--", .. files.Select(Rel)]);
            foreach (string line in status.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                dirty.Add(line.Trim());
            }
        }

        int? ahead = null, behind = null;
        Run upstream = Git("rev-parse", "--abbrev-ref", "@{u}");
        if (upstream.Code == 0)
        {
            string[] counts = Git("rev-list", "--left-right", "--count", "@{u}...HEAD").Out.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (counts.Length == 2)
            {
                behind = int.Parse(counts[0]);
                ahead = int.Parse(counts[1]);
            }
        }

        string branch = Git("rev-parse", "--abbrev-ref", "HEAD").Out.Trim();
        return new GitStatus(true, branch, dirty, ahead, behind, upstream.Code == 0 ? upstream.Out.Trim() : null);
    }

    /// <summary>Commits only these paths. The short sha, or null when nothing changed.</summary>
    public string? Commit(IReadOnlyList<string> files, string message)
    {
        if (!InRepo())
        {
            return null;
        }

        List<string> rel = files.Where(f => File.Exists(f) || Directory.Exists(f)).Select(Rel).ToList();
        if (rel.Count == 0)
        {
            return null;
        }

        Check(Git(["add", "--", .. rel]));
        if (Git(["diff", "--cached", "--quiet", "--", .. rel]).Code == 0)
        {
            return null;
        }

        Check(Git(["commit", "--quiet", "--no-verify", "-m", "akuwm: " + message, "--", .. rel]));
        return Check(Git("rev-parse", "--short", "HEAD")).Out.Trim();
    }

    public string Push() => Check(Git("push", "--porcelain")).Text;

    public string Pull() => Check(Git("pull", "--ff-only")).Text;

    private static Run Check(Run run) =>
        run.Code == 0 ? run : throw new InvalidOperationException(run.Text.Length > 0 ? run.Text : $"git exited {run.Code}");

    // ---- the three-way merge of a layer -------------------------------------

    private static Dictionary<string, JsonObject> ById(JsonNode? list)
    {
        var map = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        if (list is JsonArray array)
        {
            foreach (JsonNode? node in array)
            {
                if (node is JsonObject item && item["id"] is JsonValue v && v.TryGetValue(out string? id))
                {
                    map[id] = item;
                }
            }
        }

        return map;
    }

    private static long Stamp(JsonObject item) =>
        item["updated_at"] is JsonValue v && v.TryGetValue(out long stamp) ? stamp : 0;

    private static bool Same(JsonObject? a, JsonObject? b) => a is null ? b is null : b is not null && JsonNode.DeepEquals(a, b);

    /// <summary>
    /// Per item: newest <c>updated_at</c> wins when both sides changed it; a
    /// one-sided delete wins over an untouched item; a delete against an edit
    /// keeps the edit. Ours first in order, then what only they have.
    /// </summary>
    public static JsonArray MergeSection(JsonNode? baseList, JsonNode? oursList, JsonNode? theirsList)
    {
        Dictionary<string, JsonObject> b = ById(baseList), o = ById(oursList), t = ById(theirsList);
        var order = new List<string>();
        foreach (string id in o.Keys.Concat(t.Keys).Concat(b.Keys))
        {
            if (!order.Contains(id))
            {
                order.Add(id);
            }
        }

        var kept = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (string id in order)
        {
            b.TryGetValue(id, out JsonObject? bo);
            o.TryGetValue(id, out JsonObject? oo);
            t.TryGetValue(id, out JsonObject? to);
            JsonObject? choice;
            if (oo is null && to is null)
            {
                continue;
            }
            else if (oo is null)
            {
                choice = bo is null || !Same(to, bo) ? to : null;
            }
            else if (to is null)
            {
                choice = bo is null || !Same(oo, bo) ? oo : null;
            }
            else if (Same(oo, to))
            {
                choice = oo;
            }
            else if (Same(oo, bo))
            {
                choice = to;
            }
            else if (Same(to, bo))
            {
                choice = oo;
            }
            else
            {
                choice = Stamp(oo) >= Stamp(to) ? oo : to;
            }

            if (choice is not null)
            {
                kept[id] = choice;
            }
        }

        var merged = new JsonArray();
        foreach (string id in o.Keys)
        {
            if (kept.TryGetValue(id, out JsonObject? item))
            {
                merged.Add((JsonNode)item.DeepClone());
            }
        }

        foreach (string id in t.Keys)
        {
            if (!o.ContainsKey(id) && kept.TryGetValue(id, out JsonObject? item))
            {
                merged.Add((JsonNode)item.DeepClone());
            }
        }

        return merged;
    }

    /// <summary>Ours as the frame, every section merged, settings the union with ours winning, the higher version.</summary>
    public static string MergeStateJson(string baseText, string oursText, string theirsText)
    {
        JsonObject baseObject = baseText.Trim().Length == 0 ? new JsonObject() : (JsonObject)JsonNode.Parse(baseText)!;
        var ours = (JsonObject)JsonNode.Parse(oursText)!;
        var theirs = (JsonObject)JsonNode.Parse(theirsText)!;
        var merged = (JsonObject)ours.DeepClone();
        foreach (string section in Sections)
        {
            merged[section] = MergeSection(baseObject[section], ours[section], theirs[section]);
        }

        var settings = new JsonObject();
        if (theirs["settings"] is JsonObject ts)
        {
            foreach ((string k, JsonNode? v) in ts)
            {
                settings[k] = v?.DeepClone();
            }
        }

        if (ours["settings"] is JsonObject os)
        {
            foreach ((string k, JsonNode? v) in os)
            {
                settings[k] = v?.DeepClone();
            }
        }

        merged["settings"] = settings;
        int version = Math.Max(
            ours["version"] is JsonValue ov && ov.TryGetValue(out int a) ? a : 1,
            theirs["version"] is JsonValue tv && tv.TryGetValue(out int c) ? c : 1);
        merged["version"] = version;
        return merged.ToJsonString(ConfigJson.Options) + "\n";
    }

    /// <summary>During a stopped rebase: every conflicted layer merged by id and staged.</summary>
    private List<string> ResolveConflicts()
    {
        var fixedFiles = new List<string>();
        foreach (string rel in Git("diff", "--name-only", "--diff-filter=U").Out.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!IsStateFile(rel) || !rel.EndsWith(".json", StringComparison.Ordinal))
            {
                continue;
            }

            string Show(string stage)
            {
                Run r = Git("show", $"{stage}:{rel}");
                return r.Code == 0 ? r.Out : string.Empty;
            }

            // In a rebase, stage 2 ("ours") is upstream and stage 3 ("theirs") is our replayed commit.
            string merged = MergeStateJson(Show(":1"), Show(":3"), Show(":2"));
            File.WriteAllText(Path.Combine(Top, rel), merged);
            Git("add", "--", rel);
            fixedFiles.Add(rel);
        }

        return fixedFiles;
    }

    /// <summary>fetch → rebase our commits (merging the layers) → push.</summary>
    public SyncResult Sync(bool pushAfter = true)
    {
        if (!InRepo())
        {
            return new SyncResult(false, 0, 0, [], false, null, Skipped: "not a repository");
        }

        GitStatus status = Status();
        if (status.Dirty.Count > 0)
        {
            return new SyncResult(false, 0, 0, [], false, "state files have uncommitted changes; save first");
        }

        Git("fetch", "--quiet");
        (int behind, int ahead) = Counts();
        var merged = new List<string>();
        if (behind > 0)
        {
            string[] ours = Git("diff", "--name-only", "@{u}...HEAD").Out.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            List<string> foreign = ours.Where(f => !IsStateFile(f)).ToList();
            if (foreign.Count > 0 && ahead > 0)
            {
                return new SyncResult(false, behind, ahead, [], false, $"unpushed commits touch non-state files ({string.Join(", ", foreign.Take(3))}); sync by hand");
            }

            Run rebase = Git("rebase", "--autostash", "@{u}");
            int tries = 0;
            while (rebase.Code != 0 && tries < 10)
            {
                tries++;
                List<string> fixedFiles = ResolveConflicts();
                if (fixedFiles.Count == 0)
                {
                    Git("rebase", "--abort");
                    return new SyncResult(false, behind, ahead, merged, false, "rebase conflict outside state files; aborted: " + Tail(rebase.Text));
                }

                merged.AddRange(fixedFiles);
                rebase = Git("-c", "core.editor=true", "rebase", "--continue");
            }

            if (rebase.Code != 0)
            {
                Git("rebase", "--abort");
                return new SyncResult(false, behind, ahead, merged, false, "rebase failed: " + Tail(rebase.Text));
            }
        }

        bool pushed = false;
        if (pushAfter)
        {
            (_, int stillAhead) = Counts();
            if (stillAhead > 0)
            {
                Run push = Git("push", "--quiet");
                pushed = push.Code == 0;
                if (!pushed)
                {
                    return new SyncResult(false, behind, ahead, merged, false, "push failed: " + Tail(push.Text));
                }
            }
        }

        return new SyncResult(true, behind, ahead, merged, pushed, null);
    }

    private (int Behind, int Ahead) Counts()
    {
        string[] counts = Git("rev-list", "--left-right", "--count", "@{u}...HEAD").Out.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return counts.Length == 2 ? (int.Parse(counts[0]), int.Parse(counts[1])) : (0, 0);
    }

    private static string Tail(string text) => text.Length <= 300 ? text : text[^300..];
}
