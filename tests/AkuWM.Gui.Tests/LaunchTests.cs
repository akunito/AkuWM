using AkuWM.Gui;
using Xunit;

namespace AkuWM.Gui.Tests;

public class LaunchTests
{
    [Fact]
    public void Parses_every_flag()
    {
        var args = LaunchArgs.Parse(["--section", "monitors", "--select", "r-1", "--toggle", "--hidden", "--smoke", "C:\\a b\\out.txt"]);
        Assert.Equal(new LaunchArgs("monitors", "r-1", true, true, "C:\\a b\\out.txt"), args);
    }

    [Fact]
    public void Round_trips_over_the_pipe_line()
    {
        var args = new LaunchArgs("rules", "k 1", true, false, null);
        Assert.Equal(args, LaunchArgs.FromLine(args.ToLine()));
        Assert.Equal(new LaunchArgs(null, null, false, false, null), LaunchArgs.FromLine(string.Empty));
    }

    [Fact]
    public void Knows_its_sections()
    {
        Assert.True(LaunchArgs.ValidSection(null));
        Assert.True(LaunchArgs.ValidSection("doctor"));
        Assert.False(LaunchArgs.ValidSection("bogus"));
        Assert.Equal(9, LaunchArgs.Sections.Length);
    }

    [Theory]
    [InlineData(true, null, Visibility.Active, LaunchAction.Hide)]
    [InlineData(true, null, Visibility.Shown, LaunchAction.Activate)]
    [InlineData(true, null, Visibility.Hidden, LaunchAction.Show)]
    [InlineData(true, "rules", Visibility.Active, LaunchAction.Activate)]
    [InlineData(false, null, Visibility.Hidden, LaunchAction.Show)]
    [InlineData(false, "log", Visibility.Hidden, LaunchAction.Show)]
    public void Toggle_is_swayapps_table(bool toggle, string? section, Visibility now, LaunchAction want)
    {
        Assert.Equal(want, Launch.Decide(new LaunchArgs(section, null, toggle, false, null), now));
    }

    [Fact]
    public void Hidden_start_touches_nothing()
    {
        Assert.Equal(LaunchAction.Nothing, Launch.Decide(new LaunchArgs(null, null, false, true, null), Visibility.Hidden));
    }
}
