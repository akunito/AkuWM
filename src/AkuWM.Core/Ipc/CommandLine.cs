namespace AkuWM.Core.Ipc;

/// <summary>
/// Splits a command line the way both the pipe and the IPC read it.
/// </summary>
/// <remarks>
/// A command arrives as one string (<c>command focus --workspace 12</c>,
/// <c>config import glazewm --dry-run</c>), because that is what a socket
/// carries and what the scripts already write. Quoting is the obvious one:
/// double quotes group, a backslash escapes the next character inside them.
/// </remarks>
public static class CommandLine
{
    public static string[] Split(string line)
    {
        var tokens = new List<string>();
        var token = new System.Text.StringBuilder();
        bool quoted = false;
        bool any = false;

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];

            if (c == '\\' && quoted && i + 1 < line.Length)
            {
                token.Append(line[++i]);
                any = true;
                continue;
            }

            if (c == '"')
            {
                quoted = !quoted;
                any = true;
                continue;
            }

            if (!quoted && char.IsWhiteSpace(c))
            {
                if (any)
                {
                    tokens.Add(token.ToString());
                    token.Clear();
                    any = false;
                }

                continue;
            }

            token.Append(c);
            any = true;
        }

        if (any)
        {
            tokens.Add(token.ToString());
        }

        return [.. tokens];
    }

    /// <summary>Puts arguments back together, quoting what needs it.</summary>
    public static string Join(IEnumerable<string> arguments) =>
        string.Join(' ', arguments.Select(Quote));

    private static string Quote(string argument)
    {
        if (argument.Length > 0 && !argument.Any(c => char.IsWhiteSpace(c) || c == '"'))
        {
            return argument;
        }

        return '"' + argument.Replace("\\", "\\\\").Replace("\"", "\\\"") + '"';
    }

    /// <summary>
    /// Reads <c>--name value</c> and <c>--flag</c> out of the tail of a command.
    /// </summary>
    public static Dictionary<string, string?> Options(IReadOnlyList<string> tokens, int from = 0)
    {
        var options = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        for (int i = from; i < tokens.Count; i++)
        {
            if (!tokens[i].StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            string name = tokens[i][2..];
            string? value = i + 1 < tokens.Count && !tokens[i + 1].StartsWith("--", StringComparison.Ordinal)
                ? tokens[++i]
                : null;
            options[name] = value;
        }

        return options;
    }
}
