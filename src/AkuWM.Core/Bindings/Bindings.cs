using System.Text;
using AkuWM.Core.Config;

namespace AkuWM.Core.Bindings;

/// <summary>
/// The shortcuts of the configuration, as the AutoHotkey script binds them:
/// one line per chord in <c>bindings.tsv</c>, re-read by the script when the
/// daemon posts <c>WM_APP+1</c> to its window (plan 10.27). The script stays
/// the one process a game's anti-cheat can see leave; this is how the GUI
/// changes a chord without a <c>Reload</c>.
/// </summary>
public static class Bindings
{
    /// <summary>The message the script listens for: WM_APP + 1.</summary>
    public const uint ReloadMessage = 0x8001;

    /// <summary>The title the script gives its hidden main window, so the daemon can find it.</summary>
    public const string ScriptWindowTitle = "AkuWM hotkeys";

    private static readonly string[] Kinds = ["app", "wm", "exec", "send"];

    /// <summary>
    /// <c>Hyper+Shift+C</c> to <c>^!#+c</c>. Hyper is Ctrl+Alt+Win, as in
    /// sway; a single letter is lower-cased, any other key name goes through
    /// as AutoHotkey spells it (<c>Space</c>, <c>WheelUp</c>, <c>F5</c>).
    /// Null when there is no key.
    /// </summary>
    public static string? ChordOf(string? keys)
    {
        if (string.IsNullOrWhiteSpace(keys))
        {
            return null;
        }

        // The key is what follows the last '+', unless the key IS the plus.
        string modifiers;
        string key;
        if (keys.EndsWith("++", StringComparison.Ordinal))
        {
            key = "+";
            modifiers = keys[..^2];
        }
        else
        {
            int at = keys.LastIndexOf('+');
            key = at < 0 ? keys : keys[(at + 1)..];
            modifiers = at < 0 ? string.Empty : keys[..at];
        }

        key = key.Trim();
        if (key.Length == 0)
        {
            return null;
        }

        var chord = new StringBuilder(modifiers.Length + key.Length + 4);
        foreach (string part in modifiers.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "hyper": chord.Append("^!#"); break;
                case "ctrl": case "control": chord.Append('^'); break;
                case "alt": chord.Append('!'); break;
                case "win": case "super": chord.Append('#'); break;
                case "shift": chord.Append('+'); break;
                default: return null;
            }
        }

        chord.Append(key.Length == 1 ? key.ToLowerInvariant() : key);
        return chord.ToString();
    }

    /// <summary>
    /// The file's text: <c>chord TAB kind TAB app TAB command TAB process</c>
    /// for every enabled shortcut, environment variables expanded (the script
    /// runs as the user too, but one expansion in one place). What could not
    /// be rendered goes to <paramref name="problems"/> and is left out.
    /// </summary>
    public static string Render(IReadOnlyList<ShortcutConfig>? shortcuts, List<string> problems, Func<string, string>? expand = null)
    {
        expand ??= Environment.ExpandEnvironmentVariables;
        var text = new StringBuilder(1024);
        text.Append("# rendered by AkuWM from shortcuts in common.json; edit there, then `akuwm bindings reload`\n");
        if (shortcuts is null)
        {
            return text.ToString();
        }

        for (int i = 0; i < shortcuts.Count; i++)
        {
            ShortcutConfig s = shortcuts[i];
            if (s.Enabled == false)
            {
                continue;
            }

            string where = $"shortcuts[{i}]{(s.Id is { Length: > 0 } id ? $" ({id})" : string.Empty)}";
            string? chord = ChordOf(s.Keys);
            if (chord is null)
            {
                problems.Add($"{where}: keys \"{s.Keys}\" are not a chord");
                continue;
            }

            string kind = s.Kind?.ToLowerInvariant() ?? string.Empty;
            if (Array.IndexOf(Kinds, kind) < 0)
            {
                problems.Add($"{where}: kind \"{s.Kind}\" is not one of app, wm, exec, send");
                continue;
            }

            if (kind == "app" && string.IsNullOrWhiteSpace(s.App))
            {
                problems.Add($"{where}: an app shortcut names the process to raise");
                continue;
            }

            if (string.IsNullOrWhiteSpace(s.Command))
            {
                problems.Add($"{where}: nothing to run");
                continue;
            }

            string app = s.App ?? string.Empty;
            string command = expand(s.Command);
            string process = s.When?.Process ?? string.Empty;
            if (HasBreak(chord) || HasBreak(app) || HasBreak(command) || HasBreak(process))
            {
                problems.Add($"{where}: a tab or a line break in a field");
                continue;
            }

            text.Append(chord).Append('\t').Append(kind).Append('\t').Append(app).Append('\t').Append(command).Append('\t').Append(process).Append('\n');
        }

        return text.ToString();
    }

    /// <summary>Writes the file whole: the script never reads a half-written one.</summary>
    public static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + ".tmp";
        File.WriteAllText(temp, text, new UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
    }

    private static bool HasBreak(string s) => s.IndexOfAny(['\t', '\n', '\r']) >= 0;
}
