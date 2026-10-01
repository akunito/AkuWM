using AkuWM.Core.Desk;
using AkuWM.Core.Layout;
using AkuWM.Core.Model;
using Xunit;

namespace AkuWM.Tests;

/// <summary>
/// A window lives in one tiling tree. On 2026-10-01 Notepad++ sat in the trees
/// of workspaces 12 and 14 at once on the desk -- laid out with 14's windows,
/// listed under 12 by the compat view -- and no unit scenario reproduces how it
/// got there, so the desk heals the state at the two moments it can: when a
/// slot is taken, and at every Sync. (DeskFixture.Turn asserts the invariant
/// after every compute of every test besides.)
/// </summary>
public class OneTreePerWindowTests
{
    private static DeskFixture TwoTiledOnEleven(out Workspace eleven, out Workspace twelve)
    {
        var f = new DeskFixture();
        f.Open(1);
        f.Open(2);
        f.Turn();
        eleven = f.Desk.Workspace("11")!;
        twelve = f.Desk.Workspace("12")!;
        Assert.Contains(new WindowHandle(1), eleven.Tiling.Windows);
        Assert.Contains(new WindowHandle(2), eleven.Tiling.Windows);
        return f;
    }

    [Fact]
    public void A_sync_removes_a_window_from_a_tree_that_is_not_its_workspace()
    {
        DeskFixture f = TwoTiledOnEleven(out Workspace eleven, out Workspace twelve);
        // The corrupted state, made by hand: window 1 also in 12's tree.
        twelve.Tiling.Add(new WindowHandle(1), WindowHandle.None, SplitDirection.Horizontal);
        Assert.Contains(new WindowHandle(1), twelve.Tiling.Windows);

        f.Sync();

        Assert.DoesNotContain(new WindowHandle(1), twelve.Tiling.Windows);
        Assert.Contains(new WindowHandle(1), eleven.Tiling.Windows);
        f.AssertOneTreePerWindow();
    }

    [Fact]
    public void A_sync_removes_a_handle_that_is_no_window_at_all()
    {
        DeskFixture f = TwoTiledOnEleven(out Workspace eleven, out _);
        eleven.Tiling.Add(new WindowHandle(777), WindowHandle.None, SplitDirection.Horizontal);

        f.Sync();

        Assert.DoesNotContain(new WindowHandle(777), eleven.Tiling.Windows);
        Assert.Equal(2, eleven.Tiling.Windows.Count());
    }

    [Fact]
    public void Taking_a_slot_on_a_workspace_leaves_every_other_tree()
    {
        DeskFixture f = TwoTiledOnEleven(out Workspace eleven, out Workspace twelve);
        // Window 1 is moved to 12 by the person; a stale slot for it sits in 13.
        Workspace thirteen = f.Desk.Workspace("13")!;
        thirteen.Tiling.Add(new WindowHandle(1), WindowHandle.None, SplitDirection.Horizontal);

        Assert.True(f.Desk.MoveToWorkspace(new WindowHandle(1), "12"));
        f.Turn();

        Assert.Contains(new WindowHandle(1), twelve.Tiling.Windows);
        Assert.DoesNotContain(new WindowHandle(1), eleven.Tiling.Windows);
        Assert.DoesNotContain(new WindowHandle(1), thirteen.Tiling.Windows);
    }

    [Fact]
    public void The_layout_dump_names_every_tree_and_what_each_window_says()
    {
        DeskFixture f = TwoTiledOnEleven(out _, out Workspace twelve);
        twelve.Tiling.Add(new WindowHandle(1), WindowHandle.None, SplitDirection.Horizontal);

        string json = System.Text.Json.JsonSerializer.Serialize(f.Desk.LayoutDump());

        Assert.Contains("\"name\":\"11\"", json);
        Assert.Contains("\"name\":\"12\"", json);
        // The dump is the diagnostic: it shows the corruption rather than hiding it.
        Assert.Contains("\"says\":\"11\"", json);
        Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(json, "\"handle\":").Count - System.Text.RegularExpressions.Regex.Matches(json, "\"floating\":\\[\\{").Count);
    }
}
