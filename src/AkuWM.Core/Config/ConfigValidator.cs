using System.Text.RegularExpressions;
using AkuWM.Core.Input;
using AkuWM.Core.Matching;

namespace AkuWM.Core.Config;

/// <summary>
/// Reads a merged configuration and says what is wrong with it.
/// </summary>
/// <remarks>
/// Pure: it never touches the disk, so it runs identically in the GUI's
/// "Apply", in <c>akuwm config validate</c> and in the unit tests. A file with
/// an error is refused and the running configuration stays; warnings are shown
/// and the file is used.
/// </remarks>
public static class ConfigValidator
{
    private static readonly Regex Colour = new("^#[0-9a-fA-F]{6}$", RegexOptions.CultureInvariant);

    public static ValidationResult Validate(AkuWmConfig config)
    {
        var issues = new List<ValidationIssue>();

        ValidateVersion(config, issues);
        ValidateGaps(config.Gaps, issues);
        ValidateEffects(config.Effects, issues);
        ValidateLayout(config.Layout, issues);
        ValidateMonitors(config, issues);
        ValidateWorkspaces(config, issues);
        ValidateRules(config, issues);
        WarnAboutTheDecorative(config, issues);
        ValidateShortcuts(config, issues);
        ValidateStartup(config, issues);
        ValidateSettings(config.Settings, issues);

        return new ValidationResult(issues);
    }

    private static void ValidateVersion(AkuWmConfig config, List<ValidationIssue> issues)
    {
        if (config.Version is null)
        {
            issues.Add(ValidationIssue.Warning("version", "missing; assuming " + ConfigDefaults.SchemaVersion));
        }
        else if (config.Version > ConfigDefaults.SchemaVersion)
        {
            issues.Add(ValidationIssue.Error(
                "version",
                $"{config.Version} was written by a newer AkuWM (this one speaks {ConfigDefaults.SchemaVersion})"));
        }
    }

    /// <summary>
    /// Says so when a set option does nothing in this build.
    /// </summary>
    /// <remarks>
    /// A warning, never an error: a configuration written for a newer AkuWM
    /// must still boot an older one. Only options that are actually SET are
    /// mentioned -- warning about every unimplemented key on every start would
    /// train a person to ignore the warnings, which is worse than not having
    /// them.
    /// </remarks>
    private static void WarnAboutTheDecorative(AkuWmConfig config, List<ValidationIssue> issues)
    {
        foreach ((string path, string when) in ConfigDefaults.NotImplemented)
        {
            if (IsSet(config, path))
            {
                issues.Add(ValidationIssue.Warning(
                    path, $"is read and validated, but this build does not act on it ({when})"));
            }
        }
    }

    /// <summary>
    /// Whether a dotted path holds anything, walked over the serialised form.
    /// </summary>
    /// <remarks>
    /// Over the JSON rather than by reflection so the paths in the list above
    /// read the way the file does, which is how a person will look for them.
    /// A <c>[]</c> segment means "any item of this list".
    /// </remarks>
    private static bool IsSet(AkuWmConfig config, string path)
    {
        System.Text.Json.Nodes.JsonNode? at;
        try
        {
            at = System.Text.Json.Nodes.JsonNode.Parse(ConfigJson.Write(config));
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }

        foreach (string step in path.Split('.'))
        {
            if (step.EndsWith("[]", StringComparison.Ordinal))
            {
                if (at?[step[..^2]] is not System.Text.Json.Nodes.JsonArray list)
                {
                    return false;
                }

                // Any item that carries the next step is enough: the point is
                // "somebody set this", not which one.
                string rest = path[(path.IndexOf(step, StringComparison.Ordinal) + step.Length + 1)..];
                foreach (System.Text.Json.Nodes.JsonNode? item in list)
                {
                    if (item?[rest] is not null)
                    {
                        return true;
                    }
                }

                return false;
            }

            if (at is not System.Text.Json.Nodes.JsonObject holder
                || !holder.TryGetPropertyValue(step, out at))
            {
                return false;
            }
        }

        return at is not null;
    }

    private static void ValidateGaps(GapsConfig? gaps, List<ValidationIssue> issues)
    {
        if (gaps is null)
        {
            return;
        }

        ValidateGapsAt(gaps, "gaps", issues);
    }

