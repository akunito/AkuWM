using AkuWM.Core.Layout;
using AkuWM.Core.Model;
using Xunit;

namespace AkuWM.Tests;

/// <summary>
/// Carrying a rectangle from one screen to another, in each of the three
/// modes. Diego's own example is the first test of each.
/// </summary>
public class AcrossModeTests
{
    private static readonly Rect Small = new(0, 0, 1000, 1000);
    private static readonly Rect Big = new(5000, 2000, 2000, 2000);

    [Theory]
    [InlineData(AcrossMode.Proportional, 1980, 1980)]
    [InlineData(AcrossMode.Absolute, 990, 990)]
    [InlineData(AcrossMode.Hybrid, 990, 990)]
    public void Diegos_example(AcrossMode mode, int width, int height)
    {
        // 990x990 of a 1000x1000 screen, onto a 2000x2000 one. Hybrid is
        // absolute here: 990x990 fits on the big screen with room to spare.
        Rect went = AcrossMonitors.Map(new Rect(5, 5, 990, 990), Small, Big, mode);

        Assert.Equal(width, went.Width);
        Assert.Equal(height, went.Height);
    }

    [Theory]
    [InlineData(AcrossMode.Proportional, 990, 990)]
    [InlineData(AcrossMode.Absolute, 1980, 1980)]
    [InlineData(AcrossMode.Hybrid, 990, 990)]
    public void And_the_other_way_round(AcrossMode mode, int width, int height)
    {
        // The window that filled the 2000x2000 screen, going back to the
        // 1000x1000 one. Hybrid scales this time and absolute does not: 1980
        // pixels on a screen 1000 wide is the case the third mode exists for,
        // and absolute means what it says even when the result hangs off the
        // edge (the placement trims it there, not this).
        Rect went = AcrossMonitors.Map(new Rect(5010, 2010, 1980, 1980), Big, Small, mode);

        Assert.Equal(width, went.Width);
        Assert.Equal(height, went.Height);
    }

    [Fact]
    public void Absolute_keeps_the_offset_into_the_work_area()
    {
        Rect went = AcrossMonitors.Map(new Rect(120, 340, 400, 300), Small, Big, AcrossMode.Absolute);

        Assert.Equal(new Rect(5120, 2340, 400, 300), went);
    }

    [Fact]
    public void Proportional_keeps_the_fraction_across_it()
    {
        // A tenth across and a fifth down stays a tenth across and a fifth
        // down, at twice the size.
        Rect went = AcrossMonitors.Map(new Rect(100, 200, 400, 300), Small, Big, AcrossMode.Proportional);

        Assert.Equal(new Rect(5200, 2400, 800, 600), went);
    }

    [Fact]
    public void Hybrid_only_intervenes_when_the_pixels_do_not_fit()
    {
        var tall = new Rect(0, 0, 400, 1600);

        // Fits the big screen: left alone, offset and all.
        Assert.Equal(
            new Rect(5000, 2000, 400, 1600),
            AcrossMonitors.Map(tall, Small, Big, AcrossMode.Hybrid));

        // Does not fit the small one: scaled, like proportional.
        Assert.Equal(
            AcrossMonitors.Map(new Rect(5000, 2000, 400, 1600), Big, Small, AcrossMode.Proportional),
            AcrossMonitors.Map(new Rect(5000, 2000, 400, 1600), Big, Small, AcrossMode.Hybrid));
    }

    [Theory]
    [InlineData(AcrossMode.Proportional, 1980, 1980)]
    [InlineData(AcrossMode.Absolute, 990, 990)]
    [InlineData(AcrossMode.Hybrid, 990, 990)]
    public void Resize_leaves_the_corner_the_person_dropped(AcrossMode mode, int width, int height)
    {
        Rect went = AcrossMonitors.Resize(new Rect(5321, 2123, 990, 990), Small, Big, mode);

        Assert.Equal(5321, went.X);
        Assert.Equal(2123, went.Y);
        Assert.Equal(width, went.Width);
        Assert.Equal(height, went.Height);
    }

    [Theory]
    [InlineData(AcrossMode.Absolute)]
    [InlineData(AcrossMode.Proportional)]
    [InlineData(AcrossMode.Hybrid)]
    public void A_screen_of_no_size_changes_nothing(AcrossMode mode)
    {
        var rect = new Rect(10, 20, 30, 40);

        Assert.Equal(rect, AcrossMonitors.Map(rect, new Rect(0, 0, 0, 0), Big, mode));
        Assert.Equal(rect, AcrossMonitors.Resize(rect, new Rect(0, 0, 0, 0), Big, mode));
    }

    [Theory]
    [InlineData("absolute", AcrossMode.Absolute)]
    [InlineData("Proportional", AcrossMode.Proportional)]
    [InlineData("hybrid", AcrossMode.Hybrid)]
    [InlineData("", AcrossMode.Hybrid)]
    [InlineData(null, AcrossMode.Hybrid)]
    [InlineData("nonsense", AcrossMode.Hybrid)]
    public void The_names_the_configuration_uses(string? name, AcrossMode mode) =>
        Assert.Equal(mode, AcrossMonitors.Parse(name));
}
