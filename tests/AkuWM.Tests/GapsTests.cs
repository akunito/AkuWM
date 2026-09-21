using AkuWM.Core.Config;
using AkuWM.Core.Layout;
using AkuWM.Core.Model;
using Xunit;

namespace AkuWM.Tests;

/// <summary>
/// The two numbers a person actually reaches for: the space between windows,
/// and the space around them. Written for the GUI, where both are a slider.
/// </summary>
public class GapsTests
{
    private static AkuWmConfig With(Action<AkuWmConfig> edit)
    {
        AkuWmConfig config = DeskFixture.Configuration();
        config.Gaps ??= new GapsConfig();
        edit(config);
        return config;
    }

    [Fact]
    public void One_number_means_every_side()
    {
        // What a slider writes. It used to be a validation error: the engine
        // tolerated a short array while the validator refused it, which is the
        // worst possible split -- valid to run, invalid to save.
        AkuWmConfig config = ConfigJson.Read("""{"gaps":{"inner":8,"outer":12}}""");

        Assert.Equal([12, 12, 12, 12], config.Gaps!.Outer!);
        Assert.True(ConfigValidator.Validate(With(c => c.Gaps!.Outer = config.Gaps.Outer)).Ok);
    }

    [Fact]
    public void Four_numbers_still_mean_four_sides()
    {
        AkuWmConfig config = ConfigJson.Read("""{"gaps":{"outer":[1,2,3,4]}}""");

        Assert.Equal([1, 2, 3, 4], config.Gaps!.Outer!);
    }

    [Fact]
    public void An_even_gap_is_written_back_as_the_one_number_the_slider_set()
    {
        string json = ConfigJson.Write(new AkuWmConfig { Gaps = new GapsConfig { Outer = [10, 10, 10, 10] } });

        Assert.Contains("\"outer\": 10", json, StringComparison.Ordinal);
    }

    [Fact]
    public void An_uneven_one_is_written_back_as_four()
    {
        string json = ConfigJson.Write(new AkuWmConfig { Gaps = new GapsConfig { Outer = [10, 0, 10, 0] } });

        Assert.Contains("\"outer\": [", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"outer\": 10,", json, StringComparison.Ordinal);
    }

    [Fact]
    public void A_negative_outer_gap_is_refused()
    {
        // It pushes windows off the screen, which looks exactly like a window
        // manager that has lost them.
        ValidationResult result = ConfigValidator.Validate(With(c => c.Gaps!.Outer = [0, -40, 0, 0]));

        Assert.False(result.Ok);
        Assert.Contains(result.Errors, e => e.Path.StartsWith("gaps.outer", StringComparison.Ordinal));
    }

    [Fact]
    public void An_array_of_the_wrong_length_says_what_it_should_be()
    {
        ValidationResult result = ConfigValidator.Validate(With(c => c.Gaps!.Outer = [10, 10]));

        Assert.False(result.Ok);
        Assert.Contains(result.Errors, e => e.Message.Contains("one number, or four", StringComparison.Ordinal));
    }

    [Fact]
    public void The_outer_gap_keeps_windows_off_the_edge_of_the_screen()
    {
        var fixture = new DeskFixture(With(c =>
        {
            c.Gaps!.Inner = 0;
            c.Gaps.Outer = [30, 30, 30, 30];
            c.Gaps.ScaleWithDpi = false;
        }));

        fixture.Open(1);
        fixture.Turn();

        Rect where = fixture.Desk.Window(DeskFixture.W(1))!.Snapshot.FrameBounds;
        Rect area = fixture.Desk.MonitorByRole("main")!.TilingArea;

        Assert.Equal(area.Left + 30, where.Left);
        Assert.Equal(area.Right - 30, where.Right);
    }

    [Fact]
    public void One_screen_can_have_its_own_gaps()
    {
        var fixture = new DeskFixture(With(c =>
        {
            c.Gaps!.Inner = 4;
            c.Gaps.ScaleWithDpi = false;
            c.Monitors!.First(m => m.Id == "second").Gaps = new GapsConfig { Inner = 40 };
        }));

        // A field left out of the monitor's block is taken from the global
        // one, so "just the vertical monitor, wider" is two lines.
        Assert.Equal(4, fixture.Desk.GapsFor(fixture.Desk.MonitorByRole("main")!).Inner);
        Assert.Equal(40, fixture.Desk.GapsFor(fixture.Desk.MonitorByRole("second")!).Inner);
    }
}
