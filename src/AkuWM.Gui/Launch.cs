namespace AkuWM.Gui;

/// <summary>What the command line asked for. Pure, so the single-instance hand-off and the tests share it.</summary>
public sealed record LaunchArgs(string? Section, string? Select, bool Toggle, bool Hidden, string? Smoke)
{
    public static readonly string[] Sections =
        ["rules", "startup", "apps", "windows", "shortcuts", "monitors", "tools", "log", "doctor"];

    public static bool ValidSection(string? section) =>
        section is null || Array.IndexOf(Sections, section) >= 0;

    public static LaunchArgs Parse(IReadOnlyList<string> args)
    {
        string? section = null, select = null, smoke = null;
        bool toggle = false, hidden = false;
        for (int i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--section" when i + 1 < args.Count: section = args[++i]; break;
                case "--select" when i + 1 < args.Count: select = args[++i]; break;
                case "--smoke" when i + 1 < args.Count: smoke = args[++i]; break;
                case "--toggle": toggle = true; break;
                case "--hidden": hidden = true; break;
            }
        }

        return new LaunchArgs(section, select, toggle, hidden, smoke);
    }

    /// <summary>One line for the pipe: tab-separated so a path with spaces survives.</summary>
    public string ToLine()
    {
        var parts = new List<string>(8);
        if (Section is not null) { parts.Add("--section"); parts.Add(Section); }
        if (Select is not null) { parts.Add("--select"); parts.Add(Select); }
        if (Smoke is not null) { parts.Add("--smoke"); parts.Add(Smoke); }
        if (Toggle) { parts.Add("--toggle"); }
        if (Hidden) { parts.Add("--hidden"); }
        return string.Join('\t', parts);
    }

    public static LaunchArgs FromLine(string line) =>
        Parse(line.Length == 0 ? [] : line.Split('\t'));
}

public enum Visibility { Hidden, Shown, Active }

public enum LaunchAction { Show, Hide, Activate, Nothing }

public static class Launch
{
    /// <summary>
    /// What a launch does to a window already up (Hyper+S is a toggle): the
    /// same table sway-apps has. A section named is always shown, never hidden.
    /// </summary>
    public static LaunchAction Decide(LaunchArgs args, Visibility now)
    {
        if (args.Hidden)
        {
            return LaunchAction.Nothing;
        }

        if (args.Toggle && args.Section is null)
        {
            return now switch
            {
                Visibility.Active => LaunchAction.Hide,
                Visibility.Shown => LaunchAction.Activate,
                _ => LaunchAction.Show,
            };
        }

        return now == Visibility.Hidden ? LaunchAction.Show : LaunchAction.Activate;
    }
}
