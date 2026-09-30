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

    [Fact]
    public void A_command_line_splits_into_program_and_arguments()
    {
        Assert.Equal(("C:\\Users\\d\\AppData\\Local\\Programs\\AkuWM\\akuwm-gui.exe", "--hidden"),
            CommandLine.Program("\"C:\\Users\\d\\AppData\\Local\\Programs\\AkuWM\\akuwm-gui.exe\" --hidden"));
        Assert.Equal(("C:\\x\\a.lnk", string.Empty), CommandLine.Program("C:\\x\\a.lnk"));
        Assert.Equal(("C:\\Program Files\\ShareX\\ShareX.exe", "-workflow \"Hyper+Shift+C\""),
            CommandLine.Program("\"C:\\Program Files\\ShareX\\ShareX.exe\" -workflow \"Hyper+Shift+C\""));
        Assert.Equal((string.Empty, string.Empty), CommandLine.Program("  "));

        // An unquoted path with spaces that exists is the whole file.
        string lnk = "C:\\Users\\d\\AppData\\Roaming\\Microsoft\\Windows\\Start Menu\\Programs\\Startup\\Zebar.lnk";
        Assert.Equal((lnk, string.Empty), CommandLine.Program(lnk, exists: path => path == lnk));
        Assert.Equal(("C:\\Users\\d\\AppData\\Roaming\\Microsoft\\Windows\\Start", "Menu\\Programs\\Startup\\Zebar.lnk"),
            CommandLine.Program(lnk, exists: _ => false));
    }
}
