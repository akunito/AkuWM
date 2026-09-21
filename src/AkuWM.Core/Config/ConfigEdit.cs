using System.Text.Json;
using System.Text.Json.Nodes;

namespace AkuWM.Core.Config;

/// <summary>
/// Changes one value in one layer, on disk.
/// </summary>
/// <remarks>
/// <para>
/// Through <see cref="JsonNode"/> rather than the typed model, so a key this
/// build has never heard of survives the trip. Round-tripping the model drops
/// unknown keys on read and therefore never writes them back -- a file written
/// by a newer AkuWM would be quietly truncated the first time the GUI saved
/// anything, which is the worst kind of data loss: silent, and on somebody
/// else's behalf.
/// </para>
/// <para>
/// Comments do not survive, and cannot: preserving them means keeping the
/// whole concrete syntax tree. The file is machine-owned; the schema carries
/// the explanations.
/// </para>
/// </remarks>
public static class ConfigEdit
{
    /// <param name="Was">What was there before, as JSON, or null if nothing.</param>
    /// <param name="Now">What is there now, or null when it was removed.</param>
    /// <param name="Error">Why nothing was written, when nothing was.</param>
    /// <param name="Warnings">What the validator said about the result.</param>
    public readonly record struct Result(
        string? Was,
        string? Now,
        string? Error,
        IReadOnlyList<string> Warnings);

    /// <summary>
    /// Writes <paramref name="path"/> in <paramref name="file"/>, or removes it.
    /// </summary>
    /// <param name="path">Dotted, e.g. <c>gaps.inner</c> or <c>general.focus_follows_mouse</c>.</param>
    public static Result Apply(ConfigPaths paths, string file, string path, string? value, bool remove)
    {
        string[] steps = path.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (steps.Length == 0)
        {
            return new Result(null, null, $"'{path}' names nothing", []);
        }

        JsonObject root = Read(file);
        JsonObject at = root;

        for (int i = 0; i < steps.Length - 1; i++)
        {
            if (at[steps[i]] is JsonObject deeper)
            {
                at = deeper;
                continue;
            }

            if (remove)
            {
                // Nothing to take away, and inventing the objects on the way
                // down to delete a leaf would leave empty husks behind.
                return new Result(null, null, null, []);
            }

            var made = new JsonObject();
            at[steps[i]] = made;
            at = made;
        }

        string leaf = steps[^1];
        string? was = at[leaf]?.ToJsonString(ConfigJson.Options);

        if (remove)
        {
            at.Remove(leaf);
        }
        else
        {
            at[leaf] = Parse(value!);
        }

        string written = root.ToJsonString(ConfigJson.Options);

        // A key the schema has never heard of is the typo trap: it is accepted,
        // it is preserved, it validates clean, and it does nothing for ever.
        // Writing one is almost always a misspelling -- the file is snake_case
        // and every other name in the system is not -- so it is refused here
        // rather than discovered months later.
        if (!remove && Unknown(written, path) is { } unknown)
        {
            return new Result(was, null, unknown, []);
        }

        // Validated merged with the layer underneath, because that is the only
        // form that means anything: a key can be fine on its own and still
        // name a workspace or a monitor role that does not exist.
        ValidationResult check = Check(paths, file, written);

        if (!check.Ok)
        {
            return new Result(
                was,
                null,
                string.Join("; ", check.Errors.Select(e => e.ToString())),
                []);
        }

        ConfigStore.SaveText(file, written);

        return new Result(
            was,
            at[leaf]?.ToJsonString(ConfigJson.Options),
            null,
            [.. check.Warnings.Select(w => w.ToString())]);
    }

    /// <summary>
    /// Why the schema does not have this path, if it does not.
    /// </summary>
    /// <remarks>
    /// Asked by round trip rather than by reflecting over the model: the
    /// typed reader drops what it does not know, so a path that does not
    /// survive being read and written again is a path nothing will ever act
    /// on. Lists are left alone -- their items are matched by id, and an
    /// index is not a schema question.
    /// </remarks>
    private static string? Unknown(string written, string path)
    {
        JsonNode? survived;
        try
        {
            survived = JsonNode.Parse(ConfigJson.Write(ConfigJson.Read(written)));
        }
        catch (ConfigException)
        {
            return null;
        }

        foreach (string step in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (survived is JsonArray)
            {
                return null;
            }

            if (survived is not JsonObject at || !at.TryGetPropertyValue(step, out survived))
            {
                return $"the configuration has no '{path}'. Names in this file are written "
                       + "like gaps.inner and effects.focused_border";
            }
        }

        return null;
    }

    private static JsonObject Read(string file)
    {
        if (!File.Exists(file))
        {
            return [];
        }

        try
        {
            return JsonNode.Parse(
                File.ReadAllText(file),
                documentOptions: new JsonDocumentOptions
                {
                    CommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true,
                }) as JsonObject ?? [];
        }
        catch (JsonException ex)
        {
            throw new ConfigException($"{file} is not valid JSON: {ex.Message}");
        }
    }

    /// <summary>
    /// A bare word becomes the type it looks like, so a person does not have
    /// to quote a number and a GUI does not have to guess.
    /// </summary>
    private static JsonNode? Parse(string value)
    {
        if (value.Length == 0)
        {
            return JsonValue.Create(string.Empty);
        }

        if (value is "null")
        {
            return null;
        }

        if (bool.TryParse(value, out bool flag))
        {
            return JsonValue.Create(flag);
        }

        if (long.TryParse(value, out long number))
        {
            return JsonValue.Create(number);
        }

        if (double.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out double real))
        {
            return JsonValue.Create(real);
        }

        // An array or an object, written out: the only way to set `gaps.outer`
        // from a command line.
        if (value[0] is '[' or '{')
        {
            try
            {
                return JsonNode.Parse(value);
            }
            catch (JsonException ex)
            {
                throw new ConfigException($"'{value}' is not valid JSON: {ex.Message}");
            }
        }

        return JsonValue.Create(value);
    }

    private static ValidationResult Check(ConfigPaths paths, string file, string written)
    {
        AkuWmConfig edited = ConfigJson.Read(written);
        bool isCommon = string.Equals(file, paths.CommonFile, StringComparison.OrdinalIgnoreCase);
        AkuWmConfig? other = ConfigStore.ReadIfPresent(isCommon ? paths.ProfileFile : paths.CommonFile);

        AkuWmConfig merged = ConfigMerge.MergeAll(
            ConfigDefaults.Create(),
            (isCommon ? edited : other) ?? new AkuWmConfig(),
            (isCommon ? other : edited) ?? new AkuWmConfig());

        return ConfigValidator.Validate(merged);
    }
}
