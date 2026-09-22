using AkuWM.Core.Config;
using AkuWM.Core.Desk;
using AkuWM.Core.Model;
using Xunit;

namespace AkuWM.Tests;

/// <summary>
/// A border of AkuWM's own around a window the shell would not decorate: an
/// elevated window refuses every DWM attribute from a build without
/// uiAccess (Purple, the Razer installer, 0x80070006). The outline is a
/// separate window that follows the target; nothing on the target changes.
/// </summary>
public class OutlineTests
{
    private static WindowHandle W(long handle) => new(handle);

    private const uint Purple = 0x00E7A7C4; // #c4a7e7 as a COLORREF

    private static DeskFixture Desk(string other = "none")
    {
        AkuWmConfig config = DeskFixture.Configuration();
        config.Effects = new EffectsConfig { FocusedBorder = "#c4a7e7", OtherBorder = other, Corners = "square" };
        var f = new DeskFixture(config);
        f.Platform.RefusesDecoration = true;
        return f;
    }

    [Fact]
    public void A_window_whose_decoration_is_refused_gets_an_outline_where_its_frame_is()
    {
        DeskFixture f = Desk();
        f.Open(1, elevated: true);
        f.Turn();
        f.Foreground(1);
        f.Turn();
        f.Turn();

        Assert.NotNull(f.Managed(1)!.DecorationRefused);
        (Rect frame, uint colour, bool topmost, int corner, int width) = f.Platform.Outlines[1];
        Assert.Equal(f.FrameOf(1), frame);
        Assert.Equal(Purple, colour);
        Assert.False(topmost);
        Assert.Equal(0, corner); // corners: square in this fixture
        Assert.Equal(2, width);
    }

    [Fact]
    public void The_border_width_and_the_elevated_colour_come_from_the_configuration()
    {
        AkuWmConfig config = DeskFixture.Configuration();
        config.Effects = new EffectsConfig { FocusedBorder = "#c4a7e7", OtherBorder = "#444444", ElevatedBorder = "#ff8800", BorderWidth = 4 };
        var f = new DeskFixture(config);
        f.Platform.RefusesDecoration = true;
        f.Open(1, elevated: true);
        f.Open(2);
        f.Turn();
        f.Foreground(1);
        f.Turn();
        f.Turn();

        (_, uint colour, _, _, int width) = f.Platform.Outlines[1];
        Assert.Equal(0x000088FFu, colour); // #ff8800 as a COLORREF
        Assert.Equal(4, width);

        // Unfocused, the elevated colour does not apply.
        f.Foreground(2);
        f.Turn();
        f.Turn();
        Assert.Equal(0x00444444u, f.Platform.Outlines[1].Colour);
    }

    [Theory]
    [InlineData("round", 8)]
    [InlineData("round_small", 4)]
    [InlineData("square", 0)]
    [InlineData("default", 8)]
    public void The_outline_rounds_its_corners_as_the_windows_are(string corners, int radius)
    {
        AkuWmConfig config = DeskFixture.Configuration();
        config.Effects = new EffectsConfig { FocusedBorder = "#c4a7e7", OtherBorder = "#444444", Corners = corners };
        var f = new DeskFixture(config);
        f.Platform.RefusesDecoration = true;
        f.Open(1, elevated: true);
        f.Turn();
        f.Turn();

        Assert.Equal(radius, f.Platform.Outlines[1].Corner);
    }

    [Fact]
    public void A_window_the_shell_decorates_gets_no_outline()
    {
        AkuWmConfig config = DeskFixture.Configuration();
        config.Effects = new EffectsConfig { FocusedBorder = "#c4a7e7", OtherBorder = "none" };
        var f = new DeskFixture(config);
        f.Open(1);
        f.Turn();
        f.Foreground(1);
        f.Turn();

        Assert.Empty(f.Platform.Outlines);
    }

    [Fact]
    public void The_outline_follows_the_window_and_is_sent_only_when_something_changed()
    {
        DeskFixture f = Desk();
        f.Open(1, elevated: true);
        Assert.True(f.Desk.SetFloating(W(1), true));
        f.Turn();
        f.Foreground(1);
        f.Turn();
        f.Turn();
        Assert.Empty(f.Turn().Outline);

        f.Move(1, new Rect(500, 400, 900, 700));
        f.Wait(AkuWM.Core.Desk.Desk.SettleMs + 1);
        f.Turn();
        f.Turn();

        Assert.Equal(new Rect(500, 400, 900, 700), f.Platform.Outlines[1].Frame);
        Assert.Empty(f.Turn().Outline);
    }

    [Fact]
    public void Losing_the_focus_with_no_other_border_takes_the_outline_away_and_a_colour_keeps_it()
    {
        DeskFixture f = Desk(other: "none");
        f.Open(1, elevated: true);
        f.Open(2);
        f.Turn();
        f.Foreground(1);
        f.Turn();
        Assert.True(f.Platform.Outlines.ContainsKey(1));

        f.Foreground(2);
        f.Turn();
        Assert.False(f.Platform.Outlines.ContainsKey(1));

        DeskFixture g = Desk(other: "#444444");
        g.Open(1, elevated: true);
        g.Open(2);
        g.Turn();
        g.Foreground(2);
        g.Turn();
        Assert.Equal(0x00444444u, g.Platform.Outlines[1].Colour);
    }

