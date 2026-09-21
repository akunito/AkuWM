using AkuWM.Core.Ipc;

namespace AkuWM.Core.Compat;

/// <param name="Verb">The command, e.g. <c>focus</c>.</param>
/// <param name="Options">The <c>--name value</c> pairs, without the dashes.</param>
/// <param name="Positional">Anything that was not an option.</param>
/// <param name="Subject">The container <c>--id</c> names, when one was given.</param>
public readonly record struct ParsedCommand(
    string Verb,
    IReadOnlyDictionary<string, string?> Options,
    IReadOnlyList<string> Positional,
    Guid? Subject)
{
    public bool Has(string option) => Options.ContainsKey(option);

    public string? Value(string option) => Options.TryGetValue(option, out string? value) ? value : null;

    /// <summary>The number in an option, whatever unit it was written with.</summary>
    public int? Number(string option) => ParseNumber(Value(option));

    /// <summary>
    /// True when the value was written in pixels rather than as a share.
    /// </summary>
    /// <remarks>
    /// The scripts send both: `resize --width 10%` from a keyboard chord and
    /// `resize --width 300px` from an Alt+drag, which knows exactly how far the
    /// pointer went. Reading the second as a percentage moves the split three
    /// times the width of the screen; reading it as null, which is what
    /// happened, moves nothing at all.
    /// </remarks>
    public bool InPixels(string option) =>
        Value(option)?.EndsWith("px", StringComparison.OrdinalIgnoreCase) == true;

    private static int? ParseNumber(string? value)
    {
        if (value is not { Length: > 0 })
        {
            return null;
        }

        ReadOnlySpan<char> digits = value.AsSpan();

        if (digits.EndsWith("px", StringComparison.OrdinalIgnoreCase))
        {
            digits = digits[..^2];
        }
        else if (digits.EndsWith("ppt", StringComparison.OrdinalIgnoreCase))
        {
            digits = digits[..^3];
        }
        else if (digits.Length > 0 && digits[^1] == '%')
        {
            digits = digits[..^1];
        }

        return int.TryParse(digits, out int number) ? number : null;
    }
}

/// <summary>
/// Parses the command strings the existing scripts and the bar already send.
/// </summary>
/// <remarks>
/// <para>
/// This is a compatibility layer, not AkuWM's own grammar: the shape is the
/// one the window manager AkuWM replaces used, because a hundred lines of
/// working AutoHotkey and a bar nobody wants to rewrite already speak it. The
/// point of M2 is that those keep working while what is underneath changes.
/// </para>
/// <para>
/// The awkward parts are real and had to be handled: <c>--centered=false</c>
/// attaches its value with an equals sign, and <c>resize --width -5%</c> has a
/// value that begins with a minus. So a token counts as a value unless it
/// begins with two dashes.
/// </para>
/// </remarks>
public static class GlazeCommandLine
{
    public static ParsedCommand Parse(string line)
    {
        string[] tokens = CommandLine.Split(line);
        var options = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var positional = new List<string>();
        string verb = string.Empty;
        Guid? subject = null;

        for (int i = 0; i < tokens.Length; i++)
        {
            string token = tokens[i];

            if (!token.StartsWith("--", StringComparison.Ordinal))
            {
                if (verb.Length == 0)
                {
                    verb = token;
                }
                else
                {
                    positional.Add(token);
                }

                continue;
            }

            string name = token[2..];
            string? value = null;

            int equals = name.IndexOf('=', StringComparison.Ordinal);
            if (equals >= 0)
            {
                value = name[(equals + 1)..];
                name = name[..equals];
            }
            else if (i + 1 < tokens.Length && !tokens[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                value = tokens[++i];
            }

            if (name.Equals("id", StringComparison.OrdinalIgnoreCase)
                && Guid.TryParse(value, out Guid parsed))
            {
                subject = parsed;
                continue;
            }

            options[name] = value;
        }

        return new ParsedCommand(verb, options, positional, subject);
    }
}
