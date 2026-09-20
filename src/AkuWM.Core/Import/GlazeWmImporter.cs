using AkuWM.Core.Config;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace AkuWM.Core.Import;

/// <summary>
/// Reads the GlazeWM configuration this desk runs today and writes the same
/// intent as AkuWM configuration.
/// </summary>
/// <remarks>
/// <para>
/// A one-way, one-time job: nothing is designed twice, and the first
/// <c>common.json</c> is provably the desk that already works (21 rules, 20
/// workspaces). Only the YAML's <em>data</em> is read -- the file is Diego's
/// own configuration, not GlazeWM's source, and the mapping below is the
/// plan's own vocabulary, which is why nothing GPL comes across.
/// </para>
/// <para>
/// What is deliberately not carried over: the setting that chose how a window
/// is hidden (AkuWM always cloaks and reads the flag back), the per-state
/// defaults that only make
/// sense for GlazeWM's own fullscreen classifier, and
/// <c>focus_follows_cursor</c> -- that one is off in the YAML because
/// GlazeWM's implementation did not work on this desk, while AkuWM uses the
/// native tracking, which does.
/// </para>
/// </remarks>
public static class GlazeWmImporter
{
    /// <summary>GlazeWM command → AkuWM rule action.</summary>
    private static readonly Dictionary<string, string> Actions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ignore"] = "ignore",
        ["set-floating"] = "float",
        ["set-tiling"] = "tile",
        ["set-sticky"] = "sticky",
        ["unset-sticky"] = "unsticky",
        ["set-fullscreen"] = "fullscreen",
        ["set-minimized"] = "minimize",
    };

    /// <summary>Monitor index in the YAML → the role AkuWM binds workspaces to.</summary>
    private static readonly string[] RoleByIndex = ["main", "second", "tv", "left"];

    public static AkuWmConfig Import(string yaml, ImportSummary summary)
    {
        GlazeConfig parsed = Parse(yaml);
        long now = ConfigStore.Now();

        var config = new AkuWmConfig
        {
            Version = ConfigDefaults.SchemaVersion,
            General = ImportGeneral(parsed, summary),
            Gaps = ImportGaps(parsed.Gaps),
            Effects = ImportEffects(parsed.WindowEffects),
            Layout = new LayoutConfig { DefaultDirection = "auto" },
            Workspaces = ImportWorkspaces(parsed.Workspaces, summary),
            Rules = ImportRules(parsed.WindowRules, now, summary),
        };

        config.Monitors = MonitorRolesFor(config.Workspaces, now, summary);

        summary.Workspaces = config.Workspaces?.Count ?? 0;
        summary.Rules = config.Rules?.Count ?? 0;
        summary.Monitors = config.Monitors?.Count ?? 0;
        return config;
    }

    private static GlazeConfig Parse(string yaml)
    {
        try
        {
            IDeserializer deserializer = new DeserializerBuilder()
                .WithNamingConvention(UnderscoredNamingConvention.Instance)
                .IgnoreUnmatchedProperties()
                .Build();
            return deserializer.Deserialize<GlazeConfig>(yaml) ?? new GlazeConfig();
        }
        catch (Exception ex)
        {
            throw new ConfigException($"the GlazeWM config could not be parsed: {ex.Message}", ex);
        }
    }

    private static GeneralConfig ImportGeneral(GlazeConfig parsed, ImportSummary summary)
    {
        GlazeGeneral general = parsed.General ?? new GlazeGeneral();

        bool foldsDesktops =
            (general.StartupCommands ?? []).Any(c => c.Contains("vd-merge", StringComparison.OrdinalIgnoreCase));
        if (foldsDesktops)
        {
            summary.Notes.Add(
                "startup_commands ran vd-merge.ahk; kept as general.startup_fold_virtual_desktops");
        }

        if (general.FocusFollowsCursor == false)
        {
            summary.Notes.Add(
                "focus_follows_cursor was off (GlazeWM's own implementation did not work here); " +
                "AkuWM keeps the native tracking instead, so focus_follows_mouse is on");
        }

        return new GeneralConfig
        {
            ToggleWorkspaceOnRefocus = general.ToggleWorkspaceOnRefocus ?? true,
            FocusFollowsMouse = true,
            CursorJump = general.CursorJump?.Enabled == true
                ? general.CursorJump.Trigger ?? "monitor_focus"
                : "off",
            ShowAllInTaskbar = general.ShowAllInTaskbar ?? false,
            StartupFoldVirtualDesktops = foldsDesktops,
        };
    }

    private static GapsConfig ImportGaps(GlazeGaps? gaps)
    {
        if (gaps is null)
        {
            return new GapsConfig();
        }

        return new GapsConfig
        {
            Inner = Pixels(gaps.InnerGap) ?? 8,
            ScaleWithDpi = gaps.ScaleWithDpi ?? true,
            Outer =
            [
                Pixels(gaps.OuterGap?.Top) ?? 0,
                Pixels(gaps.OuterGap?.Right) ?? 0,
                Pixels(gaps.OuterGap?.Bottom) ?? 0,
                Pixels(gaps.OuterGap?.Left) ?? 0,
            ],
        };
    }

    /// <summary>'8px' → 8. GlazeWM also writes plain numbers.</summary>
    internal static int? Pixels(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string digits = value.Trim();
        if (digits.EndsWith("px", StringComparison.OrdinalIgnoreCase))
        {
            digits = digits[..^2];
        }

        return int.TryParse(digits.Trim(), out int pixels) ? pixels : null;
    }

    private static EffectsConfig ImportEffects(GlazeEffects? effects) => new()
    {
        FocusedBorder = effects?.FocusedWindow?.Border is { Enabled: true, Color: { Length: > 0 } colour }
            ? colour
            : null,
        OtherBorder = effects?.OtherWindows?.Border is { Enabled: true, Color: { Length: > 0 } other }
            ? other
            : null,
    };

    private static List<WorkspaceConfig> ImportWorkspaces(
        List<GlazeWorkspace>? workspaces, ImportSummary summary)
    {
        var result = new List<WorkspaceConfig>();
        foreach (GlazeWorkspace workspace in workspaces ?? [])
        {
            if (string.IsNullOrWhiteSpace(workspace.Name))
            {
                continue;
            }

            int index = workspace.BindToMonitor ?? 0;
            string role = index >= 0 && index < RoleByIndex.Length
                ? RoleByIndex[index]
                : $"monitor{index}";

            result.Add(new WorkspaceConfig
            {
                Name = workspace.Name,
                Monitor = role,
                KeepAlive = workspace.KeepAlive,
                DisplayName = string.IsNullOrWhiteSpace(workspace.DisplayName) ? null : workspace.DisplayName,
            });
        }

        if (result.Count > 0)
        {
            summary.Notes.Add(
                "workspaces were bound to monitor indexes; they are bound to roles now " +
                $"({string.Join(", ", result.Select(w => w.Monitor).Distinct())})");
        }

        return result;
    }

    private static List<RuleConfig> ImportRules(
        List<GlazeRule>? rules, long now, ImportSummary summary)
    {
        var result = new List<RuleConfig>();

        foreach (GlazeRule rule in rules ?? [])
        {
            List<string> actions = [];
            foreach (string command in rule.Commands ?? [])
            {
                string verb = command.Trim();
                if (Actions.TryGetValue(verb, out string? action))
                {
                    actions.Add(action);
                }
                else
                {
                    summary.Notes.Add($"rule command '{verb}' has no AkuWM action and was dropped");
                }
            }

            if (actions.Count == 0)
            {
                continue;
            }

            // Every alternative of a GlazeWM rule becomes a rule of its own:
            // they are edited, enabled and overridden one by one in the GUI,
            // which a shared list of alternatives would not allow.
            foreach (Dictionary<string, GlazeMatchValue> alternative in rule.Match ?? [])
            {
                MatchCriteria criteria = Criteria(alternative);
                if (criteria is { Process: null, Class: null, Title: null })
                {
                    continue;
                }

                result.Add(new RuleConfig
                {
                    Id = Ids.New("r"),
                    Name = NameFor(criteria),
                    Match = [criteria],
                    Actions = [.. actions],
                    Enabled = true,
                    Notes = "imported from glazewm/config.yaml",
                    UpdatedAt = now,
                });
            }
        }

        return result;
    }

    private static MatchCriteria Criteria(Dictionary<string, GlazeMatchValue> alternative)
    {
        var criteria = new MatchCriteria();
        foreach ((string key, GlazeMatchValue value) in alternative)
        {
            string? pattern = value.Exact is { Length: > 0 } exact
                ? exact
                : value.Regex is { Length: > 0 } regex
                    ? Matching.RuleMatcher.RegexPrefix + regex
                    : null;

            if (pattern is null)
            {
                continue;
            }

            switch (key.ToLowerInvariant())
            {
                case "window_process":
                    criteria.Process = pattern;
                    break;
                case "window_class":
                    criteria.Class = pattern;
                    break;
                case "window_title":
                    criteria.Title = pattern;
                    break;
            }
        }

        return criteria;
    }

    /// <summary>A name a human recognises in the list: the process, else the title, else the class.</summary>
    internal static string NameFor(MatchCriteria criteria)
    {
        string? name = criteria.Process ?? criteria.Title ?? criteria.Class;
        if (name is null)
        {
            return "rule";
        }

        if (name.StartsWith(Matching.RuleMatcher.RegexPrefix, StringComparison.Ordinal))
        {
            name = name[Matching.RuleMatcher.RegexPrefix.Length..];
        }

        return name;
    }

    private static List<MonitorConfig> MonitorRolesFor(
        List<WorkspaceConfig>? workspaces, long now, ImportSummary summary)
    {
        var roles = (workspaces ?? [])
            .Select(w => w.Monitor)
            .Where(role => !string.IsNullOrWhiteSpace(role))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (roles.Count > 0)
        {
            summary.Notes.Add(
                "monitor roles have no identity yet: run `akuwm monitors identify` on the desk " +
                "to fill in the EDID of each one (until then a role is matched by position)");
        }

        return [.. roles.Select((role, index) => new MonitorConfig
        {
            Id = role,
            Primary = index == 0 ? true : null,
            Enabled = true,
            Notes = "role imported from the workspace bindings; identity still to be filled in",
            UpdatedAt = now,
        })];
    }

    // ---- the shape of the YAML that is read, and nothing more ----------------

    private sealed class GlazeConfig
    {
        public GlazeGeneral? General { get; set; }
        public GlazeGaps? Gaps { get; set; }
        public GlazeEffects? WindowEffects { get; set; }
        public List<GlazeWorkspace>? Workspaces { get; set; }
        public List<GlazeRule>? WindowRules { get; set; }
    }

    private sealed class GlazeGeneral
    {
        public List<string>? StartupCommands { get; set; }
        public bool? FocusFollowsCursor { get; set; }
        public bool? ToggleWorkspaceOnRefocus { get; set; }
        public GlazeCursorJump? CursorJump { get; set; }
        public bool? ShowAllInTaskbar { get; set; }
    }

    private sealed class GlazeCursorJump
    {
        public bool? Enabled { get; set; }
        public string? Trigger { get; set; }
    }

    private sealed class GlazeGaps
    {
        public bool? ScaleWithDpi { get; set; }
        public string? InnerGap { get; set; }
        public GlazeOuterGap? OuterGap { get; set; }
    }

    private sealed class GlazeOuterGap
    {
        public string? Top { get; set; }
        public string? Right { get; set; }
        public string? Bottom { get; set; }
        public string? Left { get; set; }
    }

    private sealed class GlazeEffects
    {
        public GlazeWindowEffect? FocusedWindow { get; set; }
        public GlazeWindowEffect? OtherWindows { get; set; }
    }

    private sealed class GlazeWindowEffect
    {
        public GlazeBorder? Border { get; set; }
    }

    private sealed class GlazeBorder
    {
        public bool Enabled { get; set; }
        public string? Color { get; set; }
    }

    private sealed class GlazeWorkspace
    {
        public string? Name { get; set; }
        public string? DisplayName { get; set; }
        public int? BindToMonitor { get; set; }
        public bool? KeepAlive { get; set; }
    }

    private sealed class GlazeRule
    {
        public List<string>? Commands { get; set; }
        public List<Dictionary<string, GlazeMatchValue>>? Match { get; set; }
    }

    private sealed class GlazeMatchValue
    {
        [YamlMember(Alias = "equals")]
        public string? Exact { get; set; }

        [YamlMember(Alias = "regex")]
        public string? Regex { get; set; }
    }
}
