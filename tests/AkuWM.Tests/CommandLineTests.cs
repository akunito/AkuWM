using AkuWM.Core.Ipc;
using Xunit;

namespace AkuWM.Tests;

public class CommandLineTests
{
    [Fact]
    public void SplitsOnWhitespace() =>
        Assert.Equal(["command", "focus", "--workspace", "12"], CommandLine.Split("command focus --workspace 12"));

    [Fact]
    public void KeepsQuotedArgumentsTogether() =>
        Assert.Equal(
            ["config", "import", "glazewm", "--from", "/a path/config.yaml"],
            CommandLine.Split("""config import glazewm --from "/a path/config.yaml" """));

    [Fact]
    public void AnEmptyQuotedArgumentSurvives() =>
        Assert.Equal(["shell-exec", ""], CommandLine.Split("""shell-exec "" """));

    [Fact]
    public void JoiningAndSplittingAreARoundTrip()
    {
        string[] arguments = ["config", "import", "glazewm", "--from", @"C:\Users\diego\a b\config.yaml"];

        Assert.Equal(arguments, CommandLine.Split(CommandLine.Join(arguments)));
    }

    [Fact]
    public void OptionsAreReadWithAndWithoutValues()
    {
        Dictionary<string, string?> options =
            CommandLine.Options(CommandLine.Split("config import glazewm --dry-run --from x.yaml"), 2);

        Assert.True(options.ContainsKey("dry-run"));
        Assert.Null(options["dry-run"]);
        Assert.Equal("x.yaml", options["from"]);
    }
}
