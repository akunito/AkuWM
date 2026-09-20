namespace AkuWM.Core.Ipc;

/// <summary>
/// Splits a command line the way both the pipe and the IPC read it.
/// </summary>
/// <remarks>
/// A command arrives as one string (<c>command focus --workspace 12</c>,
/// <c>config import glazewm --dry-run</c>), because that is what a socket
/// carries and what the scripts already write. Quoting is the obvious one:
/// double quotes group. A backslash escapes only a quote or another backslash:
/// every other one is literal, because the paths these carry are Windows paths
/// and <c>shell-exec "C:\Program Files\x\y.exe"</c> has to arrive intact.
/// </remarks>
public static class CommandLine
{
    /// <summary>
    /// Windows' own rule, because these carry Windows paths.
    /// </summary>
    /// <remarks>
    /// A backslash is literal unless it comes before a quote. Before a quote,
    /// 2n backslashes mean n backslashes and the quote does its job; 2n+1 mean
    /// n backslashes and a literal quote. That is what CommandLineToArgvW
    /// does, and it is why <c>shell-exec "C:\Program Files\x\y.exe"</c> arrives
    /// intact -- treating every backslash as an escape ate the separators of
    /// every app-launch chord whose target lives under Program Files.
    /// </remarks>
    public static string[] Split(string line)
    {
        var tokens = new List<string>();
        var token = new System.Text.StringBuilder();
        bool quoted = false;
        bool any = false;

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];

            if (c == '\\')
            {
                int slashes = 0;
                while (i < line.Length && line[i] == '\\')
                {
                    slashes++;
                    i++;
                }

                bool beforeQuote = i < line.Length && line[i] == '"';
                token.Append('\\', beforeQuote ? slashes / 2 : slashes);

                if (!beforeQuote)
                {
                    i--;
                    any = true;
                    continue;
                }

                if ((slashes & 1) == 1)
                {
                    token.Append('"');
                }
                else
                {
                    quoted = !quoted;
                }

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

    /// <summary>The inverse of Split, following the same rule.</summary>
    private static string Quote(string argument)
    {
        if (argument.Length > 0 && !argument.Any(c => char.IsWhiteSpace(c) || c == '"'))
        {
            return argument;
        }

        var quoted = new System.Text.StringBuilder(argument.Length + 8);
        quoted.Append('"');

        for (int i = 0; i < argument.Length; i++)
        {
            if (argument[i] != '\\')
            {
                if (argument[i] == '"')
                {
                    quoted.Append('\\');
                }

                quoted.Append(argument[i]);
                continue;
            }

            int slashes = 0;
            while (i < argument.Length && argument[i] == '\\')
            {
                slashes++;
                i++;
            }

            // Doubled only where they meet a quote -- the closing one included,
            // or a path ending in a separator would swallow it.
            bool beforeQuote = i >= argument.Length || argument[i] == '"';
            quoted.Append('\\', beforeQuote ? slashes * 2 : slashes);

            if (i < argument.Length)
            {
                if (argument[i] == '"')
                {
                    quoted.Append('\\');
                }

                quoted.Append(argument[i]);
            }
        }

        quoted.Append('"');
        return quoted.ToString();
    }

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
