using AkuWM.Core.Config;
using AkuWM.Core.Desk;
using AkuWM.Core.Model;
using AkuWM.Core.State;
using Xunit;

namespace AkuWM.Tests;

/// <summary>
/// Two levels of rules for a new window: an explicit rule of the person's,
/// and failing that how they left the last window of that application --
/// floating or tiled, size, place -- opened on the screen the pointer is on.
/// Elevated windows are left out of both (AkuWM cannot place them).
/// </summary>
public class AppMemoryTests
{
    private static WindowHandle W(long handle) => new(handle);

    private static DeskFixture Desk(bool remember = true, bool underPointer = true)
    {
        AkuWmConfig config = DeskFixture.Configuration();
        config.General!.RememberApps = remember;
        config.General.OpenUnderPointer = underPointer;
        var f = new DeskFixture(config);
        f.Desk.RemembersAppsWith(new AppMemory());
        f.Sync(); // the first sync: after it, a window is new
        return f;
    }

    private static readonly Rect SecondWork = FakePlatform.SecondMonitor().WorkArea;

    [Fact]
    public void A_new_window_opens_on_the_screen_the_pointer_is_on()
    {
        DeskFixture f = Desk();
        f.Open(1);
        f.Turn();
        f.Foreground(1); // the focus is on the main screen
        f.Platform.Cursor = (SecondWork.X + 100, SecondWork.Y + 100);

        f.Open(2, frame: new Rect(300, 300, 800, 600)); // Windows put it on the main screen
        f.Turn();

        Assert.Equal("21", f.Managed(2)!.Workspace);
        Assert.True(f.FrameOf(2).FractionInside(SecondWork) > 0.99, $"{f.FrameOf(2)}");
    }

    [Fact]
    public void With_the_option_off_it_opens_where_the_focus_is()
    {
        DeskFixture f = Desk(underPointer: false);
        f.Open(1);
        f.Turn();
        f.Foreground(1);
        f.Platform.Cursor = (SecondWork.X + 100, SecondWork.Y + 100);

        f.Open(2);
        f.Turn();

        Assert.Equal("11", f.Managed(2)!.Workspace);
    }

    [Fact]
    public void A_rule_that_names_a_workspace_outranks_the_pointer()
    {
        AkuWmConfig config = DeskFixture.Configuration();
        config.Rules = [new RuleConfig { Name = "zen-home", Match = [new MatchCriteria { Process = "zen" }], Target = new RuleTarget { Workspace = "13" } }];
        var f = new DeskFixture(config);
        f.Desk.RemembersAppsWith(new AppMemory());
        f.Sync();
        f.Platform.Cursor = (SecondWork.X + 100, SecondWork.Y + 100);

        f.Open(1, process: "zen");
        f.Turn();

        Assert.Equal("13", f.Managed(1)!.Workspace);
    }

    [Fact]
    public void A_window_closed_floating_reopens_floating_with_the_same_size_and_place_on_the_pointers_screen()
    {
        DeskFixture f = Desk();
        f.Open(1, process: "notepad++");
        Assert.True(f.Desk.SetFloating(W(1), true));
        f.Turn();
        f.Move(1, new Rect(600, 500, 1000, 800));
        f.Wait(AkuWM.Core.Desk.Desk.SettleMs + 1);
        f.Turn();
        f.Turn();
        Assert.Equal(new Rect(600, 500, 1000, 800), f.FrameOf(1));
        f.Close(1);

        // Reopened on the vertical screen: same size, same offset from its work area.
        f.Platform.Cursor = (SecondWork.X + 50, SecondWork.Y + 50);
        f.Open(2, process: "notepad++", frame: new Rect(100, 100, 700, 500));
        f.Turn();
        f.Turn();

        DeskWindow again = f.Managed(2)!;
        Assert.Equal(WindowState.Floating, again.State);
        Assert.Equal("21", again.Workspace);
        // Same offset from the work area, pulled inside it where it does not fit (1440 wide).
        Rect work = FakePlatform.MainMonitor().WorkArea;
        int x = Math.Min(SecondWork.X + (600 - work.X), SecondWork.Right - 1000);
        Assert.Equal(new Rect(x, SecondWork.Y + (500 - work.Y), 1000, 800), f.FrameOf(2));
    }