    private static void ValidateGapsAt(GapsConfig? gaps, string at, List<ValidationIssue> issues)
    {
        if (gaps is null)
        {
            return;
        }

        if (gaps.Inner is < 0)
        {
            issues.Add(ValidationIssue.Error(at + ".inner", "cannot be negative"));
        }

        if (gaps.Inner is > MaximumGap)
        {
            issues.Add(ValidationIssue.Warning(
                at + ".inner", $"{gaps.Inner} px between windows leaves very little window"));
        }

        if (gaps.Outer is not { } outer)
        {
            return;
        }

        // One number is expanded to four when it is read, so anything else
        // here is a hand-written array of the wrong length. The engine used to
        // tolerate a short one while this refused it, which is the worst
        // possible split: valid to run, invalid to save.
        if (outer.Length != 4)
        {
            issues.Add(ValidationIssue.Error(
                at + ".outer",
                $"is one number, or four (top, right, bottom, left); got {outer.Length}"));
            return;
        }

        for (int i = 0; i < outer.Length; i++)
        {
            if (outer[i] < 0)
            {
                // A negative outer gap pushes windows off the screen, which
                // looks exactly like a window manager that has lost them.
                issues.Add(ValidationIssue.Error(
                    at + ".outer", $"cannot be negative ({outer[i]} at {Sides[i]})"));
            }
            else if (outer[i] > MaximumGap)
            {
                issues.Add(ValidationIssue.Warning(
                    at + ".outer", $"{outer[i]} px at {Sides[i]} leaves very little screen"));
            }
        }
    }

    /// <summary>Past this a gap stops being a gap and starts being a margin nobody wanted.</summary>
    private const int MaximumGap = 200;

    private static readonly string[] Sides = ["top", "right", "bottom", "left"];

    private static void ValidateEffects(EffectsConfig? effects, List<ValidationIssue> issues)
    {
        if (effects is null)
        {
            return;
        }

        ValidateEffectsAt(effects, "effects", issues);
    }

    private static void ValidateEffectsAt(EffectsConfig? effects, string at, List<ValidationIssue> issues)
    {
        if (effects is null)
        {
            return;
        }

        CheckColour(effects.FocusedBorder, at + ".focused_border", issues);
        CheckColour(effects.OtherBorder, at + ".other_border", issues);

        if (effects.Corners is { Length: > 0 } corners
            && corners is not ("default" or "square" or "round" or "round_small"))
        {
            issues.Add(ValidationIssue.Error(
                at + ".corners", $"'{corners}' is not default, square, round or round_small"));
        }

        if (effects.TitleBar is { Length: > 0 } bar && bar is not ("keep" or "hide"))
        {
            issues.Add(ValidationIssue.Error(at + ".title_bar", $"'{bar}' is not keep or hide"));
        }

        if (effects.Opacity is { } opacity && opacity is < 0.05 or > 1)
        {
            // Not zero: a window at zero is optimised away by the compositor
            // and cannot be clicked, which reads as a window that vanished.
            issues.Add(ValidationIssue.Error(
                at + ".opacity", $"{opacity} is outside 0.05 to 1"));
        }
    }

    private static void CheckColour(string? value, string path, List<ValidationIssue> issues)
    {
        // "none" is a choice, and a different one from absent: absent means the
        // layer below decides, which is what null means everywhere in this
        // file. Without the word a GUI could never clear a border -- clearing
        // the field would inherit the default colour straight back.
        if (value is { Length: > 0 } and not "none" && !Colour.IsMatch(value))
        {
            issues.Add(ValidationIssue.Error(path, $"'{value}' is not a #rrggbb colour or 'none'"));
        }
    }

    private static void ValidateLayout(LayoutConfig? layout, List<ValidationIssue> issues)
    {
        if (layout is null)
        {
            return;
        }

        if (layout.DefaultDirection is { Length: > 0 } direction
            && direction is not ("auto" or "horizontal" or "vertical"))
        {
            issues.Add(ValidationIssue.Error(
                "layout.default_direction", $"'{direction}' is not auto, horizontal or vertical"));
        }

        if (layout.ResizeStepPpt is < 1 or > 50)
        {
            issues.Add(ValidationIssue.Error("layout.resize_step_ppt", "must be between 1 and 50"));
        }

        if (layout.DragToTop is { Length: > 0 } drag
            && drag.ToLowerInvariant() is not ("fullscreen" or "float_maximized" or "float-maximized"
                or "float_fullscreen" or "float-fullscreen" or "none"))
        {
            issues.Add(ValidationIssue.Error(
                "layout.drag_to_top",
                $"'{drag}' is not fullscreen, float_maximized, float_fullscreen or none"));
        }
    }

