using System.Text.Json.Nodes;
using AkuWM.Core.Config;
using Xunit;

namespace AkuWM.Tests;

public class ConfigStoreTests
{
    [Fact]
    public void KeysAreWrittenInSnakeCase()
    {
        var config = new AkuWmConfig
        {
            Version = 1,
            General = new GeneralConfig { ToggleWorkspaceOnRefocus = true },
            Startup = [new StartupConfig { Id = "u-1", Command = "x", DelayMs = 250 }],
        };

        JsonNode json = JsonNode.Parse(ConfigJson.Write(config))!;

        Assert.True(json["general"]!["toggle_workspace_on_refocus"]!.GetValue<bool>());
        Assert.Equal(250, json["startup"]![0]!["delay_ms"]!.GetValue<int>());
    }

    [Fact]
    public void AbsentFieldsAreNotWrittenAtAll()
    {
        string json = ConfigJson.Write(new AkuWmConfig { Version = 1 });

        Assert.DoesNotContain("null", json, StringComparison.Ordinal);
        Assert.DoesNotContain("general", json, StringComparison.Ordinal);
    }

    [Fact]
    public void ApostrophesStayReadable()
    {
        string json = ConfigJson.Write(new AkuWmConfig
        {
            Rules = [new RuleConfig { Id = "r-1", Notes = "GlazeWM's own rule" }],
        });

        Assert.Contains("GlazeWM's own rule", json, StringComparison.Ordinal);
    }

    [Fact]
    public void SaveAndLoadAreARoundTrip()
    {
        using var dir = new TempDir();
        var paths = new ConfigPaths(dir.Path, "TEST", dir.Path);

        var common = new AkuWmConfig
        {
            Version = 1,
            Monitors = [new MonitorConfig { Id = "main", Match = new MonitorMatch { Edid = "SAM0F1E" } }],
            Workspaces = [new WorkspaceConfig { Name = "11", Monitor = "main" }],
            Rules = [new RuleConfig { Id = "r-1", Match = [new MatchCriteria { Process = "Telegram" }], Actions = ["float"] }],
        };
        ConfigStore.Save(paths.CommonFile, common);

        LoadedConfig loaded = ConfigStore.Load(paths);

        Assert.True(loaded.CommonExists);
        Assert.False(loaded.ProfileExists);
        Assert.Equal("SAM0F1E", loaded.Effective.Monitors!.Single().Match!.Edid);
        Assert.Equal("Telegram", loaded.Effective.Rules!.Single().Match!.Single().Process);
        Assert.True(loaded.Validation.Ok);
    }

    [Fact]
    public void TheMachineLayerIsPutOnTopOfTheCommonOne()
    {
        using var dir = new TempDir();
        var paths = new ConfigPaths(dir.Path, "DESK_W11", dir.Path);

        ConfigStore.Save(paths.CommonFile, new AkuWmConfig
        {
            Version = 1,
            Gaps = new GapsConfig { Inner = 8 },
            Monitors = [new MonitorConfig { Id = "main" }],
            Workspaces = [new WorkspaceConfig { Name = "11", Monitor = "main" }],
        });
        ConfigStore.Save(paths.ProfileFile, new AkuWmConfig
        {
            Version = 1,
            Monitors = [new MonitorConfig { Id = "main", Match = new MonitorMatch { Edid = "DEL4231" } }],
        });

        LoadedConfig loaded = ConfigStore.Load(paths);

        Assert.Equal(8, loaded.Effective.Gaps!.Inner);
        Assert.Equal("DEL4231", loaded.Effective.Monitors!.Single().Match!.Edid);
    }

    [Fact]
    public void AWriteIsAtomicAndLeavesNoTemporaryBehind()
    {
        using var dir = new TempDir();
        string file = dir.File("common.json");

        ConfigStore.Save(file, new AkuWmConfig { Version = 1 });
        ConfigStore.Save(file, new AkuWmConfig { Version = 1, Gaps = new GapsConfig { Inner = 4 } });

        Assert.Equal(["common.json"], Directory.GetFiles(dir.Path).Select(Path.GetFileName));
        Assert.Equal(4, ConfigStore.ReadIfPresent(file)!.Gaps!.Inner);
    }

    [Fact]
    public void BrokenJsonSaysWhichFileItWas()
    {
        using var dir = new TempDir();
        string file = dir.File("common.json");
        File.WriteAllText(file, "{ not json");

        ConfigException error = Assert.Throws<ConfigException>(() => ConfigStore.ReadIfPresent(file));
        Assert.Contains("common.json", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PathsFollowTheEnvironment()
    {
        var environment = new Dictionary<string, string?>
        {
            ["AKUWM_STATE_DIR"] = "/somewhere/akuwm",
            ["ENV_PROFILE"] = "DESK_W11",
            ["XDG_STATE_HOME"] = "/home/x/.local/state",
        };

        ConfigPaths paths = ConfigPaths.Discover(name => environment.GetValueOrDefault(name));

        Assert.Equal("/somewhere/akuwm", paths.ConfigDir);
        Assert.Equal("DESK_W11", paths.Profile);
        Assert.EndsWith("DESK_W11.json", paths.ProfileFile, StringComparison.Ordinal);
    }
}

public class ConfigStoreNewlineTests
{
    [Fact]
    public void A_save_keeps_the_files_line_ending_and_defaults_to_lf()
    {
        string root = Path.Combine(Path.GetTempPath(), "akuwm-eol-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string lf = Path.Combine(root, "lf.json");
            File.WriteAllText(lf, "{\n  \"version\": 1\n}\n");
            ConfigStore.SaveText(lf, "{\r\n  \"version\": 2\r\n}");
            Assert.Equal("{\n  \"version\": 2\n}\n", File.ReadAllText(lf));

            string crlf = Path.Combine(root, "crlf.json");
            File.WriteAllText(crlf, "{\r\n  \"version\": 1\r\n}\r\n");
            ConfigStore.SaveText(crlf, "{\n  \"version\": 2\n}");
            Assert.Equal("{\r\n  \"version\": 2\r\n}\r\n", File.ReadAllText(crlf));

            string fresh = Path.Combine(root, "fresh.json");
            ConfigStore.SaveText(fresh, "{\r\n}");
            Assert.Equal("{\n}\n", File.ReadAllText(fresh));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
