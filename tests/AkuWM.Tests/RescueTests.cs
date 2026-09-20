using System.Text.Json.Nodes;
using AkuWM.Core.Commands;
using AkuWM.Core.Config;
using AkuWM.Core.Ipc;
using AkuWM.Core.Model;
using AkuWM.Core.State;
using Xunit;

namespace AkuWM.Tests;

/// <summary>
/// The button a person presses when AkuWM has gone wrong and the windows are
/// not where they should be -- or are not anywhere at all.
/// </summary>
public class RescueTests
{
    private static ConfigPaths Paths(TempDir dir) => new(dir.Path, "TEST", dir.Path);

    private static (RescueCommand Rescue, FakePlatform Platform) Setup(
        TempDir dir,
        List<int> killed,
        string? pidBelongsTo = null,
        params WindowSnapshot[] windows)
    {
        var platform = new FakePlatform();
        platform.WindowList.AddRange(windows);

        var rescue = new RescueCommand(
            Paths(dir),
            platform,
            platform,
            // A pipe name nothing is listening on: the rescue must work when
            // the daemon is unreachable, which is the case it exists for.
            client: () => new PipeClient("akuwm-test-" + Guid.NewGuid().ToString("n")[..8]),
            processName: _ => pidBelongsTo,
            kill: killed.Add);

        return (rescue, platform);
    }

    private static JsonObject Run(RescueCommand rescue, params string[] arguments)
    {
        string line = string.Join(' ', new[] { "rescue" }.Concat(arguments));
        CommandResponse response = rescue.Execute(line, CommandLine.Split(line));
        Assert.True(response.Success, response.Error);
        return response.Data!.AsObject();
    }

    [Fact]
    public void Windows_the_last_run_hid_come_back()
    {
        using var dir = new TempDir();
        WindowSnapshot zen = FakePlatform.Window(1, "zen", title: "a tab", cloak: CloakKind.Shell);
        var (rescue, platform) = Setup(dir, [], windows: zen);

        // What the daemon wrote before it cloaked the window, and then died.
        new CloakLedger(Paths(dir).CloakLedgerFile).Record(zen);

        JsonObject data = Run(rescue);

        Assert.Equal(1, data["uncloaked"]!.GetValue<int>());
        Assert.False(platform.Window(zen.Handle)!.Cloak.HasFlag(CloakKind.Shell));
    }

    [Fact]
    public void Windows_the_last_run_moved_go_back_where_they_were()
    {
        using var dir = new TempDir();
        WindowSnapshot zen = FakePlatform.Window(1, "zen", frame: new Rect(200, 300, 900, 700));
        var (rescue, platform) = Setup(dir, [], windows: zen);

        new GeometryJournal(Paths(dir).GeometryJournalFile).Remember(zen);
        platform.Place([new Placement(zen.Handle, new Rect(0, 0, 1920, 2118))]);

        JsonObject data = Run(rescue);

        Assert.Equal(1, data["restored"]!.GetValue<int>());
        Assert.Equal(new Rect(200, 300, 900, 700), platform.Window(zen.Handle)!.FrameBounds);
    }

    [Fact]
    public void By_default_only_what_AkuWM_hid_is_touched()
    {
        using var dir = new TempDir();
        WindowSnapshot ours = FakePlatform.Window(1, "zen", cloak: CloakKind.Shell);
        WindowSnapshot theirs = FakePlatform.Window(2, "code", cloak: CloakKind.Shell);
        var (rescue, platform) = Setup(dir, [], null, ours, theirs);

        new CloakLedger(Paths(dir).CloakLedgerFile).Record(ours);

        Run(rescue);

        // The other window manager's hidden workspace stays hidden: a rescue
        // that reveals every window on the machine is not a rescue.
        Assert.True(platform.Window(theirs.Handle)!.Cloak.HasFlag(CloakKind.Shell));
    }

    [Fact]
    public void With_all_every_hidden_window_is_given_back()
    {
        using var dir = new TempDir();
        WindowSnapshot ours = FakePlatform.Window(1, "zen", cloak: CloakKind.Shell);
        WindowSnapshot orphan = FakePlatform.Window(2, "code", cloak: CloakKind.Shell);
        var (rescue, platform) = Setup(dir, [], null, ours, orphan);

        new CloakLedger(Paths(dir).CloakLedgerFile).Record(ours);

        JsonObject data = Run(rescue, "--all");

        Assert.Equal(2, data["uncloaked"]!.GetValue<int>());
        Assert.False(platform.Window(orphan.Handle)!.Cloak.HasFlag(CloakKind.Shell));
    }

    [Fact]
    public void A_window_on_another_virtual_desktop_is_left_where_it_is()
    {
        using var dir = new TempDir();
        WindowSnapshot elsewhere = FakePlatform.Window(
            2, "code", cloak: CloakKind.Shell, onCurrentDesktop: false);
        var (rescue, platform) = Setup(dir, [], windows: elsewhere);

        Run(rescue, "--all");

        // Uncloaking it would drag it onto this desktop, which is a different
        // mess from the one being cleaned up.
        Assert.True(platform.Window(elsewhere.Handle)!.Cloak.HasFlag(CloakKind.Shell));
    }

    [Fact]
    public void A_daemon_that_does_not_answer_is_stopped()
    {
        using var dir = new TempDir();
        var killed = new List<int>();
        var (rescue, _) = Setup(dir, killed, pidBelongsTo: "akuwm");

        // A run that began and never ended: the process may still be there,
        // frozen, holding windows invisible.
        new SessionMarker(Paths(dir).SessionFile).Begin();

        JsonObject data = Run(rescue);

        Assert.Single(killed);
        Assert.Contains("stopped", data["daemon"]!.GetValue<string>());
    }

    [Fact]
    public void A_pid_that_now_belongs_to_something_else_is_not_killed()
    {
        using var dir = new TempDir();
        var killed = new List<int>();
        var (rescue, _) = Setup(dir, killed, pidBelongsTo: "chrome");

        new SessionMarker(Paths(dir).SessionFile).Begin();

        JsonObject data = Run(rescue);

        // Windows reuses pids. A rescue that kills a browser is not one.
        Assert.Empty(killed);
        Assert.Contains("chrome", data["daemon"]!.GetValue<string>());
    }

    [Fact]
    public void A_run_that_ended_cleanly_leaves_nothing_to_stop()
    {
        using var dir = new TempDir();
        var killed = new List<int>();
        var (rescue, _) = Setup(dir, killed, pidBelongsTo: "akuwm");

        var marker = new SessionMarker(Paths(dir).SessionFile);
        marker.Begin();
        marker.End();

        Run(rescue);

        Assert.Empty(killed);
    }

    [Fact]
    public void Forgive_lets_the_next_start_manage_the_desk_again()
    {
        using var dir = new TempDir();
        string session = Paths(dir).SessionFile;
        var (rescue, _) = Setup(dir, []);

        new SessionMarker(session).Begin();
        new SessionMarker(session).Begin();
        new SessionMarker(session).Begin();
        Assert.True(new SessionMarker(session).Previous!.UncleanInARow >= SessionMarker.SafeModeAfter);

        Run(rescue, "--forgive");

        Assert.False(new SessionMarker(session).Begin().SafeMode);
    }

    [Fact]
    public void Rescue_is_never_handed_to_the_process_it_is_rescuing_from()
    {
        Assert.True(CommandRouter.NeverDelegates("rescue"));
        Assert.True(CommandRouter.NeedsNoDaemon("rescue"));
        Assert.False(CommandRouter.NeverDelegates("query"));
    }
}