    private static void ValidateMonitors(AkuWmConfig config, List<ValidationIssue> issues)
    {
        List<MonitorConfig> monitors = config.Monitors ?? [];
        CheckDuplicateIds(monitors, "monitors", issues);

        for (int i = 0; i < monitors.Count; i++)
        {
            MonitorConfig monitor = monitors[i];
            string path = $"monitors[{i}]";

            ValidateGapsAt(monitor.Gaps, path + ".gaps", issues);

            if (string.IsNullOrWhiteSpace(monitor.Id))
            {
                issues.Add(ValidationIssue.Error(path + ".id", "a monitor needs a role id"));
                continue;
            }

            // No list of blessed names. A role is whatever this desk calls a
            // screen, and a fourth or a fifth one is a person buying a
            // monitor, not a mistake. The four AkuWM ships are a starting
            // point, not a vocabulary -- warning about anything else would
            // have made "however many screens I like" a warning per start.

            bool identified = monitor.Match is not null &&
                              (monitor.Match.Edid is { Length: > 0 }
                               || monitor.Match.Name is { Length: > 0 }
                               || monitor.Match.DevicePath is { Length: > 0 });
            if (!identified)
            {
                issues.Add(ValidationIssue.Warning(
                    path + ".match",
                    "no identity: this role can only be assigned by position, which a sleep cycle can change"));
            }
        }

        if (monitors.Count(m => m.Primary == true) > 1)
        {
            issues.Add(ValidationIssue.Error("monitors", "more than one monitor is marked primary"));
        }
    }

    private static void ValidateWorkspaces(AkuWmConfig config, List<ValidationIssue> issues)
    {
        List<WorkspaceConfig> workspaces = config.Workspaces ?? [];
        var roles = new HashSet<string>(
            (config.Monitors ?? []).Select(m => m.Id ?? string.Empty), StringComparer.Ordinal);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < workspaces.Count; i++)
        {
            WorkspaceConfig workspace = workspaces[i];
            string path = $"workspaces[{i}]";

            if (string.IsNullOrWhiteSpace(workspace.Name))
            {
                issues.Add(ValidationIssue.Error(path + ".name", "a workspace needs a name"));
                continue;
            }

            if (!seen.Add(workspace.Name))
            {
                issues.Add(ValidationIssue.Error(path + ".name", $"'{workspace.Name}' is declared twice"));
            }

            if (string.IsNullOrWhiteSpace(workspace.Monitor))
            {
                issues.Add(ValidationIssue.Error(path + ".monitor", "a workspace needs a monitor role"));
            }
            else if (roles.Count > 0 && !roles.Contains(workspace.Monitor))
            {
                issues.Add(ValidationIssue.Error(
                    path + ".monitor", $"'{workspace.Monitor}' is not a declared monitor role"));
            }

            if (workspace.Direction is { Length: > 0 } direction
                && direction is not ("auto" or "horizontal" or "vertical"))
            {
                issues.Add(ValidationIssue.Error(
                    path + ".direction", $"'{direction}' is not auto, horizontal or vertical"));
            }
        }

