using System.Text.Json.Nodes;
using AkuWM.Core.Config;
using Xunit;

namespace AkuWM.Tests;

/// <summary>
/// The write surface. Everything could be read and validated and nothing could
/// be saved, so a GUI would have had to write the file itself -- a second
/// implementation of the schema, in the one place where being out of step is
/// silent.
/// </summary>
public class ConfigEditTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly ConfigPaths _paths;

    public ConfigEditTests()
    {
        _paths = new ConfigPaths(_dir.Path, "TEST", _dir.Path);
        ConfigStore.Save(_paths.CommonFile, ConfigDefaults.Create());
    }

    public void Dispose()
    {
        _dir.Dispose();
        GC.SuppressFinalize(this);
    }

    private JsonObject Common() =>
        (JsonObject)JsonNode.Parse(File.ReadAllText(_paths.CommonFile))!;

    [Fact]
    public void A_number_is_written_as_a_number()
    {
        ConfigEdit.Result result = ConfigEdit.Apply(_paths, _paths.CommonFile, "gaps.inner", "14", remove: false);

        Assert.Null(result.Error);
        Assert.Equal(14, (int)Common()["gaps"]!["inner"]!);
    }

    [Fact]
    public void A_boolean_is_written_as_a_boolean_not_a_string()
    {
        ConfigEdit.Apply(_paths, _paths.CommonFile, "general.show_all_in_taskbar", "true", remove: false);

        Assert.True((bool)Common()["general"]!["show_all_in_taskbar"]!);
    }

    [Fact]
    public void An_array_can_be_written_whole()
    {
        ConfigEdit.Apply(_paths, _paths.CommonFile, "gaps.outer", "[4,5,6,7]", remove: false);

        Assert.Equal([4, 5, 6, 7], Common()["gaps"]!["outer"]!.AsArray().Select(n => (int)n!));
    }

    [Fact]
    public void Unset_removes_the_key_rather_than_writing_null()
    {
        ConfigEdit.Apply(_paths, _paths.CommonFile, "gaps.inner", "9", remove: false);
        ConfigEdit.Apply(_paths, _paths.CommonFile, "gaps.inner", null, remove: true);

        // Absent and null are different: absent inherits from the layer below,
        // which is how a profile stops overriding something.
        Assert.False(Common()["gaps"]!.AsObject().ContainsKey("inner"));
    }

    [Fact]
    public void A_value_the_validator_refuses_is_not_written()
    {
        ConfigEdit.Result result = ConfigEdit.Apply(
            _paths, _paths.CommonFile, "effects.focused_border", "not-a-colour", remove: false);

        Assert.NotNull(result.Error);
        Assert.NotEqual(
            "not-a-colour",
            (string?)Common()["effects"]?["focused_border"]);
    }

    [Fact]
    public void A_key_this_build_has_never_heard_of_survives_the_edit()
    {
        JsonObject file = Common();
        file["somethingFromTheFuture"] = 42;
        ConfigStore.SaveText(_paths.CommonFile, file.ToJsonString());

        ConfigEdit.Apply(_paths, _paths.CommonFile, "gaps.inner", "6", remove: false);

        // Round-tripping the typed model drops unknown keys on read and never
        // writes them back -- a file from a newer AkuWM would be truncated the
        // first time the GUI saved anything.
        Assert.Equal(42, (int)Common()["somethingFromTheFuture"]!);
    }

    [Fact]
    public void A_path_whose_parents_do_not_exist_yet_is_made()
    {
        ConfigEdit.Apply(_paths, _paths.CommonFile, "effects.corners", "round", remove: false);

        Assert.Equal("round", (string?)Common()["effects"]!["corners"]);
    }

    [Fact]
    public void A_misspelled_key_is_refused_rather_than_created_beside_the_real_one()
    {
        // The typo trap, and the reason this check exists: the file is
        // snake_case and nothing else in the system is, so `gaps.innner` --
        // or `gaps.inner` written as `gaps.Inner` -- used to be accepted,
        // preserved, validated clean, and do nothing for ever.
        ConfigEdit.Result result = ConfigEdit.Apply(
            _paths, _paths.CommonFile, "gaps.innner", "8", remove: false);

        Assert.NotNull(result.Error);
        Assert.Contains("no 'gaps.innner'", result.Error, StringComparison.Ordinal);
        Assert.False(Common()["gaps"]!.AsObject().ContainsKey("innner"));
    }

    [Fact]
    public void A_camel_cased_name_is_refused_too()
    {
        ConfigEdit.Result result = ConfigEdit.Apply(
            _paths, _paths.CommonFile, "effects.focusedBorder", "#ff0000", remove: false);

        Assert.NotNull(result.Error);
        Assert.Equal("#c4a7e7", (string?)Common()["effects"]!["focused_border"]);
    }

    [Fact]
    public void The_result_says_what_it_replaced()
    {
        ConfigEdit.Apply(_paths, _paths.CommonFile, "gaps.inner", "3", remove: false);
        ConfigEdit.Result second = ConfigEdit.Apply(
            _paths, _paths.CommonFile, "gaps.inner", "11", remove: false);

        Assert.Equal("3", second.Was);
        Assert.Equal("11", second.Now);
    }
}
