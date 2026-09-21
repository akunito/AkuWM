using AkuWM.Core.Compat;
using AkuWM.Core.Config;
using AkuWM.Core.Desk;
using AkuWM.Core.Layout;
using AkuWM.Core.Model;
using Xunit;

namespace AkuWM.Tests;

/// <summary>
/// Taking a new configuration without losing the desk. This is what the GUI's
/// Apply button becomes, so the thing that matters is not that the new values
/// arrive -- it is that everything the person had arranged is still there
/// afterwards.
/// </summary>
public class ReloadTests
{
    private readonly DeskFixture _fixture = new();

    private Desk Desk => _fixture.Desk;

    private static AkuWmConfig Changed(Action<AkuWmConfig> edit)
    {
        AkuWmConfig config = DeskFixture.Configuration();
        edit(config);
        return config;
    }

    [Fact]
    public void The_layout_survives()
    {
        _fixture.Open(1);
        _fixture.Open(2);
        _fixture.Turn();

        Desk.Resize(DeskFixture.W(1), Direction.Right, 10);
        _fixture.Turn();
        Rect arranged = Desk.Window(DeskFixture.W(1))!.Snapshot.FrameBounds;

        Desk.Reload(Changed(c => c.Gaps!.Inner = 20));
        _fixture.Turn();

        // The gap changed, so the rectangle must too -- but the SHARE the
        // person set must not. Rebuilding the workspaces would have thrown the
        // tree away and given both windows half each again.
        Rect after = Desk.Window(DeskFixture.W(1))!.Snapshot.FrameBounds;
        Assert.NotEqual(arranged, after);
        Assert.True(after.Width > Desk.Window(DeskFixture.W(2))!.Snapshot.FrameBounds.Width);
    }

    [Fact]
    public void A_floating_window_keeps_where_the_person_put_it()
    {
        _fixture.Open(1);
        _fixture.Turn();
        Desk.SetFloating(DeskFixture.W(1), true);
        _fixture.Turn();
        _fixture.Move(1, new Rect(640, 480, 800, 600));
        _fixture.Turn();

        Desk.Reload(Changed(c => c.Effects = new EffectsConfig { FocusedBorder = "#ff0000" }));
        _fixture.Turn();

        Assert.Equal(new Rect(640, 480, 800, 600), Desk.Window(DeskFixture.W(1))!.Snapshot.FrameBounds);
    }

    [Fact]
    public void The_workspace_you_are_on_is_still_the_one_you_are_on()
    {
        _fixture.Open(1);
        _fixture.Turn();
        Desk.FocusWorkspace("13");
        _fixture.Turn();

        Desk.Reload(Changed(c => c.Gaps!.Inner = 4));

        Assert.Equal("13", Desk.MonitorByRole("main")!.Displayed!.Name);
    }

    [Fact]
    public void A_new_workspace_appears_and_belongs_to_its_monitor()
    {
        Desk.ReloadResult result = Desk.Reload(Changed(c =>
            c.Workspaces!.Add(new WorkspaceConfig { Name = "19", Monitor = "main" })));

        Assert.Equal(1, result.Added);
        Assert.NotNull(Desk.Workspace("19"));
        Assert.Contains(Desk.MonitorByRole("main")!.Workspaces, w => w.Name == "19");
    }

    [Fact]
    public void A_window_on_a_workspace_that_is_gone_is_rehomed_not_stranded()
    {
        _fixture.Open(1);
        _fixture.Turn();
        Desk.FocusWorkspace("12");
        _fixture.Open(2);
        _fixture.Turn();
        Assert.Equal("12", Desk.Window(DeskFixture.W(2))!.Workspace);

        Desk.ReloadResult result = Desk.Reload(Changed(c =>
            c.Workspaces!.RemoveAll(w => w.Name == "12")));

        // A window pointing at a workspace nobody has is a window no redraw
        // will ever account for.
        Assert.Equal(1, result.Removed);
        Assert.Equal(1, result.Rehomed);
        Assert.Null(Desk.Workspace("12"));

        string? home = Desk.Window(DeskFixture.W(2))!.Workspace;
        Assert.NotNull(home);
        Assert.NotNull(Desk.Workspace(home));
        Assert.Equal("main", Desk.Workspace(home)!.MonitorRole);
    }

