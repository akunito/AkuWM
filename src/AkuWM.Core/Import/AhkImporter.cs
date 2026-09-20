using System.Text.RegularExpressions;
using AkuWM.Core.Config;

namespace AkuWM.Core.Import;

/// <summary>
/// Reads the raise-or-launch table out of <c>hyper-desktops.ahk</c>.
/// </summary>
/// <remarks>
/// The AutoHotkey file is Diego's own, and only its <em>table</em> is read: the
/// thirteen <c>Hyper+&lt;letter&gt;</c> lines that say "this chord raises this
/// process, and launches this command if it is not running". The commands are
/// translated out of AutoHotkey's expression syntax into plain
/// <c>%ENV%</c> paths, which is the one thing the Python prototype did not do
/// -- it stored <c>A_ProgramFiles "\Alacritty\alacritty.exe"</c> verbatim, a
/// string only AutoHotkey can evaluate.
/// </remarks>
public static class AhkImporter
{
    // ^!#l:: AppToggle("Telegram.exe", A_AppData "\Telegram Desktop\Telegram.exe")
    private static readonly Regex ToggleLine = new(
        """^\^!#(\+?)([A-Za-z0-9]):: *AppToggle\("([^"]+)" *, *(.+?)\) *(?:;.*)?$""",
        RegexOptions.CultureInvariant);

    private static readonly Regex Literal = new("""^"([^"]*)"$""", RegexOptions.CultureInvariant);
    private static readonly Regex EnvGet = new("""^EnvGet\("([^"]+)"\)$""", RegexOptions.CultureInvariant);

    /// <summary>AutoHotkey's built-in variables, as the environment names Windows knows.</summary>
    private static readonly Dictionary<string, string> Builtins = new(StringComparer.OrdinalIgnoreCase)
    {
        ["A_ProgramFiles"] = "ProgramFiles",
        ["A_AppData"] = "APPDATA",
        ["A_AppDataCommon"] = "ProgramData",
        ["A_WinDir"] = "windir",
        ["A_Temp"] = "TEMP",
        ["A_ComSpec"] = "ComSpec",
        ["A_UserName"] = "USERNAME",
        ["A_Desktop"] = "USERPROFILE\\Desktop",
    };

    public static List<ShortcutConfig> ImportToggles(string ahk, ImportSummary summary)
    {
        var shortcuts = new List<ShortcutConfig>();
        long now = ConfigStore.Now();

        foreach (string raw in ahk.Split('\n'))
        {
            Match match = ToggleLine.Match(raw.Trim().TrimEnd('\r'));
            if (!match.Success)
            {
                continue;
            }

            string shift = match.Groups[1].Value;
            string key = match.Groups[2].Value.ToUpperInvariant();
            string process = match.Groups[3].Value;
            string expression = match.Groups[4].Value.Trim();

            string? command = TranslateExpression(expression);
            if (command is null)
            {
                summary.Notes.Add(
                    $"Hyper+{key}: the launch command '{expression}' is an AutoHotkey expression " +
                    "AkuWM cannot translate; it was kept as a comment and must be written by hand");
            }

            shortcuts.Add(new ShortcutConfig
            {
                Id = Ids.New("k"),
                Keys = $"Hyper+{(shift.Length > 0 ? "Shift+" : string.Empty)}{key}",
                Kind = "app",
                App = process,
                Command = command ?? string.Empty,
                Category = "Apps",
                Name = RuleName(process),
                Enabled = true,
                Notes = command is null
                    ? $"imported from hyper-desktops.ahk; original: {expression}"
                    : "imported from hyper-desktops.ahk",
                UpdatedAt = now,
            });
        }

        summary.Shortcuts = shortcuts.Count;
        return shortcuts;
    }

    /// <summary>
    /// <c>A_ProgramFiles "\Alacritty\alacritty.exe"</c> →
    /// <c>%ProgramFiles%\Alacritty\alacritty.exe</c>. Returns null when a part
    /// is not a literal or a variable AkuWM knows.
    /// </summary>
    internal static string? TranslateExpression(string expression)
    {
        var parts = new List<string>();
        foreach (string piece in SplitConcatenation(expression))
        {
            Match literal = Literal.Match(piece);
            if (literal.Success)
            {
                parts.Add(literal.Groups[1].Value);
                continue;
            }

            Match env = EnvGet.Match(piece);
            if (env.Success)
            {
                parts.Add($"%{env.Groups[1].Value}%");
                continue;
            }

            if (Builtins.TryGetValue(piece, out string? variable))
            {
                parts.Add($"%{variable}%");
                continue;
            }

            return null;
        }

        return parts.Count == 0 ? null : string.Concat(parts);
    }

    /// <summary>
    /// AutoHotkey concatenates by putting expressions next to each other. The
    /// pieces are separated by whitespace that is not inside a quoted string.
    /// </summary>
    private static IEnumerable<string> SplitConcatenation(string expression)
    {
        var piece = new System.Text.StringBuilder();
        bool inString = false;

        foreach (char c in expression)
        {
            if (c == '"')
            {
                inString = !inString;
                piece.Append(c);
                continue;
            }

            if (!inString && char.IsWhiteSpace(c))
            {
                if (piece.Length > 0)
                {
                    yield return piece.ToString();
                    piece.Clear();
                }

                continue;
            }

            piece.Append(c);
        }

        if (piece.Length > 0)
        {
            yield return piece.ToString();
        }
    }

    private static string RuleName(string process) =>
        process.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? process[..^4] : process;
}