        if (workspaces.Count > 0 && roles.Count == 0)
        {
            issues.Add(ValidationIssue.Warning(
                "monitors",
                "no monitors declared: workspace roles cannot be resolved by identity yet"));
        }
    }

    private static void ValidateRules(AkuWmConfig config, List<ValidationIssue> issues)
    {
        List<RuleConfig> rules = config.Rules ?? [];
        CheckDuplicateIds(rules, "rules", issues);

        var workspaceNames = new HashSet<string>(
            (config.Workspaces ?? []).Select(w => w.Name ?? string.Empty), StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < rules.Count; i++)
        {
            RuleConfig rule = rules[i];
            string path = $"rules[{i}]";

            if (string.IsNullOrWhiteSpace(rule.Id))
            {
                issues.Add(ValidationIssue.Error(path + ".id", "a rule needs an id"));
            }

            if (rule.Match is not { Count: > 0 })
            {
                issues.Add(ValidationIssue.Error(path + ".match", "a rule that matches nothing does nothing"));
            }
            else
            {
                for (int m = 0; m < rule.Match.Count; m++)
                {
                    ValidateCriteria(rule.Match[m], $"{path}.match[{m}]", issues);
                }
            }

            ValidateEffectsAt(rule.Effects, path + ".effects", issues);

            // A rule may now say only how a window should LOOK, with no verb
            // at all: "every Zen window at 90% opacity" is a whole rule.
            if (rule.Actions is not { Count: > 0 } actions)
            {
                if (rule.Effects is null)
                {
                    issues.Add(ValidationIssue.Error(
                        path + ".actions", "a rule needs at least one action, or an effects block"));
                }
            }
            else
            {
                foreach (string action in actions)
                {
                    if (!ConfigDefaults.RuleActions.Contains(action))
                    {
                        issues.Add(ValidationIssue.Error(
                            path + ".actions",
                            $"'{action}' is not an action AkuWM knows ({string.Join(", ", ConfigDefaults.RuleActions)})"));
                    }
                }

                if (rule.Actions.Contains("ignore") && rule.Actions.Count > 1)
                {
                    issues.Add(ValidationIssue.Warning(
                        path + ".actions",
                        "'ignore' takes the window out of the manager, so the other actions never run"));
                }

                if (rule.Actions.Contains("float") && rule.Actions.Contains("tile"))
                {
                    issues.Add(ValidationIssue.Error(path + ".actions", "'float' and 'tile' contradict each other"));
                }
            }

            if (rule.Target is { } target)
            {
                if (target.Workspace is { Length: > 0 } name
                    && workspaceNames.Count > 0 && !workspaceNames.Contains(name))
                {
                    issues.Add(ValidationIssue.Error(
                        path + ".target.workspace", $"'{name}' is not a declared workspace"));
                }

                // Counted, not assumed. Ten was a literal, and a person with
                // twelve workspaces on a screen could not aim a rule at the
                // eleventh -- for no reason except that nobody had thought
                // about it.
                if (target.Slot is { } slot)
                {
                    int available = target.Monitor is { Length: > 0 } role
                        ? Slots(config, role)
                        : workspaceNames.Count;

                    if (slot < 1 || (available > 0 && slot > available))
                    {
                        issues.Add(ValidationIssue.Error(
                            path + ".target.slot",
                            available > 0
                                ? $"is 1 to {available} on '{target.Monitor ?? "this desk"}'"
                                : "starts at 1"));
                    }
                }
            }
        }
    }

    /// <summary>How many workspaces one screen has been given.</summary>
    private static int Slots(AkuWmConfig config, string role)
    {
        int count = 0;

        foreach (WorkspaceConfig workspace in config.Workspaces ?? [])
        {
            if (workspace.Enabled != false
                && string.Equals(workspace.Monitor, role, StringComparison.OrdinalIgnoreCase))
            {
                count++;
            }
        }

        return count;
    }

    private static void ValidateCriteria(MatchCriteria criteria, string path, List<ValidationIssue> issues)
    {
        string?[] values = [criteria.Process, criteria.Class, criteria.Title];
        string[] names = ["process", "class", "title"];

        if (values.All(string.IsNullOrWhiteSpace))
        {
            issues.Add(ValidationIssue.Error(path, "empty: it would match every window"));
            return;
        }

        for (int i = 0; i < values.Length; i++)
        {
            if (values[i] is { Length: > 0 } value
                && !RuleMatcher.IsValidPattern(value, out string? error))
            {
                issues.Add(ValidationIssue.Error($"{path}.{names[i]}", $"bad regular expression: {error}"));
            }
        }
    }

    private static void ValidateShortcuts(AkuWmConfig config, List<ValidationIssue> issues)
    {
        List<ShortcutConfig> shortcuts = config.Shortcuts ?? [];
        CheckDuplicateIds(shortcuts, "shortcuts", issues);

        var bound = new Dictionary<string, int>(StringComparer.Ordinal);

        for (int i = 0; i < shortcuts.Count; i++)
        {
            ShortcutConfig shortcut = shortcuts[i];
            string path = $"shortcuts[{i}]";

            if (string.IsNullOrWhiteSpace(shortcut.Id))
            {
                issues.Add(ValidationIssue.Error(path + ".id", "a shortcut needs an id"));
            }

            string kind = shortcut.Kind ?? "app";
            if (!ConfigDefaults.ShortcutKinds.Contains(kind))
            {
                issues.Add(ValidationIssue.Error(
                    path + ".kind",
                    $"'{kind}' is not {string.Join(", ", ConfigDefaults.ShortcutKinds)}"));
            }

            if (!Chord.TryParse(shortcut.Keys, out Chord chord, out string? chordError))
            {
                issues.Add(ValidationIssue.Error(path + ".keys", chordError ?? "cannot be parsed"));
            }
            else if (shortcut.Enabled != false)
            {
                // Two chords collide only when neither is scoped to a window, or
                // both are scoped to the same one.
                string scope = Describe(shortcut.When);
                string key = chord + "|" + scope;
                if (bound.TryGetValue(key, out int first))
                {
                    issues.Add(ValidationIssue.Error(
                        path + ".keys",
                        $"'{chord}'{(scope.Length > 0 ? " (when " + scope + ")" : string.Empty)} is already bound by shortcuts[{first}]"));
                }
                else
                {
                    bound[key] = i;
                }
            }

            if (string.IsNullOrWhiteSpace(shortcut.Command) && kind != "app")
            {
                issues.Add(ValidationIssue.Error(path + ".command", $"a '{kind}' shortcut needs a command"));
            }

            if (kind == "app" && string.IsNullOrWhiteSpace(shortcut.App))
            {
                issues.Add(ValidationIssue.Error(path + ".app", "an 'app' shortcut needs the process to raise"));
            }
        }
    }

    private static string Describe(MatchCriteria? when) =>
        when is null
            ? string.Empty
            : string.Join(
                ",",
                new[] { ("process", when.Process), ("class", when.Class), ("title", when.Title) }
                    .Where(pair => !string.IsNullOrWhiteSpace(pair.Item2))
                    .Select(pair => $"{pair.Item1}={pair.Item2}"));

    private static void ValidateStartup(AkuWmConfig config, List<ValidationIssue> issues)
    {
        List<StartupConfig> startup = config.Startup ?? [];
        CheckDuplicateIds(startup, "startup", issues);

        for (int i = 0; i < startup.Count; i++)
        {
            StartupConfig entry = startup[i];
            string path = $"startup[{i}]";

            if (string.IsNullOrWhiteSpace(entry.Command))
            {
                issues.Add(ValidationIssue.Error(path + ".command", "a startup entry needs a command"));
            }

            if (entry.After is { Length: > 0 } after && !ConfigDefaults.StartupPhases.Contains(after))
            {
                issues.Add(ValidationIssue.Error(
                    path + ".after", $"'{after}' is not {string.Join(" or ", ConfigDefaults.StartupPhases)}"));
            }

            if (entry.DelayMs is < 0)
            {
                issues.Add(ValidationIssue.Error(path + ".delay_ms", "cannot be negative"));
            }
        }
    }

    private static void ValidateSettings(SettingsConfig? settings, List<ValidationIssue> issues)
    {
        if (settings?.Journal?.IntervalS is < 5)
        {
            issues.Add(ValidationIssue.Error(
                "settings.journal.interval_s", "under 5 s the journal writes more than it protects"));
        }

        if (settings?.Repair?.SettleMs is < 0)
        {
            issues.Add(ValidationIssue.Error("settings.repair.settle_ms", "cannot be negative"));
        }
    }

    private static void CheckDuplicateIds<T>(List<T> items, string path, List<ValidationIssue> issues)
        where T : IConfigItem
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < items.Count; i++)
        {
            string? id = items[i].Id;
            if (id is { Length: > 0 } && !seen.Add(id))
            {
                issues.Add(ValidationIssue.Error(
                    $"{path}[{i}].id", $"'{id}' is used twice; the machine layer would override both"));
            }
        }
    }
}