    [Fact]
    public void A_workspace_moved_to_another_screen_arrives_there()
    {
        _fixture.Open(1);
        _fixture.Turn();

        Desk.Reload(Changed(c =>
            c.Workspaces!.First(w => w.Name == "13").Monitor = "second"));

        // Found on the desk: renaming this desk's workspaces moved one from
        // the second monitor to the first, and it stayed where it was -- the
        // screen it had left kept it, and the one that should have had it was
        // a workspace short.
        Assert.Equal("second", Desk.Workspace("13")!.MonitorRole);
        Assert.Contains(Desk.MonitorByRole("second")!.Workspaces, w => w.Name == "13");
        Assert.DoesNotContain(Desk.MonitorByRole("main")!.Workspaces, w => w.Name == "13");
    }

    [Fact]
    public void A_workspace_that_changes_screen_keeps_its_windows()
    {
        _fixture.Open(1);
        _fixture.Turn();
        Desk.FocusWorkspace("12");
        _fixture.Open(2);
        _fixture.Turn();

        Desk.Reload(Changed(c =>
            c.Workspaces!.First(w => w.Name == "12").Monitor = "second"));

        // Moved, not torn down and rebuilt: the person edited a file, they did
        // not ask to lose the layout on that workspace.
        Assert.Equal("12", Desk.Window(DeskFixture.W(2))!.Workspace);
    }

    [Fact]
    public void The_workspaces_of_a_screen_stay_in_the_order_the_file_gives_them()
    {
        Desk.Reload(Changed(c =>
        {
            c.Workspaces!.RemoveAll(w => w.Name == "13");
            c.Workspaces.Insert(2, new WorkspaceConfig { Name = "99", Monitor = "main" });
        }));

        // A dictionary reuses the slot a removed key freed, so on this desk
        // removing 10 and adding 30 put 30 at the FRONT of the second
        // monitor's list -- and that list is the order the bar draws.
        Assert.Equal(
            ["11", "12", "99"],
            Desk.MonitorByRole("main")!.Workspaces.Take(3).Select(w => w.Name));
    }

    [Fact]
    public void A_rule_added_by_the_reload_reaches_the_next_window_not_the_last_one()
    {
        _fixture.Open(1, process: "zen");
        _fixture.Turn();
        Assert.Equal(WindowState.Tiling, Desk.Window(DeskFixture.W(1))!.State);

        Desk.Reload(Changed(c => c.Rules!.Insert(0, new RuleConfig
        {
            Id = "float-zen",
            Match = [new MatchCriteria { Process = "zen" }],
            Actions = ["float"],
        })));

        // The window already on screen is left alone: re-deciding it would
        // undo whatever the person has done with it since.
        _fixture.Turn();
        Assert.Equal(WindowState.Tiling, Desk.Window(DeskFixture.W(1))!.State);

        _fixture.Open(2, process: "zen");
        _fixture.Turn();
        Assert.Equal(WindowState.Floating, Desk.Window(DeskFixture.W(2))!.State);
    }

    [Fact]
    public void The_executor_refuses_a_reload_it_has_no_source_for()
    {
        var executor = new GlazeExecutor(Desk, new FakeDeskPlatform());

        ExecResult result = executor.Command("wm-reload-config");

        // It used to answer Ok and read nothing at all.
        Assert.False(result.Success);
    }

    [Fact]
    public void The_executor_reports_what_the_reload_did()
    {
        var executor = new GlazeExecutor(
            Desk,
            new FakeDeskPlatform(),
            () => ExecResult.Ok(data: new System.Text.Json.Nodes.JsonObject { ["workspacesAdded"] = 1 }));

        ExecResult result = executor.Command("wm-reload-config");

        Assert.True(result.Success);
        Assert.Equal(1, (int)result.Data!["workspacesAdded"]!);
    }
}
