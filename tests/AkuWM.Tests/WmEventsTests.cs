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
    [InlineData(PlatformEventKind.WindowCreated, EventResponse.ReadTheDeskIfItCouldBeOurs)]
    [InlineData(PlatformEventKind.WindowDestroyed, EventResponse.ReadTheDeskIfWeKnowIt)]
    [InlineData(PlatformEventKind.WindowShown, EventResponse.ReadTheDeskIfItCouldBeOurs)]
    [InlineData(PlatformEventKind.WindowHidden, EventResponse.ReadTheDeskIfWeKnowIt)]
    [InlineData(PlatformEventKind.ForegroundChanged, EventResponse.TheFocusMoved)]
    [InlineData(PlatformEventKind.WindowsReordered, EventResponse.TheStackChanged)]
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
    public void A_window_going_away_is_never_tested_for_being_a_candidate()
    {
        // A destroyed window fails every candidate check, so the two
        // directions cannot share a test: asking "could it be ours" about one
        // means never forgetting it.
        Assert.Equal(EventResponse.ReadTheDeskIfWeKnowIt, WmEvents.Decide(PlatformEventKind.WindowDestroyed));
        Assert.Equal(EventResponse.ReadTheDeskIfWeKnowIt, WmEvents.Decide(PlatformEventKind.WindowHidden));
        Assert.Equal(EventResponse.ReadTheDeskIfItCouldBeOurs, WmEvents.Decide(PlatformEventKind.WindowCreated));
        Assert.Equal(EventResponse.ReadTheDeskIfItCouldBeOurs, WmEvents.Decide(PlatformEventKind.WindowShown));
    }

    [Fact]
    public void Moving_a_window_never_costs_a_read_of_the_whole_desk()
    {
        // Dozens of these arrive for one drag. This is the single decision
        // that keeps a gesture in microseconds.
        Assert.Equal(EventResponse.ReadTheWindow, WmEvents.Decide(PlatformEventKind.WindowMoved));
    }
}
