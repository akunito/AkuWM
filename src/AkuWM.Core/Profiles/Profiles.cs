using System.Text.Json;
using System.Text.Json.Nodes;
using AkuWM.Core.Config;

namespace AkuWM.Core.Profiles;

/// <summary>What one section looks like from layer A against layer B.</summary>
public sealed record SectionDiff(
    List<JsonObject> OnlyA,
    List<JsonObject> OnlyB,
    List<(JsonObject A, JsonObject B)> Changed,
    List<JsonObject> Same)
{
    public bool IsEmpty => OnlyA.Count == 0 && OnlyB.Count == 0 && Changed.Count == 0 && Same.Count == 0;
}

public sealed record SnapshotInfo(string Id, string Path, string Reason, string Profile, long Time, List<string> Files);

public sealed record CopyResult(List<string> Copied, string Section, string From, string To, string Snapshot, int DestinationTotal);

/// <summary>
/// The layers side by side (every <c>*.json</c> in the config directory is
/// one: <c>common</c> and one per machine), items copied between them by id,
/// and a dated copy of every layer before anything is written. The port of
/// sway-apps' profiles module; the JSON is handled raw so a key this build
/// does not know survives a copy.
/// </summary>
public sealed class Profiles
{
    public static readonly string[] Sections = ["rules", "startup", "shortcuts", "tools", "monitors", "nodes", "workspaces"];

    public const int Keep = 30;

    private readonly ConfigPaths _paths;
    private readonly Func<DateTimeOffset> _clock;

