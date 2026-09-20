using AkuWM.Core.Platform;
using AkuWM.Core.Wm;
using Xunit;

namespace AkuWM.Tests;

/// <summary>
/// Every kind of thing Windows can say, and what it costs to answer.
/// </summary>
public class WmEventsTests
{
    [Theory]
    [InlineData(PlatformEventKind.WindowCreated, EventResponse.ReadTheDesk)]
    [InlineData(PlatformEventKind.WindowDestroyed, EventResponse.ReadTheDesk)]
    [InlineData(PlatformEventKind.WindowShown, EventResponse.ReadTheDesk)]
    [InlineData(PlatformEventKind.WindowHidden, EventResponse.ReadTheDesk)]
    [InlineData(PlatformEventKind.ForegroundChanged, EventResponse.TheFocusMoved)]
    [InlineData(PlatformEventKind.DisplayChanged, EventResponse.TheScreensChanged)]
    [InlineData(PlatformEventKind.SettingsChanged, EventResponse.TheScreensChanged)]
    [InlineData(PlatformEventKind.PowerResume, EventResponse.TheScreensChanged)]
    [InlineData(PlatformEventKind.PowerSuspend, EventResponse.Nothing)]
    [InlineData(PlatformEventKind.WindowMoved, EventResponse.ReadTheWindow)]
    [InlineData(PlatformEventKind.WindowMinimizeStart, EventResponse.ReadTheWindow)]
    [InlineData(PlatformEventKind.WindowMinimizeEnd, EventResponse.ReadTheWindow)]
    [InlineData(PlatformEventKind.WindowCloaked, EventResponse.ReadTheWindow)]
    [InlineData(PlatformEventKind.WindowUncloaked, EventResponse.ReadTheWindow)]
    [InlineData(PlatformEventKind.WindowTitleChanged, EventResponse.ReadTheWindow)]
    public void Each_kind_costs_what_it_should(PlatformEventKind kind, EventResponse expected) =>
        Assert.Equal(expected, WmEvents.Decide(kind));

    [Fact]
    public void Every_kind_has_an_answer()
    {
        // The compiler does not check a switch over an enum for completeness,
        // and a kind that falls through would quietly read the whole desk on
        // every keystroke-sized event.
        foreach (PlatformEventKind kind in Enum.GetValues<PlatformEventKind>())
        {
            Assert.True(
                Enum.IsDefined(WmEvents.Decide(kind)),
                $"{kind} has no answer");
        }
    }

    [Fact]
    public void Moving_a_window_never_costs_a_read_of_the_whole_desk()
    {
        // Dozens of these arrive for one drag. This is the single decision
        // that keeps a gesture in microseconds.
        Assert.Equal(EventResponse.ReadTheWindow, WmEvents.Decide(PlatformEventKind.WindowMoved));
    }
}
