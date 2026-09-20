using AkuWM.Core.Config;
using AkuWM.Core.Import;
using AkuWM.Core.Model;
using Xunit;

namespace AkuWM.Tests;

/// <summary>
/// What AkuWM decides about a window, checked against the configuration this
/// desk actually runs -- the one the importer produced from the GlazeWM setup.
/// </summary>
public class ShadowModelTests
{
    private static AkuWmConfig Config()
    {
        AkuWmConfig imported = GlazeWmImporter.Import(Fixture.Read(Fixture.GlazeConfig), new ImportSummary());
        return ConfigMerge.Merge(ConfigDefaults.Create(), imported);
    }

    private static ShadowView Build(params WindowSnapshot[] windows) =>
        ShadowModel.Build(Config(), [FakePlatform.MainMonitor(), FakePlatform.SecondMonitor()], windows);

    private static ManagedWindow Only(ShadowView view) => Assert.Single(view.Windows);

    [Fact]
    public void TheRulesFromTodaysDeskFloatAndStickTelegram()
    {
        ManagedWindow telegram = Only(Build(FakePlatform.Window(1, "Telegram", "Qt51519QWindowIcon")));

        Assert.True(telegram.Managed);
        Assert.Equal(WindowState.Floating, telegram.State);
        Assert.True(telegram.Sticky);
    }

    [Fact]
    public void ZebarIsLeftAloneByName()
    {
        ManagedWindow zebar = Only(Build(FakePlatform.Window(1, "zebar", "Tauri Window")));

        Assert.False(zebar.Managed);
        Assert.Equal(UnmanagedReason.Rule, zebar.Reason);
        Assert.Equal("zebar", zebar.ReasonDetail);
    }

    [Fact]
    public void TheCalculatorIsRecognisedByItsTitleAndNothingElseIs()
    {
        ManagedWindow calculator = Only(Build(
            FakePlatform.Window(1, "ApplicationFrameHost", "ApplicationFrameWindow", "Calculator")));
        ManagedWindow photos = Only(Build(
            FakePlatform.Window(2, "ApplicationFrameHost", "ApplicationFrameWindow", "Photos")));

        Assert.True(calculator.Sticky);
        Assert.False(photos.Sticky);
        // Both float, but the second one only because the host app rule says so.
        Assert.Equal(WindowState.Floating, photos.State);
    }

    [Fact]
    public void AnOrdinaryWindowTiles()
    {
        ManagedWindow browser = Only(Build(FakePlatform.Window(1, "zen", "MozillaWindowClass")));

        Assert.True(browser.Managed);
        Assert.Equal(WindowState.Tiling, browser.State);
        Assert.False(browser.Sticky);
    }

    [Fact]
    public void AWindowThatCannotBeResizedFloatsRatherThanBeingFought()
    {
        ManagedWindow dialog = Only(Build(
            FakePlatform.Window(1, "someapp", "SomeDialog", resizable: false)));

        Assert.Equal(WindowState.Floating, dialog.State);
    }

    [Fact]
    public void CoveringTheWholeMonitorIsFullscreenEvenThoughTheTaskbarIsThere()
    {
        // The work area stops at 2118; a game covers all 2160.
        ManagedWindow game = Only(Build(FakePlatform.Window(
            1, "aion", "UnrealWindow", frame: new Rect(0, 0, 3840, 2160))));

        Assert.Equal(WindowState.Fullscreen, game.State);
    }

    [Fact]
    public void CoveringOnlyTheWorkAreaIsNotFullscreen()
    {
        ManagedWindow window = Only(Build(FakePlatform.Window(
            1, "zen", frame: new Rect(0, 42, 3840, 2118))));

        Assert.Equal(WindowState.Tiling, window.State);
    }

    [Fact]
    public void MinimisedBeatsEverythingElse()
    {
        ManagedWindow window = Only(Build(FakePlatform.Window(
            1, "aion", frame: new Rect(0, 0, 3840, 2160), minimized: true)));

        Assert.Equal(WindowState.Minimized, window.State);
    }

    [Fact]
    public void AWindowSomebodyElseCloakedIsNotOurs()
    {
        // In shadow mode AkuWM has hidden nothing, so every shell cloak on the
        // desk belongs to somebody else -- another manager, or the shell.
        ManagedWindow window = Only(Build(FakePlatform.Window(1, "zen", cloak: CloakKind.Shell)));

        Assert.False(window.Managed);
        Assert.Equal(UnmanagedReason.CloakedElsewhere, window.Reason);
    }

