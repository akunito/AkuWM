using System.Collections;
using System.Reflection;
using System.Text.Json.Serialization;

namespace AkuWM.Core.Config;

/// <summary>
/// Puts one configuration layer on top of another.
/// </summary>
/// <remarks>
/// <para>
/// Scalars and nested objects merge field by field: a value present in the top
/// layer wins, a value absent leaves the one below alone. That is what makes a
/// machine file able to change <c>gaps.inner</c> without repeating the rest of
/// the section.
/// </para>
/// <para>
/// Lists merge <strong>by id</strong> (workspaces by name). An entry whose id
/// is already in the layer below is merged into it, field by field, in place;
/// an entry with a new id is appended. The order of the layer below is kept, so
/// a diff of the written file stays readable. Nothing is ever removed by
/// merging -- to switch an entry off, the top layer sets
/// <c>"enabled": false</c>.
/// </para>
/// </remarks>
public static class ConfigMerge
{
    public static AkuWmConfig Merge(AkuWmConfig bottom, AkuWmConfig top) => new()
    {
        Version = top.Version ?? bottom.Version,
        General = MergeObject(bottom.General, top.General),
        Gaps = MergeObject(bottom.Gaps, top.Gaps),
        Effects = MergeObject(bottom.Effects, top.Effects),
        Layout = MergeObject(bottom.Layout, top.Layout),
        Monitors = MergeList(bottom.Monitors, top.Monitors),
        Workspaces = MergeList(bottom.Workspaces, top.Workspaces),
        Rules = MergeList(bottom.Rules, top.Rules),
        Shortcuts = MergeList(bottom.Shortcuts, top.Shortcuts),
        Startup = MergeList(bottom.Startup, top.Startup),
        Apps = MergeObject(bottom.Apps, top.Apps),
        Tools = MergeList(bottom.Tools, top.Tools),
        Nodes = MergeList(bottom.Nodes, top.Nodes),
        Settings = MergeObject(bottom.Settings, top.Settings),
    };

    /// <summary>Merges the layers in order, the last one winning.</summary>
    public static AkuWmConfig MergeAll(params AkuWmConfig[] layers)
    {
        if (layers.Length == 0)
        {
            return new AkuWmConfig();
        }

        AkuWmConfig result = layers[0];
        for (int i = 1; i < layers.Length; i++)
        {
            result = Merge(result, layers[i]);
        }

        return result;
    }

    public static List<T>? MergeList<T>(List<T>? bottom, List<T>? top)
        where T : class, IConfigItem, new()
    {
        if (top is null)
        {
            return bottom;
        }

        if (bottom is null)
        {
            return [.. top];
        }

        var result = new List<T>(bottom);
        foreach (T incoming in top)
        {
            int at = result.FindIndex(existing =>
                existing.Id is { Length: > 0 } &&
                string.Equals(existing.Id, incoming.Id, StringComparison.Ordinal));

            if (at >= 0)
            {
                result[at] = MergeObject(result[at], incoming)!;
            }
            else
            {
                result.Add(incoming);
            }
        }

        return result;
    }

    /// <summary>
    /// Field-by-field merge of two configuration objects. Leaves (strings,
    /// numbers, bools, arrays, lists of strings) are taken whole from the top
    /// layer when present; nested configuration objects recurse.
    /// </summary>
    public static T? MergeObject<T>(T? bottom, T? top)
        where T : class, new()
    {
        if (top is null)
        {
            return bottom;
        }

        if (bottom is null)
        {
            return top;
        }

        var merged = new T();
        foreach (PropertyInfo property in Mergeable(typeof(T)))
        {
            object? bottomValue = property.GetValue(bottom);
            object? topValue = property.GetValue(top);
            property.SetValue(merged, MergeValue(property.PropertyType, bottomValue, topValue));
        }

        return merged;
    }

    private static object? MergeValue(Type type, object? bottom, object? top)
    {
        if (top is null)
        {
            return bottom;
        }

        if (bottom is null || !IsConfigObject(type))
        {
            return top;
        }

        // A nested configuration object (settings.git, monitor.match, ...).
        MethodInfo merge = typeof(ConfigMerge)
            .GetMethod(nameof(MergeObject), BindingFlags.Public | BindingFlags.Static)!
            .MakeGenericMethod(type);
        return merge.Invoke(null, [bottom, top]);
    }

    private static bool IsConfigObject(Type type) =>
        type.IsClass
        && type != typeof(string)
        && !typeof(IEnumerable).IsAssignableFrom(type)
        && type.Namespace == typeof(AkuWmConfig).Namespace
        && type.GetConstructor(Type.EmptyTypes) is not null;

    private static IEnumerable<PropertyInfo> Mergeable(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite)
            .Where(p => p.GetCustomAttribute<JsonIgnoreAttribute>() is null);
}
