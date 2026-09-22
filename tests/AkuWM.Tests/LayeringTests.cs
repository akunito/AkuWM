using AkuWM.Core.Model;
using Xunit;

namespace AkuWM.Tests;

/// <summary>
/// The alpha is only ever set on a window AkuWM itself made layered: one
/// SetLayeredWindowAttributes on a window that paints with
/// UpdateLayeredWindow (WPF AllowsTransparency -- NordVPN's "Add apps"
/// dialog) and it never repaints again. Measured 2026-09-22 16:38.
/// </summary>
public class LayeringTests
{
    [Fact]
    public void A_solid_window_layered_by_its_own_doing_is_never_touched() =>
        Assert.False(Layering.ShouldSetAlpha(windowIsLayered: true, layeredByUs: false, opacity: 1));

    [Fact]
    public void Translucency_is_refused_on_a_window_layered_by_its_own_doing() =>
        Assert.False(Layering.ShouldSetAlpha(windowIsLayered: true, layeredByUs: false, opacity: 0.8));

    [Fact]
    public void Translucency_on_an_ordinary_window_adds_the_bit_and_sets_the_alpha() =>
        Assert.True(Layering.ShouldSetAlpha(windowIsLayered: false, layeredByUs: false, opacity: 0.8));

    [Fact]
    public void A_window_we_layered_gets_its_alpha_changed_and_put_back()
    {
        Assert.True(Layering.ShouldSetAlpha(windowIsLayered: true, layeredByUs: true, opacity: 0.5));
        Assert.True(Layering.ShouldSetAlpha(windowIsLayered: true, layeredByUs: true, opacity: 1));
    }

    [Fact]
    public void A_solid_ordinary_window_is_not_made_layered_for_nothing() =>
        Assert.False(Layering.ShouldSetAlpha(windowIsLayered: false, layeredByUs: false, opacity: 1));
}
