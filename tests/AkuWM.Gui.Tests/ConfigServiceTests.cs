using System.Text.Json.Nodes;
using AkuWM.Gui.Services;
using Xunit;

namespace AkuWM.Gui.Tests;

public class ConfigServiceTests : IDisposable
{
    private readonly Fixture _f = new();

    public void Dispose() => _f.Dispose();

    [Fact]
    public void Effective_merges_by_id_and_names_the_layer_to_write()
    {
        List<(JsonObject Item, string Layer)> rules = _f.Services.Config.Effective("rules");
        Assert.Equal(["r-tg", "r-zebar", "r-only"], rules.Select(r => ConfigService.IdOf(r.Item)));
        Assert.Equal(["profile", "common", "profile"], rules.Select(r => r.Layer));
        JsonObject telegram = rules[0].Item;
        Assert.False(ConfigService.Flag(telegram, "enabled"));
        Assert.Equal("Telegram", ConfigService.Str(telegram, "name"));
        Assert.Equal("off on this box", ConfigService.Str(telegram, "notes"));
    }

    [Fact]
    public void Save_appends_with_a_minted_id_and_keeps_unknown_keys_of_the_others()
    {
        var rule = new JsonObject { ["name"] = "Calc", ["match"] = new JsonArray(new JsonObject { ["process"] = "Calculator" }), ["actions"] = new JsonArray("float") };
        Assert.Null(_f.Services.Config.Save("common", "rules", rule, "r"));
        string id = ConfigService.IdOf(rule)!;
        Assert.StartsWith("r-", id);
        JsonArray written = (JsonArray)_f.CommonJson()["rules"]!;
        Assert.Equal(3, written.Count);
        Assert.Equal("kept", written[0]!["future_key"]!.ToString());
        Assert.Equal(id, written[2]!["id"]!.ToString());
        Assert.True(written[2]!["updated_at"]!.GetValue<long>() > 0);
    }

    [Fact]
    public void Save_replaces_by_id_in_place()
    {
        var rule = new JsonObject { ["id"] = "r-zebar", ["name"] = "Zebar bar", ["match"] = new JsonArray(new JsonObject { ["process"] = "zebar" }), ["actions"] = new JsonArray("ignore") };
        Assert.Null(_f.Services.Config.Save("common", "rules", rule, "r"));
        JsonArray written = (JsonArray)_f.CommonJson()["rules"]!;
        Assert.Equal(2, written.Count);
        Assert.Equal("Zebar bar", written[1]!["name"]!.ToString());
    }

    [Fact]
    public void Save_refuses_what_the_validator_refuses()
    {
        var rule = new JsonObject { ["name"] = "Bad", ["match"] = new JsonArray(new JsonObject { ["process"] = "x" }), ["actions"] = new JsonArray("levitate") };
        string? error = _f.Services.Config.Save("common", "rules", rule, "r");
        Assert.NotNull(error);
        Assert.Contains("levitate", error);
        Assert.Equal(2, ((JsonArray)_f.CommonJson()["rules"]!).Count);
    }

    [Fact]
    public void Remove_and_reorder_touch_one_layer()
    {
        Assert.Null(_f.Services.Config.Remove("profile", "rules", "r-only"));
        Assert.Single((JsonArray)_f.ProfileJson()["rules"]!);
        Assert.Equal(2, ((JsonArray)_f.CommonJson()["rules"]!).Count);
        Assert.NotNull(_f.Services.Config.Remove("profile", "rules", "r-zebar"));

        Assert.Null(_f.Services.Config.Reorder("common", "startup", ["u-b", "u-a"]));
        JsonArray startup = (JsonArray)_f.CommonJson()["startup"]!;
        Assert.Equal(["u-b", "u-a"], startup.Select(s => s!["id"]!.ToString()));
    }

    [Fact]
    public void Reload_tells_the_daemon_and_the_bindings_when_asked()
    {
        Assert.Null(ConfigService.Reload(_f.Daemon, bindings: true));
        Assert.Equal(["compat command wm-reload-config", "bindings reload"], _f.Daemon.Sent);
        _f.Daemon.Running = false;
        Assert.Contains("not running", ConfigService.Reload(_f.Daemon, bindings: false));
    }

    [Fact]
    public void Split_takes_a_quoted_program()
    {
        Assert.Equal(("C:\\Program Files\\ShareX\\ShareX.exe", "-workflow \"Hyper+Shift+C\""), ProcessRunner.Split("\"C:\\Program Files\\ShareX\\ShareX.exe\" -workflow \"Hyper+Shift+C\""));
        Assert.Equal(("cmd.exe", "/k winget list"), ProcessRunner.Split("cmd.exe /k winget list"));
        Assert.Equal(("ms-settings:display", string.Empty), ProcessRunner.Split(" ms-settings:display "));
    }
}
