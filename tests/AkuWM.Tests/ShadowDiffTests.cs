using AkuWM.Core.Commands;
using AkuWM.Core.Config;
using AkuWM.Core.Import;
using AkuWM.Core.Model;
using Xunit;

namespace AkuWM.Tests;

/// <summary>
/// The comparison M1 is judged by, against a reply recorded from the GlazeWM
/// that was managing this desk at the time.
/// </summary>
public class ShadowDiffTests
{
    private const string Recorded = "glazewm-query-windows.json";

    private static AkuWmConfig Config()
    {
        AkuWmConfig imported = GlazeWmImporter.Import(Fixture.Read(Fixture.GlazeConfig), new ImportSummary());
        return ConfigMerge.Merge(ConfigDefaults.Create(), imported);
    }

    [Fact]
    public void ARecordedReplyIsReadBackWhole()
    {
        List<ExternalWindow> windows = ShadowDiff.ParseGlazeWindows(Fixture.Read(Recorded));

        Assert.Equal(6, windows.Count);
        Assert.Contains(windows, w => w.ProcessName == "Telegram" && w.Sticky && w.State == "minimized");
        Assert.Contains(windows, w => w.ProcessName == "zen" && w.State == "tiling");
        Assert.All(windows, w => Assert.NotEqual(0, w.Handle));
    }

    [Fact]
    public void AnEmptyOrBrokenReplyIsNotACrash()
    {
        Assert.Empty(ShadowDiff.ParseGlazeWindows("{}"));
        Assert.Empty(ShadowDiff.ParseGlazeWindows("""{"data":{}}"""));
    }

    [Fact]
    public void TheSameDeskSeenTwiceAgrees()
    {
        // AkuWM's snapshots rebuilt from the recorded reply: the same windows,
        // the same states. The comparison must find nothing to say.
        List<ExternalWindow> theirs = ShadowDiff.ParseGlazeWindows(Fixture.Read(Recorded));
        var mine = theirs.Select((w, i) => FakePlatform.Window(
            w.Handle,
            w.ProcessName,
            w.ClassName,
            w.Title,
            minimized: w.State == "minimized")).ToList();

        ShadowView view = ShadowModel.Build(Config(), [FakePlatform.MainMonitor()], mine);
        ShadowDiffResult result = ShadowDiff.Compare(view, theirs);

        Assert.True(result.Agrees, ShadowDiff.Format(result));
        Assert.Equal(6, result.Mine);
        Assert.Equal(6, result.Theirs);
    }

    [Fact]
    public void AWindowOnlyOneSideManagesIsReported()
    {
        ShadowView view = ShadowModel.Build(
            Config(), [FakePlatform.MainMonitor()], [FakePlatform.Window(0x11, "zen")]);

        ShadowDiffResult result = ShadowDiff.Compare(view, []);

        Assert.False(result.Agrees);
        Assert.Single(result.OnlyMine);
        Assert.Empty(result.OnlyTheirs);
    }

    [Fact]
    public void ADisagreementAboutStateIsReportedPerWindow()
    {
        ShadowView view = ShadowModel.Build(
            Config(), [FakePlatform.MainMonitor()], [FakePlatform.Window(0x11, "zen")]);

        ShadowDiffResult result = ShadowDiff.Compare(view,
            [new ExternalWindow(0x11, "zen", "Window", "a window", "floating", false, 0, "11")]);

        Disagreement disagreement = Assert.Single(result.Disagreements);
        Assert.Equal("state", disagreement.Field);
        Assert.Equal("tiling", disagreement.Mine);
        Assert.Equal("floating", disagreement.Theirs);
    }

    [Fact]
    public void ADisagreementAboutStickyIsReportedToo()
    {
        ShadowView view = ShadowModel.Build(
            Config(), [FakePlatform.MainMonitor()], [FakePlatform.Window(0x11, "Telegram")]);

        ShadowDiffResult result = ShadowDiff.Compare(view,
            [new ExternalWindow(0x11, "Telegram", "Window", "a window", "floating", false, 0, "11")]);

        Assert.Contains(result.Disagreements, d => d.Field == "sticky" && d.Mine == "True");
    }

    [Fact]
    public void WorkspaceNamesAreNotComparedBecauseTheyCannotBe()
    {
        // From outside, every hidden window looks the same: cloaked. Which
        // workspace it belongs to is knowable only to the manager in charge,
        // so that column waits for M2.
        ShadowView view = ShadowModel.Build(
            Config(), [FakePlatform.MainMonitor()], [FakePlatform.Window(0x11, "zen")]);

        ShadowDiffResult result = ShadowDiff.Compare(view,
            [new ExternalWindow(0x11, "zen", "Window", "a window", "tiling", false, 0, "a-workspace-id")]);

        Assert.True(result.Agrees);
    }
}

public class RectTests
{
    [Fact]
    public void EdgesAndSizeAreTheSameRectangleSaidTwoWays()
    {
        var rect = Rect.FromEdges(100, 200, 900, 800);

        Assert.Equal(new Rect(100, 200, 800, 600), rect);
        Assert.Equal(900, rect.Right);
        Assert.Equal(800, rect.Bottom);
    }

    [Fact]
    public void ContainingIsInclusiveOfTheEdges()
    {
        var monitor = new Rect(0, 0, 3840, 2160);

        Assert.True(monitor.Contains(monitor));
        Assert.True(new Rect(-1, -1, 3842, 2162).Contains(monitor));
        Assert.False(new Rect(0, 42, 3840, 2118).Contains(monitor));
    }

    [Fact]
    public void ShrinkingTakesADifferentAmountFromEachSide()
    {
        var shrunk = new Rect(0, 0, 100, 100).Shrink(top: 10, right: 20, bottom: 30, left: 40);

        Assert.Equal(new Rect(40, 10, 40, 60), shrunk);
    }

    [Fact]
    public void HowMuchOfAWindowIsOnScreenIsAFraction()
    {
        var screen = new Rect(0, 0, 1000, 1000);

        Assert.Equal(1.0, new Rect(0, 0, 500, 500).FractionInside(screen));
        Assert.Equal(0.25, new Rect(-500, -500, 1000, 1000).FractionInside(screen));
        Assert.Equal(0, new Rect(2000, 2000, 100, 100).FractionInside(screen));
    }

    [Fact]
    public void TwoRectanglesThatDoNotTouchIntersectToNothing() =>
        Assert.True(new Rect(0, 0, 10, 10).Intersect(new Rect(20, 20, 10, 10)).IsEmpty);
}
