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
        (Rect frame, uint colour, bool topmost) = f.Platform.Outlines[1];
        Assert.Equal(f.FrameOf(1), frame);
        Assert.Equal(Purple, colour);
        Assert.False(topmost);
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
}
