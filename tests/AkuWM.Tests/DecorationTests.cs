using AkuWM.Core.Config;
using AkuWM.Core.Desk;
using AkuWM.Core.Model;
using Xunit;

namespace AkuWM.Tests;

/// <summary>
/// What the shell is asked to draw around a window. None of it is dangerous to
/// get wrong the way a cloak is -- except the title bar, which is tested with
/// the rest of the per-rule effects -- but all of it is the kind of thing that
/// looks right in a review and is wrong on the screen.
/// </summary>
public class DecorationTests
{
    private static DeskFixture Fixture(string? focused = "#c4a7e7", string? other = "none") =>
        new(Configured(focused, other));

    private static AkuWmConfig Configured(string? focused, string? other)
    {
        AkuWmConfig config = DeskFixture.Configuration();
        config.Effects = new EffectsConfig { FocusedBorder = focused, OtherBorder = other };
        return config;
    }

    // ---- the colour ------------------------------------------------------

    [Theory]
    [InlineData("#ff0000", 0x000000FFu)]
    [InlineData("#00ff00", 0x0000FF00u)]
    [InlineData("#0000ff", 0x00FF0000u)]
    [InlineData("#c4a7e7", 0x00E7A7C4u)]
    public void A_colour_is_turned_round_for_the_shell(string rrggbb, uint colorref)
    {
        // COLORREF is 0x00BBGGRR -- blue first -- and every colour in the
        // configuration is written the other way round. Getting this wrong is
        // not an error, it is a border in the wrong colour, which is exactly
        // the kind of thing that survives a code review.
        Assert.Equal(colorref, Decoration.ColorRef(rrggbb));
    }

    [Theory]
    [InlineData("none")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a colour")]
    [InlineData("#12345")]
    public void Anything_that_is_not_a_colour_means_no_border(string? value)
    {
        Assert.Equal(Decoration.NoBorder, Decoration.ColorRef(value));
    }

    [Fact]
    public void No_border_is_not_the_same_as_the_shell_default()
    {
        // One means "draw nothing", the other means "draw what you would have
        // drawn". Collapsing them would make it impossible to put a window
        // back the way it was found.
        Assert.NotEqual(Decoration.NoBorder, Decoration.DefaultBorder);
        Assert.Equal(Decoration.DefaultBorder, Decoration.Untouched.Border);
    }

    // ---- who gets which --------------------------------------------------

    [Fact]
    public void The_focused_window_gets_the_focused_colour_and_the_others_do_not()
    {
        DeskFixture fixture = Fixture();
        fixture.Open(1);
        fixture.Open(2);
        fixture.Turn();
        fixture.Desk.Focus(DeskFixture.W(1));
        fixture.Turn();

        Assert.Equal(Decoration.ColorRef("#c4a7e7"), fixture.Platform.Decorations[DeskFixture.W(1)].Border);
        Assert.Equal(Decoration.NoBorder, fixture.Platform.Decorations[DeskFixture.W(2)].Border);
    }

    [Fact]
    public void The_border_follows_the_focus()
    {
        DeskFixture fixture = Fixture();
        fixture.Open(1);
        fixture.Open(2);
        fixture.Turn();
        fixture.Desk.Focus(DeskFixture.W(1));
        fixture.Turn();

        fixture.Desk.Focus(DeskFixture.W(2));
        fixture.Turn();

        Assert.Equal(Decoration.NoBorder, fixture.Platform.Decorations[DeskFixture.W(1)].Border);
        Assert.Equal(Decoration.ColorRef("#c4a7e7"), fixture.Platform.Decorations[DeskFixture.W(2)].Border);
    }

    // ---- and how often ---------------------------------------------------

    [Fact]
    public void Nothing_is_asked_for_twice()
    {
        DeskFixture fixture = Fixture();
        fixture.Open(1);
        fixture.Turn();

        // The decoration pass runs over every window on every redraw, and the
        // shell is asked only for what changed.
        Assert.Empty(fixture.Desk.Compute().Decorate);
    }

    [Fact]
    public void A_window_AkuWM_has_never_decorated_is_not_put_back()
    {
        DeskFixture fixture = Fixture();

        // The first pass must not go around reverting windows it has never
        // touched: that is a shell call per window per start, for nothing.
        Assert.Empty(fixture.Desk.Compute().Decorate);
    }

    [Fact]
    public void A_window_that_stops_being_managed_is_given_its_own_look_back()
    {
        DeskFixture fixture = Fixture();
        fixture.Open(1);
        fixture.Turn();
        fixture.Desk.Focus(DeskFixture.W(1));
        fixture.Turn();

        fixture.Desk.Window(DeskFixture.W(1))!.Managed = false;
        Redraw redraw = fixture.Desk.Compute();

        // A window left wearing AkuWM's border after AkuWM has let go of it is
        // a puzzle nobody can solve.
        Assert.Contains((DeskFixture.W(1), Decoration.Untouched), redraw.Decorate);
    }

    // ---- corners ---------------------------------------------------------

    [Theory]
    [InlineData("square", Corners.Square)]
    [InlineData("round", Corners.Round)]
    [InlineData("round_small", Corners.RoundSmall)]
    [InlineData("default", Corners.Default)]
    [InlineData(null, Corners.Default)]
    public void The_corner_shape_is_read_from_the_configuration(string? written, Corners expected)
    {
        AkuWmConfig config = Configured("#c4a7e7", "none");
        config.Effects!.Corners = written;

        var fixture = new DeskFixture(config);
        fixture.Open(1);
        fixture.Turn();

        Assert.Equal(expected, fixture.Platform.Decorations[DeskFixture.W(1)].Corners);
    }

    [Fact]
    public void A_shell_that_refuses_does_not_stop_the_redraw()
    {
        DeskFixture fixture = Fixture();
        fixture.Platform.RefusesDecoration = true;

        fixture.Open(1);
        Redraw redraw = fixture.Turn();

        // Windows 10 has neither attribute. That is not an error, and a
        // window manager that stopped arranging the desk over it would be.
        Assert.False(redraw.IsNothing);
        Assert.Contains(fixture.Platform.Calls, c => c.StartsWith("decorate", StringComparison.Ordinal));
    }
}
