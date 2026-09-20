using AkuWM.Core.Commands;
using AkuWM.Core.Config;
using AkuWM.Core.Ipc;
using Xunit;

namespace AkuWM.Tests;

/// <summary>
/// The commands that answer without a window manager, which is all of M0.
/// </summary>
public class CommandTests
{
    private static (CommandRouter Router, ConfigPaths Paths) Build(TempDir dir)
    {
        var paths = new ConfigPaths(dir.Path, "TEST", dir.Path);
        return (new CommandRouter(new ConfigCommands(paths), new DoctorCommand(paths, () => false)), paths);
    }

    [Fact]
    public void AnUnknownCommandSaysSoInsteadOfThrowing()
    {
        using var dir = new TempDir();
        CommandResponse response = Build(dir).Router.Execute("wiggle the windows");

        Assert.False(response.Success);
        Assert.Contains("wiggle", response.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public void ConfigPathSaysWhereEverythingIs()
    {
        using var dir = new TempDir();
        (CommandRouter router, ConfigPaths paths) = Build(dir);

        CommandResponse response = router.Execute("config path");

        Assert.True(response.Success);
        Assert.Equal(paths.CommonFile, response.Data!["common"]!.GetValue<string>());
        Assert.False(response.Data["commonExists"]!.GetValue<bool>());
    }

    [Fact]
    public void ImportWritesTheConfigurationAndThenRefusesToOverwriteIt()
    {
        using var dir = new TempDir();
        (CommandRouter router, ConfigPaths paths) = Build(dir);
        string command =
            $"""config import glazewm --from "{Fixture.Path(Fixture.GlazeConfig)}" --ahk "{Fixture.Path(Fixture.AhkToggles)}" --startup-dir "{dir.Path}" """;

        CommandResponse first = router.Execute(command);

        Assert.True(first.Success, first.Error);
        Assert.True(first.Data!["written"]!.GetValue<bool>());
        Assert.Equal(21, first.Data["counts"]!["rules"]!.GetValue<int>());
        Assert.Equal(20, first.Data["counts"]!["workspaces"]!.GetValue<int>());
        Assert.Equal(13, first.Data["counts"]!["shortcuts"]!.GetValue<int>());
        Assert.True(File.Exists(paths.CommonFile));
        Assert.True(File.Exists(paths.ProfileFile));

        CommandResponse second = router.Execute(command);

        Assert.False(second.Success);
        Assert.Contains("--force", second.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public void ADryRunWritesNothing()
    {
        using var dir = new TempDir();
        (CommandRouter router, ConfigPaths paths) = Build(dir);

        CommandResponse response = router.Execute(
            $"""config import glazewm --from "{Fixture.Path(Fixture.GlazeConfig)}" --dry-run""");

        Assert.True(response.Success, response.Error);
        Assert.False(File.Exists(paths.CommonFile));
        Assert.NotNull(response.Data!["preview"]);
    }

    [Fact]
    public void ValidateReportsWhatIsThere()
    {
        using var dir = new TempDir();
        (CommandRouter router, _) = Build(dir);
        router.Execute(
            $"""config import glazewm --from "{Fixture.Path(Fixture.GlazeConfig)}" --ahk "{Fixture.Path(Fixture.AhkToggles)}" --startup-dir "{dir.Path}" """);

        CommandResponse response = router.Execute("config validate");

        Assert.True(response.Success);
        Assert.True(response.Data!["ok"]!.GetValue<bool>());
        Assert.Equal(21, response.Data["rules"]!.GetValue<int>());
        Assert.Equal(0, response.Data["errors"]!.GetValue<int>());
    }

    [Fact]
    public void DoctorFailsWhenThereIsNoConfigurationYet()
    {
        using var dir = new TempDir();
        (CommandRouter router, _) = Build(dir);

        CommandResponse response = router.Execute("doctor");

        Assert.True(response.Success);                       // the command ran
        Assert.False(response.Data!["ok"]!.GetValue<bool>()); // the desk is not ready
        Assert.Contains(
            response.Data["checks"]!.AsArray(),
            check => check!["name"]!.GetValue<string>() == "common.json"
                     && check["status"]!.GetValue<string>() == "fail");
    }

    [Fact]
    public void DoctorPassesOnAnImportedConfiguration()
    {
        using var dir = new TempDir();
        (CommandRouter router, _) = Build(dir);
        router.Execute(
            $"""config import glazewm --from "{Fixture.Path(Fixture.GlazeConfig)}" --ahk "{Fixture.Path(Fixture.AhkToggles)}" --startup-dir "{dir.Path}" """);

        CommandResponse response = router.Execute("doctor");

        Assert.True(response.Data!["ok"]!.GetValue<bool>(),
            response.Data["checks"]!.ToJsonString());
    }

    [Fact]
    public void VersionAnswersWithoutADaemon()
    {
        using var dir = new TempDir();
        CommandResponse response = Build(dir).Router.Execute("version");

        Assert.True(response.Success);
        Assert.False(string.IsNullOrEmpty(response.Data!["version"]!.GetValue<string>()));
    }

    [Fact]
    public void TheCommandsThatNeedNoWindowManagerAreTheOnesM0Has()
    {
        Assert.True(CommandRouter.NeedsNoDaemon("config"));
        Assert.True(CommandRouter.NeedsNoDaemon("doctor"));
        // Only while AkuWM manages nothing: see the remark on NeedsNoDaemon.
        Assert.True(CommandRouter.NeedsNoDaemon("query"));
        Assert.False(CommandRouter.NeedsNoDaemon("command"));
    }
}

/// <summary>
/// The command surface itself: which verbs need a running window manager, and
/// what a caller gets when there is not one.
/// </summary>
public class CommandSurfaceTests
{
    [Fact]
    public void Every_verb_in_the_help_is_a_verb_the_router_knows()
    {
        var router = new CommandRouter(
            new ConfigCommands(new ConfigPaths("/nowhere", "TEST", "/nowhere")),
            new DoctorCommand(new ConfigPaths("/nowhere", "TEST", "/nowhere"), () => false));

        foreach (string line in CommandRouter.Help)
        {
            string verb = line.Split(' ')[0];
            CommandResponse response = router.Execute(verb);

            // It may well refuse for want of a platform or an argument. What
            // it must never say is that it has never heard of it.
            Assert.DoesNotContain("is not a command AkuWM knows", response.Error ?? string.Empty);
        }
    }

    [Fact]
    public void The_compat_surface_says_so_when_nothing_is_managing_the_desk()
    {
        var router = new CommandRouter(
            new ConfigCommands(new ConfigPaths("/nowhere", "TEST", "/nowhere")),
            new DoctorCommand(new ConfigPaths("/nowhere", "TEST", "/nowhere"), () => false));

        CommandResponse response = router.Execute("compat query windows");

        Assert.False(response.Success);
        Assert.Contains("not managing the desk", response.Error);
    }

    [Fact]
    public void Only_rescue_refuses_to_be_handed_to_a_running_daemon()
    {
        foreach (string line in CommandRouter.Help)
        {
            string verb = line.Split(' ')[0];
            Assert.Equal(verb == "rescue", CommandRouter.NeverDelegates(verb));
        }
    }

    [Fact]
    public void The_build_says_which_milestone_it_is()
    {
        // It is the first line of every log and the first line of doctor. A
        // stale one is a small lie told very often.
        Assert.Contains("M2", Build.Description);
    }
}