    [Fact]
    public void A_hidden_workspace_takes_the_outline_with_it_and_showing_it_brings_it_back()
    {
        DeskFixture f = Desk(other: "#444444");
        f.Open(1, elevated: true);
        f.Turn();
        f.Turn(); // the refusal is known after the first pass; the outline follows
        Assert.True(f.Platform.Outlines.ContainsKey(1));

        f.Desk.FocusWorkspace("12");
        f.Turn();
        Assert.True(f.IsHidden(1));
        Assert.False(f.Platform.Outlines.ContainsKey(1));

        f.Desk.FocusWorkspace("11");
        f.Turn();
        f.Turn();
        Assert.True(f.Platform.Outlines.ContainsKey(1));
    }

    [Fact]
    public void A_fullscreen_or_minimised_window_has_no_outline()
    {
        DeskFixture f = Desk(other: "#444444");
        f.Open(1, elevated: true, frame: new Rect(0, 0, 3840, 2160), resizable: false);
        f.Turn();
        Assert.Equal(WindowState.Fullscreen, f.Managed(1)!.State);
        Assert.False(f.Platform.Outlines.ContainsKey(1));

        f.Open(2, elevated: true);
        f.Turn();
        f.Turn();
        Assert.True(f.Platform.Outlines.ContainsKey(2));
        f.Platform.SetMinimized(W(2), true);
        f.Sync();
        f.Turn();
        Assert.False(f.Platform.Outlines.ContainsKey(2));
    }

    [Fact]
    public void A_window_that_closes_takes_its_outline_with_it()
    {
        DeskFixture f = Desk(other: "#444444");
        f.Open(1, elevated: true);
        f.Turn();
        f.Turn();
        Assert.True(f.Platform.Outlines.ContainsKey(1));

        f.Close(1);
        f.Turn();
        Assert.False(f.Platform.Outlines.ContainsKey(1));
    }

    [Fact]
    public void The_outline_follows_the_target_into_the_always_on_top_band()
    {
        DeskFixture f = Desk(other: "#444444");
        f.Open(1, elevated: true);
        Assert.True(f.Desk.SetFloating(W(1), true));
        f.Turn();
        f.Turn();
        Assert.True(f.Platform.Outlines[1].Topmost);
    }
}

/// <summary>The re-assert of a decoration must not blink the outline.</summary>
public class OutlineSurvivesRedecorationTests
{
    private static WindowHandle W(long handle) => new(handle);

    [Fact]
    public void A_redecorate_of_a_refused_window_keeps_its_outline()
    {
        AkuWmConfig config = DeskFixture.Configuration();
        config.Effects = new EffectsConfig { FocusedBorder = "#c4a7e7", OtherBorder = "#444444" };
        var f = new DeskFixture(config);
        f.Platform.RefusesDecoration = true;
        f.Open(1, elevated: true);
        f.Turn();
        f.Turn();
        Assert.True(f.Platform.Outlines.ContainsKey(1));

        f.Desk.Redecorate(W(1));
        Redraw redraw = f.Turn();

        Assert.Contains(redraw.Decorate, d => d.Item1 == W(1));
        Assert.DoesNotContain(redraw.Outline, o => o.Window == W(1) && o.Frame is null);
        Assert.True(f.Platform.Outlines.ContainsKey(1));
        Assert.NotNull(f.Managed(1)!.DecorationRefused);
    }

    [Fact]
    public void A_refused_redecoration_is_asked_once_not_on_every_redraw()
    {
        AkuWmConfig config = DeskFixture.Configuration();
        config.Effects = new EffectsConfig { FocusedBorder = "#c4a7e7", OtherBorder = "#444444" };
        var f = new DeskFixture(config);
        f.Platform.RefusesDecoration = true;
        f.Open(1, elevated: true);
        f.Turn();
        f.Turn();

        f.Desk.Redecorate(W(1));
        Assert.Contains(f.Turn().Decorate, d => d.Item1 == W(1));

        Assert.DoesNotContain(f.Turn().Decorate, d => d.Item1 == W(1));
        Assert.DoesNotContain(f.Turn().Decorate, d => d.Item1 == W(1));
        Assert.NotNull(f.Managed(1)!.DecorationRefused);
    }
}

public class EffectsConfigValidationTests
{
    private static List<AkuWM.Core.Config.ValidationIssue> Validate(EffectsConfig effects)
    {
        AkuWmConfig config = DeskFixture.Configuration();
        config.Effects = effects;
        return AkuWM.Core.Config.ConfigValidator.Validate(config).Issues.ToList();
    }

    [Fact]
    public void Border_width_glow_and_elevated_colour_are_checked()
    {
        Assert.Contains(Validate(new EffectsConfig { BorderWidth = 0 }), i => i.Path == "effects.border_width");
        Assert.Contains(Validate(new EffectsConfig { Glow = 40 }), i => i.Path == "effects.glow");
        Assert.Contains(Validate(new EffectsConfig { ElevatedBorder = "orange" }), i => i.Path == "effects.elevated_border");
        Assert.DoesNotContain(Validate(new EffectsConfig { BorderWidth = 3, ElevatedBorder = "#ff8800" }), i => i.Path.StartsWith("effects.", StringComparison.Ordinal) && i.Severity == AkuWM.Core.Config.Severity.Error);
    }

    [Fact]
    public void Shadow_and_glow_are_taken_but_say_they_are_not_drawn_yet()
    {
        var issues = Validate(new EffectsConfig { Shadow = true, Glow = 6 });
        Assert.Contains(issues, i => i.Path == "effects.shadow" && i.Message.Contains("does not act", StringComparison.Ordinal));
        Assert.Contains(issues, i => i.Path == "effects.glow" && i.Message.Contains("does not act", StringComparison.Ordinal));
    }
}
