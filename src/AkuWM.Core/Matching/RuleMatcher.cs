using System.Text.RegularExpressions;
using AkuWM.Core.Config;

namespace AkuWM.Core.Matching;

/// <summary>
/// Decides whether a rule fires for a window.
/// </summary>
/// <remarks>
/// <para>
/// A rule holds a list of alternatives; it fires when <strong>any</strong> of
/// them matches. Inside one alternative every field present must match
/// (<c>class</c> and <c>title</c> together is how the calculator is told apart
/// from every other window <c>ApplicationFrameHost</c> hosts).
/// </para>
/// <para>
/// A value is an exact, case-insensitive string, unless it starts with
/// <c>re:</c>, in which case the rest is a .NET regular expression matched
/// case-insensitively anywhere in the value. The process name is written
/// without <c>.exe</c>, as the platform reports it.
/// </para>
/// </remarks>
public sealed class RuleMatcher
{
    public const string RegexPrefix = "re:";

    private static readonly TimeSpan RegexBudget = TimeSpan.FromMilliseconds(50);
    private readonly Dictionary<string, Regex> _compiled = new(StringComparer.Ordinal);

    public bool Matches(RuleConfig rule, WindowFacts window)
    {
        if (rule.Enabled == false || rule.Match is not { Count: > 0 })
        {
            return false;
        }

        return rule.Match.Any(alternative => MatchesAlternative(alternative, window));
    }

    public bool MatchesAlternative(MatchCriteria criteria, WindowFacts window)
    {
        bool any = false;

        if (criteria.Process is { Length: > 0 } process)
        {
            any = true;
            if (!ValueMatches(process, StripExe(window.Process)))
            {
                return false;
            }
        }

        if (criteria.Class is { Length: > 0 } className)
        {
            any = true;
            if (!ValueMatches(className, window.Class))
            {
                return false;
            }
        }

        if (criteria.Title is { Length: > 0 } title)
        {
            any = true;
            if (!ValueMatches(title, window.Title))
            {
                return false;
            }
        }

        // An alternative with no criterion at all would match every window;
        // that is a broken rule, not a wildcard. The validator rejects it too.
        return any;
    }

    /// <summary>Every rule that fires for this window, in configuration order.</summary>
    public IEnumerable<RuleConfig> Firing(IEnumerable<RuleConfig> rules, WindowFacts window) =>
        rules.Where(rule => Matches(rule, window));

    private bool ValueMatches(string pattern, string value)
    {
        if (!pattern.StartsWith(RegexPrefix, StringComparison.Ordinal))
        {
            return string.Equals(pattern, value, StringComparison.OrdinalIgnoreCase);
        }

        return Compile(pattern[RegexPrefix.Length..]).IsMatch(value);
    }

    private Regex Compile(string pattern)
    {
        if (!_compiled.TryGetValue(pattern, out Regex? regex))
        {
            regex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexBudget);
            _compiled[pattern] = regex;
        }

        return regex;
    }

    /// <summary>
    /// The configuration writes <c>Telegram</c>; a caller may pass
    /// <c>Telegram.exe</c>. Both mean the same process.
    /// </summary>
    public static string StripExe(string process) =>
        process.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? process[..^4]
            : process;

    /// <summary>Checks a pattern without running it, for the validator.</summary>
    public static bool IsValidPattern(string pattern, out string? error)
    {
        error = null;
        if (!pattern.StartsWith(RegexPrefix, StringComparison.Ordinal))
        {
            return true;
        }

        try
        {
            _ = new Regex(pattern[RegexPrefix.Length..], RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            return true;
        }
        catch (ArgumentException ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
