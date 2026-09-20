using AkuWM.Core.Model;
using AkuWM.Core.Platform;
using AkuWM.Core.State;
using Xunit;

namespace AkuWM.Tests;

/// <summary>A platform whose cloak calls can be made to succeed or refuse.</summary>
internal sealed class FakeActions : IPlatformActions
{
    private readonly FakePlatform _platform;

    public FakeActions(FakePlatform platform) => _platform = platform;

    /// <summary>When set, every call fails with this.</summary>
    public string? Refuse { get; set; }

    /// <summary>The call reports success and does nothing -- the shell really does this.</summary>
    public bool SilentlyDoNothing { get; set; }

    public List<(WindowHandle Window, bool Cloaked)> Calls { get; } = [];

    public string? SetCloak(WindowHandle window, bool cloaked)
    {
        Calls.Add((window, cloaked));

        if (Refuse is not null)
        {
            return Refuse;
        }

        if (!SilentlyDoNothing)
        {
            int at = _platform.WindowList.FindIndex(w => w.Handle == window);
            if (at >= 0)
            {
                _platform.WindowList[at] = _platform.WindowList[at] with
                {
                    Cloak = cloaked ? CloakKind.Shell : CloakKind.None,
                };
            }
        }

        return null;
    }

    // The rest of the interface is not what these tests are about; the fake
    // desk applies them so a ledger test that strays into geometry still says
    // something true.
    public int Place(IReadOnlyList<Placement> placements, bool activate = false) =>
        _platform.Place(placements, activate);

    public void SetMaximized(WindowHandle window, bool maximized) =>
        _platform.SetMaximized(window, maximized);

    public void SetMinimized(WindowHandle window, bool minimized) =>
        _platform.SetMinimized(window, minimized);

    public void SetTopmost(WindowHandle window, bool topmost) => _platform.SetTopmost(window, topmost);

    public bool Focus(WindowHandle window) => _platform.Focus(window);
}

/// <summary>
/// The promise a window manager makes when it hides a window by cloaking it:
/// that it will give it back. These are the cases where it nearly does not.
/// </summary>
public class CloakLedgerTests
{
    private static (CloakLedger Ledger, FakePlatform Platform, FakeActions Actions) Setup(
        TempDir dir, params WindowSnapshot[] windows)
    {
        var platform = new FakePlatform();
        platform.WindowList.AddRange(windows);
        return (new CloakLedger(dir.File("cloaked.bin")), platform, new FakeActions(platform));
    }

    [Fact]
    public void AWindowHiddenByARunThatDiedComesBackOnTheNextStart()
    {
        using var dir = new TempDir();
        WindowSnapshot window = FakePlatform.Window(1, "zen", title: "New chat - Claude");
        (CloakLedger ledger, FakePlatform platform, FakeActions actions) = Setup(dir, window);

        // The run that hid it: the ledger is written, and then the process is
        // gone before it could uncloak anything.
        ledger.Record(window);
        platform.WindowList[0] = window with { Cloak = CloakKind.Shell };

        // The next run reads the same file off disk.
        var next = new CloakLedger(dir.File("cloaked.bin"));
        RecoveryResult result = next.Recover(platform, actions);

        Assert.Single(result.Recovered);
        Assert.Empty(result.Failed);
        Assert.Equal(CloakKind.None, platform.WindowList[0].Cloak);
        Assert.Empty(next.Entries);
    }

    [Fact]
    public void ACloakThatComesOffIsNotRecoveredTwice()
    {
        using var dir = new TempDir();
        WindowSnapshot window = FakePlatform.Window(1, "zen");
        (CloakLedger ledger, FakePlatform platform, FakeActions actions) = Setup(dir, window);

        ledger.Record(window);
        ledger.Forget(window.Handle);

        Assert.Empty(ledger.Recover(platform, actions).Recovered);
        Assert.Empty(actions.Calls);
    }

    [Fact]
    public void AHandleWindowsHasGivenToSomebodyElseIsDroppedNotUncloaked()
    {
        using var dir = new TempDir();
        WindowSnapshot original = FakePlatform.Window(1, "zen");
        var platform = new FakePlatform();
        var ledger = new CloakLedger(dir.File("cloaked.bin"));
        ledger.Record(original);

        // Same handle, another process: Windows reuses them.
        platform.WindowList.Add(FakePlatform.Window(1, "explorer", cloak: CloakKind.Shell));
        var actions = new FakeActions(platform);

        RecoveryResult result = ledger.Recover(platform, actions);

        Assert.Empty(result.Recovered);
        Assert.Single(result.Stale);
        Assert.Empty(actions.Calls);
    }

    [Fact]
    public void AWindowThatIsGoneIsForgottenRatherThanRetriedForever()
    {
        using var dir = new TempDir();
        var platform = new FakePlatform();
        var ledger = new CloakLedger(dir.File("cloaked.bin"));
        ledger.Record(FakePlatform.Window(1, "zen"));

        Assert.Single(ledger.Recover(platform, new FakeActions(platform)).Stale);
        Assert.Empty(ledger.Entries);
    }

    [Fact]
    public void ACallThatReportsSuccessAndDoesNothingIsNotCalledASuccess()
    {
        // The shell has a spelling of SetCloak that does exactly this, and
        // trusting it is how a rescue reports thirteen successes and leaves
        // thirteen windows invisible.
        using var dir = new TempDir();
        WindowSnapshot window = FakePlatform.Window(1, "zen", cloak: CloakKind.Shell);
        (CloakLedger ledger, FakePlatform platform, FakeActions actions) = Setup(dir, window);
        actions.SilentlyDoNothing = true;
        ledger.Record(window);

        RecoveryResult result = ledger.Recover(platform, actions);

        Assert.Empty(result.Recovered);
        (CloakedWindow failed, string error) = Assert.Single(result.Failed);
        Assert.Equal("zen", failed.Process);
        Assert.Contains("still on", error, StringComparison.Ordinal);
    }

    [Fact]
    public void AWindowThatRefusesStaysInTheLedgerForTheNextAttempt()
    {
        using var dir = new TempDir();
        WindowSnapshot window = FakePlatform.Window(1, "zen", cloak: CloakKind.Shell);
        (CloakLedger ledger, FakePlatform platform, FakeActions actions) = Setup(dir, window);
        actions.Refuse = "access denied";
        ledger.Record(window);

        Assert.Single(ledger.Recover(platform, actions).Failed);
        Assert.Single(ledger.Entries);
    }

    [Fact]
    public void AnUnreadableLedgerDoesNotStopAkuWmStarting()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("cloaked.bin"), "{ this is not the file it was");

        var ledger = new CloakLedger(dir.File("cloaked.bin"));

        Assert.Empty(ledger.Entries);
    }

    [Fact]
    public void TheLedgerSurvivesBeingWrittenOverAndOver()
    {
        using var dir = new TempDir();
        var ledger = new CloakLedger(dir.File("cloaked.bin"));

        for (int i = 1; i <= 20; i++)
        {
            ledger.Record(FakePlatform.Window(i, "app" + i));
        }

        for (int i = 1; i <= 10; i++)
        {
            ledger.Forget(new WindowHandle(i));
        }

        Assert.Equal(10, new CloakLedger(dir.File("cloaked.bin")).Entries.Count);
        Assert.Equal(["cloaked.bin"], Directory.GetFiles(dir.Path).Select(Path.GetFileName));
    }
}
