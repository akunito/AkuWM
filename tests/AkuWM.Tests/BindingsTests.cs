using AkuWM.Core.Bindings;
using AkuWM.Core.Config;
using Xunit;

namespace AkuWM.Tests;

/// <summary>The shortcuts of the configuration, rendered for the AutoHotkey script (plan 10.27).</summary>
public class BindingsTests
{
    [Theory]
    [InlineData("Hyper+T", "^!#t")]
    [InlineData("Hyper+Shift+C", "^!#+c")]
    [InlineData("Hyper+Space", "^!#Space")]
    [InlineData("Ctrl+Alt+c", "^!c")]
    [InlineData("Win+Tab", "#Tab")]
    [InlineData("Hyper+WheelUp", "^!#WheelUp")]
    [InlineData("Hyper+Shift+;", "^!#+;")]
    [InlineData("Hyper+Shift++", "^!#++")]
    public void Keys_become_the_chord_the_script_binds(string keys, string chord) =>
        Assert.Equal(chord, Bindings.ChordOf(keys));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Hyper+")]
    [InlineData("Meta+T")]
    public void What_is_not_a_chord_is_null(string? keys) => Assert.Null(Bindings.ChordOf(keys));

    [Fact]
    public void Every_enabled_shortcut_is_one_line_with_its_command_expanded()
    {
        var shortcuts = new List<ShortcutConfig>
        {
            new() { Id = "a", Keys = "Hyper+T", Kind = "app", App = "WindowsTerminal.exe", Command = "wt.exe" },
            new() { Id = "b", Keys = "Hyper+Shift+C", Kind = "exec", Command = "\"%ProgramFiles%\\ShareX\\ShareX.exe\" -workflow \"Hyper+Shift+C\"" },
            new() { Id = "c", Keys = "Hyper+Space", Kind = "send", Command = "#!{Space}" },
            new() { Id = "d", Keys = "Ctrl+Alt+c", Kind = "wm", Command = "toggle-floating", When = new MatchCriteria { Process = "WindowsTerminal.exe" } },
            new() { Id = "off", Keys = "Hyper+X", Kind = "app", App = "calc.exe", Command = "calc", Enabled = false },
        };
        var problems = new List<string>();

        string text = Bindings.Render(shortcuts, problems, s => s.Replace("%ProgramFiles%", "C:\\Program Files"));

        Assert.Empty(problems);
        string[] lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.StartsWith("#", lines[0]);
        Assert.Equal("^!#t\tapp\tWindowsTerminal.exe\twt.exe\t", lines[1]);
        Assert.Equal("^!#+c\texec\t\t\"C:\\Program Files\\ShareX\\ShareX.exe\" -workflow \"Hyper+Shift+C\"\t", lines[2]);
        Assert.Equal("^!#Space\tsend\t\t#!{Space}\t", lines[3]);
        Assert.Equal("^!c\twm\t\ttoggle-floating\tWindowsTerminal.exe", lines[4]);
        Assert.Equal(5, lines.Length);
    }

    [Fact]
    public void What_cannot_be_bound_is_reported_and_left_out()
    {
        var shortcuts = new List<ShortcutConfig>
        {
            new() { Id = "nokeys", Keys = "", Kind = "wm", Command = "x" },
            new() { Id = "kind", Keys = "Hyper+A", Kind = "macro", Command = "x" },
            new() { Id = "noapp", Keys = "Hyper+B", Kind = "app", Command = "x" },
            new() { Id = "nocmd", Keys = "Hyper+C", Kind = "exec", Command = " " },
            new() { Id = "tab", Keys = "Hyper+D", Kind = "exec", Command = "a\tb" },
            new() { Id = "good", Keys = "Hyper+E", Kind = "exec", Command = "notepad" },
        };
        var problems = new List<string>();

        string text = Bindings.Render(shortcuts, problems, s => s);

        Assert.Equal(5, problems.Count);
        Assert.All(problems, p => Assert.Contains("shortcuts[", p));
        Assert.Contains("(kind)", problems[1]);
        Assert.Single(text.Split('\n', StringSplitOptions.RemoveEmptyEntries), l => !l.StartsWith('#'));
    }

    [Fact]
    public void Written_whole_and_read_back()
    {
        string dir = Path.Combine(Path.GetTempPath(), "akuwm-bindings-" + Guid.NewGuid().ToString("N"));
        string file = Path.Combine(dir, "bindings.tsv");
        try
        {
            Bindings.Write(file, "# one\n^!#t\tapp\tx\ty\t\n");
            Assert.Equal("# one\n^!#t\tapp\tx\ty\t\n", File.ReadAllText(file));
            Assert.False(File.Exists(file + ".tmp"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