    public Profiles(ConfigPaths paths, Func<DateTimeOffset>? clock = null)
    {
        _paths = paths;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public string SnapshotDir => Path.Combine(_paths.ConfigDir, "snapshots");

    /// <summary>Layer name → file, for every layer on disk, common first.</summary>
    public SortedDictionary<string, string> Layers()
    {
        var found = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (!Directory.Exists(_paths.ConfigDir))
        {
            return found;
        }

        foreach (string file in Directory.EnumerateFiles(_paths.ConfigDir, "*.json"))
        {
            found[Path.GetFileNameWithoutExtension(file)] = file;
        }

        return found;
    }

    public string FileOf(string layer) => Path.Combine(_paths.ConfigDir, layer + ".json");

    /// <summary>The layer as it is in the file; empty when there is no file. Asking never creates one.</summary>
    public JsonObject Layer(string name) => Read(FileOf(name));

    public static JsonObject Read(string file)
    {
        if (!File.Exists(file))
        {
            return new JsonObject();
        }

        try
        {
            return JsonNode.Parse(File.ReadAllText(file), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject ?? new JsonObject();
        }
        catch (JsonException)
        {
            return new JsonObject();
        }
    }

    public static List<JsonObject> Items(JsonObject layer, string section)
    {
        var items = new List<JsonObject>();
        if (layer[section] is JsonArray array)
        {
            foreach (JsonNode? node in array)
            {
                if (node is JsonObject item)
                {
                    items.Add(item);
                }
            }
        }

        return items;
    }

    public static string? IdOf(JsonObject item) => item["id"] is JsonValue v && v.TryGetValue(out string? id) ? id : null;

    private static Dictionary<string, JsonObject> ById(List<JsonObject> items)
    {
        var map = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (JsonObject item in items)
        {
            if (IdOf(item) is { } id)
            {
                map[id] = item;
            }
        }

        return map;
    }

    /// <summary>Equal apart from <c>updated_at</c>: a copy stamps it, a save stamps it, and neither is a change.</summary>
    public static bool SameButForStamp(JsonObject a, JsonObject b)
    {
        JsonObject x = (JsonObject)a.DeepClone();
        JsonObject y = (JsonObject)b.DeepClone();
        x.Remove("updated_at");
        y.Remove("updated_at");
        return JsonNode.DeepEquals(x, y);
    }

    public SectionDiff Diff(string a, string b, string section)
    {
        Dictionary<string, JsonObject> la = ById(Items(Layer(a), section));
        Dictionary<string, JsonObject> lb = ById(Items(Layer(b), section));
        var diff = new SectionDiff([], [], [], []);
        foreach ((string id, JsonObject item) in la)
        {
            if (!lb.TryGetValue(id, out JsonObject? other))
            {
                diff.OnlyA.Add(item);
            }
            else if (SameButForStamp(item, other))
            {
                diff.Same.Add(item);
            }
            else
            {
                diff.Changed.Add((item, other));
            }
        }

        foreach ((string id, JsonObject item) in lb)
        {
            if (!la.ContainsKey(id))
            {
                diff.OnlyB.Add(item);
            }
        }

        return diff;
    }

    public Dictionary<string, (int OnlyA, int OnlyB, int Changed, int Same)> Summary(string a, string b)
    {
        var summary = new Dictionary<string, (int, int, int, int)>(StringComparer.Ordinal);
        foreach (string section in Sections)
        {
            SectionDiff d = Diff(a, b, section);
            summary[section] = (d.OnlyA.Count, d.OnlyB.Count, d.Changed.Count, d.Same.Count);
        }

        return summary;
    }

    /// <summary>One line for a list row.</summary>
    public static string Label(string section, JsonObject item)
    {
        string Str(string key) => item[key] is JsonValue v && v.TryGetValue(out string? s) ? s : string.Empty;
        string id = IdOf(item) ?? "?";
        switch (section)
        {
            case "shortcuts":
                {
                    string keys = Str("keys");
                    string what = Str("name");
                    if (what.Length == 0)
                    {
                        what = Str("command");
                    }

                    return $"{(keys.Length == 0 ? "?" : keys)} → {what}";
                }

            case "startup":
                return $"{Str("name")} ({Str("command")})";
            case "monitors":
                {
                    string edid = item["match"] is JsonObject m && m["edid"] is JsonValue e && e.TryGetValue(out string? s) ? s : string.Empty;
                    return $"{id} = {edid} ({Str("orientation")})".Replace(" ()", string.Empty);
                }

            case "nodes":
                {
                    string ssh = Str("ssh");
                    return $"{id} {(ssh.Length == 0 ? "local" : ssh)}";
                }

            case "workspaces":
                return $"{Str("name")} on {Str("monitor")}";
            default:
                {
                    string name = Str("name");
                    return name.Length == 0 ? id : name;
                }
        }
    }

    // ---- snapshots -------------------------------------------------------

    /// <summary>Every layer copied into a dated folder; the oldest beyond <see cref="Keep"/> go.</summary>
    public string Snapshot(string reason)
    {
        Directory.CreateDirectory(SnapshotDir);
        DateTimeOffset now = _clock();
        string stamp = now.ToString("yyyyMMdd-HHmmss");
        var safe = new System.Text.StringBuilder(reason.Length);
        foreach (char c in reason)
        {
            safe.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '-');
        }

        string tail = safe.ToString();
        if (tail.Length > 40)
        {
            tail = tail[..40];
        }

        tail = tail.Trim('-');
        if (tail.Length == 0)
        {
            tail = "manual";
        }

        string dir = Path.Combine(SnapshotDir, $"{stamp}-{_paths.Profile}-{tail}");
        int n = 1;
        while (Directory.Exists(dir))
        {
            n++;
            dir = Path.Combine(SnapshotDir, $"{stamp}-{n}-{_paths.Profile}-{tail}");
        }

        Directory.CreateDirectory(dir);
        var files = new List<string>();
        foreach ((string name, string file) in Layers())
        {
            File.Copy(file, Path.Combine(dir, name + ".json"), overwrite: true);
            files.Add(name + ".json");
        }

        var meta = new JsonObject
        {
            ["reason"] = reason,
            ["profile"] = _paths.Profile,
            ["time"] = now.ToUnixTimeSeconds(),
            ["files"] = new JsonArray([.. files.Select(f => (JsonNode)f)]),
        };
        File.WriteAllText(Path.Combine(dir, "META.json"), meta.ToJsonString(ConfigJson.Options) + "\n");
        Prune();
        return Path.GetFileName(dir);
    }

    public List<string> Prune(int keep = Keep)
    {
        var removed = new List<string>();
        if (!Directory.Exists(SnapshotDir))
        {
            return removed;
        }

        List<string> dirs = Directory.EnumerateDirectories(SnapshotDir).OrderBy(d => Path.GetFileName(d), StringComparer.Ordinal).ToList();
        for (int i = 0; i < dirs.Count - keep; i++)
        {
            Directory.Delete(dirs[i], recursive: true);
            removed.Add(Path.GetFileName(dirs[i]));
        }

        return removed;
    }

    public List<SnapshotInfo> Snapshots()
    {
        var list = new List<SnapshotInfo>();
        if (!Directory.Exists(SnapshotDir))
        {
            return list;
        }

        foreach (string dir in Directory.EnumerateDirectories(SnapshotDir).OrderBy(d => Path.GetFileName(d), StringComparer.Ordinal))
        {
            JsonObject meta = Read(Path.Combine(dir, "META.json"));
            var files = new List<string>();
            if (meta["files"] is JsonArray array)
            {
                foreach (JsonNode? f in array)
                {
                    files.Add(f?.ToString() ?? string.Empty);
                }
            }

            list.Add(new SnapshotInfo(
                Path.GetFileName(dir),
                dir,
                meta["reason"]?.ToString() ?? string.Empty,
                meta["profile"]?.ToString() ?? string.Empty,
                meta["time"] is JsonValue t && t.TryGetValue(out long time) ? time : 0,
                files));
        }

        return list;
    }

    /// <summary>The snapshot's folder, by exact name or by a unique fragment.</summary>
    public string SnapshotPath(string id)
    {
        string exact = Path.Combine(SnapshotDir, id);
        if (Directory.Exists(exact))
        {
            return exact;
        }

        List<string> candidates = Directory.Exists(SnapshotDir)
            ? Directory.EnumerateDirectories(SnapshotDir).Where(d => Path.GetFileName(d).Contains(id, StringComparison.Ordinal)).ToList()
            : [];
        return candidates.Count == 1 ? candidates[0] : throw new FileNotFoundException($"no snapshot '{id}'");
    }

    /// <summary>
    /// Whole files (all, or the named ones) or only some sections of them,
    /// back from the snapshot. The current state is snapshotted first, so a
    /// restore is itself undoable.
    /// </summary>
    public List<string> Restore(string id, IReadOnlyList<string>? files = null, IReadOnlyList<string>? sections = null)
    {
        string dir = SnapshotPath(id);
        string name = Path.GetFileName(dir);
        Snapshot("before-restore-" + name[..Math.Min(15, name.Length)]);
        var touched = new List<string>();
        foreach (string src in Directory.EnumerateFiles(dir, "*.json").OrderBy(f => f, StringComparer.Ordinal))
        {
            string file = Path.GetFileName(src);
            if (file == "META.json" || (files is not null && !files.Contains(file)))
            {
                continue;
            }

            string dst = Path.Combine(_paths.ConfigDir, file);
            if (sections is { Count: > 0 })
            {
                JsonObject current = Read(dst);
                JsonObject old = Read(src);
                foreach (string section in sections)
                {
                    current[section] = old[section]?.DeepClone() ?? new JsonArray();
                }

                ConfigStore.SaveText(dst, current.ToJsonString(ConfigJson.Options));
            }
            else
            {
                File.Copy(src, dst, overwrite: true);
            }

            touched.Add(file);
        }

        return touched;
    }

    /// <summary>Per file and section: how many items differ between the snapshot and now.</summary>
    public Dictionary<string, Dictionary<string, (int OnlySnapshot, int OnlyNow, int Changed)>> DiffSnapshot(string id)
    {
        string dir = SnapshotPath(id);
        var result = new Dictionary<string, Dictionary<string, (int, int, int)>>(StringComparer.Ordinal);
        foreach (string src in Directory.EnumerateFiles(dir, "*.json").OrderBy(f => f, StringComparer.Ordinal))
        {
            string file = Path.GetFileName(src);
            if (file == "META.json")
            {
                continue;
            }

            JsonObject old = Read(src);
            JsonObject now = Read(Path.Combine(_paths.ConfigDir, file));
            var per = new Dictionary<string, (int, int, int)>(StringComparer.Ordinal);
            foreach (string section in Sections)
            {
                Dictionary<string, JsonObject> o = ById(Items(old, section));
                Dictionary<string, JsonObject> c = ById(Items(now, section));
                int onlySnapshot = o.Keys.Count(k => !c.ContainsKey(k));
                int onlyNow = c.Keys.Count(k => !o.ContainsKey(k));
                int changed = o.Count(kv => c.TryGetValue(kv.Key, out JsonObject? other) && !SameButForStamp(kv.Value, other));
                per[section] = (onlySnapshot, onlyNow, changed);
            }

            result[file] = per;
        }

        return result;
    }

    // ---- copy between layers ---------------------------------------------

    /// <summary>
    /// Items of <paramref name="section"/> (all, or the given ids) from one
    /// layer into another: an id already there is overwritten, the rest of
    /// the destination is kept unless <paramref name="replace"/>. Snapshot
    /// first; copied items are stamped.
    /// </summary>
    public CopyResult CopyItems(string from, string to, string section, IReadOnlyList<string>? ids = null, bool replace = false)
    {
        if (Array.IndexOf(Sections, section) < 0)
        {
            throw new ArgumentException($"section must be one of {string.Join(", ", Sections)}", nameof(section));
        }

        List<JsonObject> items = Items(Layer(from), section);
        if (ids is not null)
        {
            items = items.Where(i => IdOf(i) is { } id && ids.Contains(id)).ToList();
        }

        string snapshot = Snapshot($"copy-{section}-{from}-to-{to}");
        long now = _clock().ToUnixTimeSeconds();
        JsonObject dst = Layer(to);
        var merged = new List<JsonObject>();
        if (replace)
        {
            foreach (JsonObject item in items)
            {
                merged.Add(Stamped(item, now));
            }
        }
        else
        {
            merged = Items(dst, section).Select(i => (JsonObject)i.DeepClone()).ToList();
            foreach (JsonObject item in items)
            {
                string? id = IdOf(item);
                int at = merged.FindIndex(m => IdOf(m) == id);
                if (at >= 0)
                {
                    merged[at] = Stamped(item, now);
                }
                else
                {
                    merged.Add(Stamped(item, now));
                }
            }
        }

        dst[section] = new JsonArray([.. merged.Select(m => (JsonNode)m)]);
        if (dst["version"] is null)
        {
            dst["version"] = ConfigDefaults.SchemaVersion;
        }

        ConfigStore.SaveText(FileOf(to), dst.ToJsonString(ConfigJson.Options));
        return new CopyResult(items.Select(i => IdOf(i) ?? string.Empty).ToList(), section, from, to, snapshot, merged.Count);
    }

    private static JsonObject Stamped(JsonObject item, long now)
    {
        var copy = (JsonObject)item.DeepClone();
        copy["updated_at"] = now;
        return copy;
    }

    /// <summary>The layers and the snapshot folder: what a commit takes.</summary>
    public List<string> Files()
    {
        List<string> files = Layers().Values.ToList();
        if (Directory.Exists(SnapshotDir))
        {
            files.Add(SnapshotDir);
        }

        return files;
    }
}