    [Fact]
    public void A_window_closed_tiled_reopens_tiled_even_after_it_had_floated_once()
    {
        DeskFixture f = Desk();
        f.Open(1, process: "notepad++");
        Assert.True(f.Desk.SetFloating(W(1), true));
        f.Turn();
        f.Close(1);

        f.Open(2, process: "notepad++");
        f.Turn();
        Assert.Equal(WindowState.Floating, f.Managed(2)!.State);
        Assert.True(f.Desk.SetFloating(W(2), false));
        f.Turn();
        f.Close(2);

        f.Open(3, process: "notepad++");
        f.Turn();
        Assert.Equal(WindowState.Tiling, f.Managed(3)!.State);
    }

    [Fact]
    public void A_remembered_rectangle_that_does_not_fit_the_new_screen_is_pulled_inside_it()
    {
        DeskFixture f = Desk();
        f.Open(1, process: "big");
        Assert.True(f.Desk.SetFloating(W(1), true));
        f.Turn();
        f.Move(1, new Rect(1500, 300, 2000, 1500));
        f.Wait(AkuWM.Core.Desk.Desk.SettleMs + 1);
        f.Turn();
        f.Turn();
        f.Close(1);

        f.Platform.Cursor = (SecondWork.X + 50, SecondWork.Y + 50);
        f.Open(2, process: "big");
        f.Turn();
        f.Turn();

        Assert.True(SecondWork.Contains(f.FrameOf(2)), $"{f.FrameOf(2)} not inside {SecondWork}");
        Assert.Equal(1440, f.FrameOf(2).Width); // as wide as the screen allows
    }

    [Fact]
    public void An_elevated_window_and_a_ruled_window_are_not_remembered()
    {
        AkuWmConfig config = DeskFixture.Configuration();
        config.General!.RememberApps = true;
        config.Rules = [new RuleConfig { Name = "steam", Match = [new MatchCriteria { Process = "steam" }], Actions = ["tile"] }];
        var f = new DeskFixture(config);
        var memory = new AppMemory();
        f.Desk.RemembersAppsWith(memory);
        f.Sync();

        f.Open(1, process: "Purple", elevated: true);
        Assert.True(f.Desk.SetFloating(W(1), true));
        f.Turn();
        f.Close(1);

        f.Open(2, process: "steam");
        Assert.True(f.Desk.SetFloating(W(2), true));
        f.Turn();
        f.Close(2);

        Assert.Equal(0, memory.Count);
    }

    [Fact]
    public void With_the_option_off_nothing_is_remembered()
    {
        DeskFixture f = Desk(remember: false);
        f.Open(1, process: "notepad++");
        Assert.True(f.Desk.SetFloating(W(1), true));
        f.Turn();
        f.Close(1);

        f.Open(2, process: "notepad++");
        f.Turn();
        Assert.Equal(WindowState.Tiling, f.Managed(2)!.State);
    }

    [Fact]
    public void The_memory_survives_a_restart_through_its_file()
    {
        string file = Path.Combine(Path.GetTempPath(), "akuwm-apps-" + Guid.NewGuid().ToString("N") + ".tsv");
        try
        {
            var first = new AppMemory(file);
            first.Remember("zen|MozillaWindowClass", new AppRecord(true, 900, 700, 40, 60));

            var second = new AppMemory(file);
            Assert.Equal(new AppRecord(true, 900, 700, 40, 60), second.Recall("zen|MozillaWindowClass"));
            Assert.Null(second.Recall("brave|Chrome_WidgetWin_1"));
        }
        finally
        {
            File.Delete(file);
        }
    }
}
