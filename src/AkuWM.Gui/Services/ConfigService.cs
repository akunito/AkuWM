using System.Text.Json.Nodes;
using AkuWM.Core;
using AkuWM.Core.Config;
using AkuWM.Core.Ipc;

namespace AkuWM.Gui.Services;

/// <summary>
/// The configuration on disk, the way the daemon's <c>config set</c> writes it:
/// through <see cref="ConfigEdit"/>, so the schema check, the unknown-key
/// refusal and the merged validation are the daemon's own. Items are raw
/// <see cref="JsonObject"/>s -- a key this build has never heard of survives
/// the trip through the editor.
/// </summary>
public sealed class ConfigService
{
    public ConfigService(ConfigPaths paths) => Paths = paths;

    public ConfigPaths Paths { get; }

    public LoadedConfig Load() => ConfigStore.Load(Paths);

    public JsonObject Layer(string layer)
    {
        string file = FileOf(layer);
        if (!File.Exists(file))
        {
            return new JsonObject();
        }

        try
        {
            return JsonNode.Parse(File.ReadAllText(file), documentOptions: new() { CommentHandling = System.Text.Json.JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject ?? new JsonObject();
        }
        catch (System.Text.Json.JsonException)
        {
            return new JsonObject();
        }
    }

    public string FileOf(string layer) => layer == "profile" ? Paths.ProfileFile : Paths.CommonFile;

    /// <summary>The section's items of one layer, as they are in the file.</summary>
    public List<JsonObject> Items(string layer, string section)
    {
        var items = new List<JsonObject>();
        if (Layer(layer)[section] is JsonArray array)
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

    /// <summary>
    /// Common under profile, merged by id field by field, in common's order
    /// with profile-only items after. Each comes with the layer the editor
    /// must write to: profile when the profile has a say, else common.
    /// </summary>
    public List<(JsonObject Item, string Layer)> Effective(string section)
    {
        List<JsonObject> common = Items("common", section);
        List<JsonObject> profile = Items("profile", section);
        var result = new List<(JsonObject, string)>(common.Count + profile.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonObject bottom in common)
        {
            string? id = IdOf(bottom);
            JsonObject? top = id is null ? null : profile.Find(p => IdOf(p) == id);
            if (top is null)
            {
                result.Add((Clone(bottom), "common"));
                continue;
            }

            seen.Add(id!);
            JsonObject merged = Clone(bottom);
            foreach ((string key, JsonNode? value) in top)
            {
                merged[key] = value?.DeepClone();
            }

            result.Add((merged, "profile"));
        }

        foreach (JsonObject only in profile)
        {
            string? id = IdOf(only);
            if (id is null || !seen.Contains(id))
            {
                result.Add((Clone(only), "profile"));
            }
        }

        return result;
    }

    /// <summary>
    /// Writes one item into the layer's list: replaced by id, or appended with a
    /// fresh id. The whole list goes through the daemon's edit path, validated
    /// against the other layer. Null when it was written.
    /// </summary>
    public string? Save(string layer, string section, JsonObject item, string idPrefix, int? at = null)
    {
        List<JsonObject> items = Items(layer, section);
        string? id = IdOf(item);
        if (id is null)
        {
            id = Ids.New(idPrefix);
            item["id"] = id;
        }

        item["updated_at"] = ConfigStore.Now();
        int index = items.FindIndex(i => IdOf(i) == id);
        if (index >= 0)
        {
            items[index] = item;
        }
        else if (at is { } wanted && wanted >= 0 && wanted <= items.Count)
        {
            items.Insert(wanted, item);
        }
        else
        {
            items.Add(item);
        }

        return WriteList(layer, section, items);
    }

    public string? Remove(string layer, string section, string id)
    {
        List<JsonObject> items = Items(layer, section);
        int index = items.FindIndex(i => IdOf(i) == id);
        if (index < 0)
        {
            return $"{id} is not in {layer}'s {section}";
        }

        items.RemoveAt(index);
        return WriteList(layer, section, items);
    }

    /// <summary>Reorders a layer's list to the given ids; ids not present are ignored, items not named keep their place at the end.</summary>
    public string? Reorder(string layer, string section, IReadOnlyList<string> ids)
    {
        List<JsonObject> items = Items(layer, section);
        var ordered = new List<JsonObject>(items.Count);
        foreach (string id in ids)
        {
            JsonObject? item = items.Find(i => IdOf(i) == id);
            if (item is not null && !ordered.Contains(item))
            {
                ordered.Add(item);
            }
        }

        foreach (JsonObject item in items)
        {
            if (!ordered.Contains(item))
            {
                ordered.Add(item);
            }
        }

        return WriteList(layer, section, ordered);
    }

    /// <summary>One scalar, the way <c>config set</c> does it.</summary>
    public string? Set(string layer, string path, string jsonValue)
    {
        ConfigEdit.Result result = ConfigEdit.Apply(Paths, FileOf(layer), path, jsonValue, remove: false);
        return result.Error;
    }

    private string? WriteList(string layer, string section, List<JsonObject> items)
    {
        var array = new JsonArray();
        foreach (JsonObject item in items)
        {
            array.Add((JsonNode)Clone(item));
        }

        try
        {
            ConfigEdit.Result result = ConfigEdit.Apply(Paths, FileOf(layer), section, array.ToJsonString(ConfigJson.Options), remove: false);
            return result.Error;
        }
        catch (ConfigException ex)
        {
            return ex.Message;
        }
    }

    /// <summary>Tells the daemon; what it says back is what the person sees.</summary>
    public static string? Reload(IDaemon daemon, bool bindings)
    {
        if (!daemon.IsRunning())
        {
            return "saved; AkuWM is not running, it will read the file when it starts";
        }

        CommandResponse reply = daemon.Send("compat command wm-reload-config");
        if (!reply.Success)
        {
            return reply.Error ?? "the reload failed";
        }

        if (bindings)
        {
            CommandResponse chords = daemon.Send("bindings reload");
            if (!chords.Success)
            {
                return chords.Error ?? "the bindings did not reload";
            }
        }

        return null;
    }

    public static string? IdOf(JsonObject item) => item["id"]?.GetValue<string>();

    public static JsonObject Clone(JsonObject item) => (JsonObject)item.DeepClone();

    public static string Str(JsonObject item, string key) =>
        item[key] is JsonValue v && v.TryGetValue(out string? s) ? s : string.Empty;

    public static bool Flag(JsonObject item, string key, bool fallback = true) =>
        item[key] is JsonValue v && v.TryGetValue(out bool b) ? b : fallback;

    public static int? Int(JsonObject item, string key) =>
        item[key] is JsonValue v && v.TryGetValue(out int i) ? i : null;
}