    [Fact]
    public void AWindowThisManagerCloakedItselfStaysManaged()
    {
        WindowSnapshot hidden = FakePlatform.Window(1, "zen", cloak: CloakKind.Shell);

        ShadowView view = ShadowModel.Build(
            Config(),
            [FakePlatform.MainMonitor()],
            [hidden],
            cloakedByUs: new HashSet<WindowHandle> { hidden.Handle });

        Assert.True(Only(view).Managed);
    }

    [Fact]
    public void AWindowOnAnotherNativeVirtualDesktopIsTheShellsBusiness()
    {
        ManagedWindow window = Only(Build(FakePlatform.Window(
            1, "zen", cloak: CloakKind.Shell, onCurrentDesktop: false)));

        Assert.False(window.Managed);
        Assert.Equal(UnmanagedReason.OtherVirtualDesktop, window.Reason);
    }

    [Fact]
    public void AnAppThatWentToItsTrayIsNotOnTheDesk()
    {
        ManagedWindow window = Only(Build(FakePlatform.Window(1, "zen", cloak: CloakKind.App)));

        Assert.False(window.Managed);
        Assert.Equal(UnmanagedReason.SelfCloaked, window.Reason);
    }

    [Fact]
    public void EachWindowGetsTheRoleOfTheMonitorItIsOn()
    {
        ShadowView view = ShadowModel.Build(
            ConfigMerge.Merge(Config(), new AkuWmConfig
            {
                Monitors =
                [
                    new MonitorConfig { Id = "main", Match = new MonitorMatch { Edid = "SAM7233" } },
                    new MonitorConfig { Id = "second", Match = new MonitorMatch { Edid = "NSL2711" } },
                ],
            }),
            [FakePlatform.MainMonitor(), FakePlatform.SecondMonitor()],
            [
                FakePlatform.Window(1, "zen", monitor: new MonitorHandle(1)),
                FakePlatform.Window(2, "vivaldi", monitor: new MonitorHandle(2)),
            ]);

        Assert.Equal("main", view.Windows[0].MonitorRole);
        Assert.Equal("second", view.Windows[1].MonitorRole);
    }
}

public class MonitorRolesTests
{
    [Fact]
    public void IdentityWinsOverPosition()
    {
        // The two monitors come back in the other order after a sleep cycle,
        // which is exactly what an index-based binding gets wrong.
        List<MonitorConfig> configured =
        [
            new MonitorConfig { Id = "main", Match = new MonitorMatch { Edid = "SAM7233" } },
            new MonitorConfig { Id = "second", Match = new MonitorMatch { Edid = "NSL2711" } },
        ];

        Dictionary<MonitorHandle, string> roles = MonitorRoles.Resolve(
            configured, [FakePlatform.SecondMonitor(handle: 7), FakePlatform.MainMonitor(handle: 9)]);

        Assert.Equal("second", roles[new MonitorHandle(7)]);
        Assert.Equal("main", roles[new MonitorHandle(9)]);
    }

    [Fact]
    public void WithoutAnIdentityTheRolesFallBackToPosition()
    {
        List<MonitorConfig> configured =
        [
            new MonitorConfig { Id = "main" },
            new MonitorConfig { Id = "second" },
        ];

        Dictionary<MonitorHandle, string> roles = MonitorRoles.Resolve(
            configured, [FakePlatform.MainMonitor(handle: 1), FakePlatform.SecondMonitor(handle: 2)]);

        Assert.Equal("main", roles[new MonitorHandle(1)]);
        Assert.Equal("second", roles[new MonitorHandle(2)]);
    }

    [Fact]
    public void ARoleWhoseMonitorIsNotThereSimplyHasNoMonitor()
    {
        List<MonitorConfig> configured =
        [
            new MonitorConfig { Id = "main", Match = new MonitorMatch { Edid = "SAM7233" } },
            new MonitorConfig { Id = "tv", Match = new MonitorMatch { Edid = "PHL0000" } },
        ];

        Dictionary<MonitorHandle, string> roles =
            MonitorRoles.Resolve(configured, [FakePlatform.MainMonitor(handle: 1)]);

        Assert.Equal("main", Assert.Single(roles).Value);
    }

    [Fact]
    public void AnUnknownMonitorTakesAnUnusedRoleRatherThanNone()
    {
        List<MonitorConfig> configured =
        [
            new MonitorConfig { Id = "main", Match = new MonitorMatch { Edid = "SAM7233" } },
            new MonitorConfig { Id = "second" },
        ];

        Dictionary<MonitorHandle, string> roles = MonitorRoles.Resolve(
            configured, [FakePlatform.MainMonitor(handle: 1), FakePlatform.SecondMonitor(handle: 2)]);

        Assert.Equal("second", roles[new MonitorHandle(2)]);
    }
}
