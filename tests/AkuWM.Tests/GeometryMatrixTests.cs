using System.Collections.Generic;
using System.Linq;
using AkuWM.Core.Config;
using AkuWM.Core.Desk;
using AkuWM.Core.Layout;
using AkuWM.Core.Model;
using Xunit;

namespace AkuWM.Tests;

/// <summary>
/// The geometry rules, against monitor arrangements this desk does not have.
/// </summary>
/// <remarks>
/// Diego, 2026-09-21: "tenemos que hacer tests con diferentes zooms,
/// resoluciones y posiciones de los monitores... la geometría que tenemos no
/// es lo suficientemente buena". He is right, and the driven suite cannot do
/// it: it runs on the screens that are plugged in, one arrangement, always the
/// same. Everything here is arithmetic over a monitor table, so a screen to
/// the LEFT of the main one, one above it, a third one, 100 % next to 200 %
/// and a portrait panel all cost nothing to try.
///
/// The rules asserted are the ones a person notices the moment they break:
/// tiles reach every edge of the screen they are on, nothing is placed outside
/// its monitor, a window dropped on another screen stays there, and a
/// fullscreen window covers the monitor exactly.
/// </remarks>
public class GeometryMatrixTests
{
    /// <param name="taskbar">Taken off the BOTTOM of the primary, as Windows does.</param>
    /// <param name="bar">
    /// Taken off the TOP of this screen: Zebar, which sits on every monitor
    /// and is what makes a real work area differ from the bounds on all three
    /// of this desk's screens.
    /// </param>
    private static MonitorSnapshot Screen(
        long handle, string id, Rect bounds, uint dpi,
        bool primary = false, int taskbar = 42, int bar = 0)
        => new()
        {
            Handle = new MonitorHandle(handle),
            DeviceName = $@"\\.\DISPLAY{handle}",
            FriendlyName = id,
            HardwareId = id,
            Bounds = bounds,
            WorkArea = new Rect(
                bounds.X,
                bounds.Y + bar,
                bounds.Width,
                bounds.Height - bar - (primary ? taskbar : 0)),
            Dpi = dpi,
            IsPrimary = primary,
        };

    /// <summary>
    /// Every arrangement against every mode of layout.across_monitors: Diego
    /// asked for the whole suite to run under each, because a geometry rule
    /// that holds in one mode and not another is a rule nobody can trust.
    /// </summary>
    public static TheoryData<string, MonitorSnapshot[], AcrossMode> Arrangements()
    {
        var cases = new TheoryData<string, MonitorSnapshot[], AcrossMode>();
        foreach ((string name, MonitorSnapshot[] screens) in Layouts())
        {
            foreach (AcrossMode mode in Enum.GetValues<AcrossMode>())
            {
                cases.Add($"{name} / {mode.ToString().ToLowerInvariant()}", screens, mode);
            }
        }

        return cases;
    }

    /// <summary>The arrangements. Each is a name and the screens, primary first.</summary>
    private static IEnumerable<(string Name, MonitorSnapshot[] Screens)> Layouts() => new[]
    {
        (
            // This desk: 4K at 150 % with a portrait panel at 125 % to its
            // right, whose top is ABOVE the main one's (a negative y).
            "this desk",
            new MonitorSnapshot[]
            {
                Screen(1, "main", new Rect(0, 0, 3840, 2160), 144, primary: true),
                Screen(2, "second", new Rect(3840, -408, 1440, 2560), 120),
            }),
        (
            // The second screen to the LEFT, so every x on it is negative --
            // the case that catches code treating 0 as the left of the world.
            "second monitor on the left",
            new MonitorSnapshot[]
            {
                Screen(1, "main", new Rect(0, 0, 3840, 2160), 144, primary: true),
                Screen(2, "second", new Rect(-1920, 0, 1920, 1080), 96),
            }),
        (
            // Stacked, the second one above: negative y, same as a laptop
            // under an external screen.
            "second monitor above",
            new MonitorSnapshot[]
            {
                Screen(1, "main", new Rect(0, 0, 2560, 1440), 96, primary: true),
                Screen(2, "second", new Rect(0, -1440, 2560, 1440), 96),
            }),
        (
            // The two extremes of scaling next to each other.
            "100 % beside 200 %",
            new MonitorSnapshot[]
            {
                Screen(1, "main", new Rect(0, 0, 1920, 1080), 96, primary: true),
                Screen(2, "second", new Rect(1920, 0, 3840, 2160), 192),
            }),
        (
            // Three, the main one in the middle: the arrangement Diego said he
            // may plug in.
            "three, main in the middle",
            new MonitorSnapshot[]
            {
                Screen(1, "main", new Rect(0, 0, 3840, 2160), 144, primary: true),
                Screen(2, "second", new Rect(3840, -408, 1440, 2560), 120),
                Screen(3, "third", new Rect(-2560, 100, 2560, 1440), 96),
            }),
        (
            // THIS desk, as it stands tonight: `monitors list` 2026-09-21,
            // after the ZOWIE went on the left and the vertical one moved up.
            // Three scales at once, two screens starting above the main one's
            // top, one starting left of zero, and a bar on each.
            "this desk with three screens",
            new MonitorSnapshot[]
            {
                Screen(1, "main", new Rect(0, 0, 3840, 2160), 144, primary: true, taskbar: 0, bar: 42),
                Screen(2, "second", new Rect(3840, -720, 1440, 2560), 120, bar: 35),
                Screen(3, "third", new Rect(-1920, -706, 1920, 1080), 96, bar: 28),
            }),
    };

    /// <summary>Two workspaces per screen, named after it.</summary>
    private static AkuWmConfig Configuration(MonitorSnapshot[] screens, AcrossMode mode) => new()
    {
        General = new GeneralConfig { ToggleWorkspaceOnRefocus = true },
        Gaps = new GapsConfig { Inner = 8, Outer = [0, 0, 0, 0], ScaleWithDpi = true },
        Layout = new LayoutConfig
        {
            DefaultDirection = "auto",
            FloatUnresizable = true,
            AcrossMonitors = new AcrossConfig { All = mode.ToString().ToLowerInvariant() },
        },
        Monitors = [.. screens.Select(s => new MonitorConfig
        {
            Id = s.HardwareId,
            Match = new MonitorMatch { Edid = s.HardwareId },
        })],
        Workspaces = [.. screens.SelectMany((s, at) => new[]
        {
            new WorkspaceConfig { Name = $"{at + 1}1", Monitor = s.HardwareId },
            new WorkspaceConfig { Name = $"{at + 1}2", Monitor = s.HardwareId },
        })],
        Rules = [],
    };

    private static DeskFixture Desk(MonitorSnapshot[] screens, AcrossMode mode) =>
        new(Configuration(screens, mode), screens);

    private static string Show(IEnumerable<Rect> tiles) => string.Join(" | ", tiles);

    /// <summary>A rectangle a third of the way into a screen, and small.</summary>
    private static Rect Inside(MonitorSnapshot screen) => new(
        screen.WorkArea.X + (screen.WorkArea.Width / 3),
        screen.WorkArea.Y + (screen.WorkArea.Height / 3),
        300,
        200);

    [Theory]
    [MemberData(nameof(Arrangements))]
    public void Tiles_reach_every_edge_of_the_screen_they_are_on(string name, MonitorSnapshot[] screens, AcrossMode mode)
    {
        for (int at = 0; at < screens.Length; at++)
        {
            DeskFixture fixture = Desk(screens, mode);
            string workspace = $"{at + 1}1";

            foreach (long handle in new long[] { 1, 2, 3 })
            {
                fixture.Open(handle, frame: Inside(screens[at]), monitor: screens[at].Handle);
                fixture.Desk.MoveToWorkspace(DeskFixture.W(handle), workspace);
            }

            fixture.Turn();
            fixture.Turn();

            Rect[] tiles = [.. new long[] { 1, 2, 3 }.Select(fixture.FrameOf)];
            Rect area = screens[at].WorkArea;

            Assert.True(area.Left == tiles.Min(t => t.Left), $"{name}: left edge of {area}, tiles {Show(tiles)}");
            Assert.True(area.Right == tiles.Max(t => t.Right), $"{name}: right edge of {area}, tiles {Show(tiles)}");
            Assert.True(area.Top == tiles.Min(t => t.Top), $"{name}: top edge of {area}, tiles {Show(tiles)}");
            Assert.True(area.Bottom == tiles.Max(t => t.Bottom), $"{name}: bottom edge of {area}, tiles {Show(tiles)}");
        }
    }

    [Theory]
    [MemberData(nameof(Arrangements))]
    public void A_window_left_where_no_screen_is_comes_back_onto_one(string name, MonitorSnapshot[] screens, AcrossMode mode)
    {
        for (int at = 0; at < screens.Length; at++)
        {
            DeskFixture fixture = Desk(screens, mode);
            fixture.Open(1, frame: Inside(screens[at]), monitor: screens[at].Handle);
            fixture.Desk.MoveToWorkspace(DeskFixture.W(1), $"{at + 1}1");
            fixture.Desk.SetFloating(DeskFixture.W(1), true);
            fixture.Turn();

            // Where a screen used to be. Windows answers with the primary for
            // a point on no monitor, which is what the fixture does too.
            fixture.Move(1, new Rect(-30000, -30000, 300, 200));
            fixture.Turn();
            fixture.Turn();

            Rect where = fixture.FrameOf(1);
            Rect area = HomeOf(fixture).Snapshot.WorkArea;
            Assert.True(where.FractionInside(area) > 0.5,
                $"{name}: from screen {at + 1}, ended at {where}, off {area}");
        }
    }

    /// <summary>
    /// A rectangle sitting across the join between two screens, its middle
    /// just inside the second one -- wherever that screen happens to be.
    /// </summary>
    private static Rect OverTheSeam(Rect a, Rect b)
    {
        int x = b.X + (b.Width / 2);
        int y = b.Y + (b.Height / 2);

        if (b.Left >= a.Right)
        {
            x = b.Left + 20;
        }
        else if (b.Right <= a.Left)
        {
            x = b.Right - 20;
        }
        else if (b.Top >= a.Bottom)
        {
            y = b.Top + 20;
        }
        else
        {
            y = b.Bottom - 20;
        }

        return new Rect(x - 150, y - 100, 300, 200);
    }

    /// <summary>The screen the window's workspace belongs to.</summary>
    private static DeskMonitor HomeOf(DeskFixture fixture) =>
        fixture.Desk.MonitorOf(fixture.Desk.Workspace(fixture.Managed(1)!.Workspace!)!)!;

    [Theory]
    [MemberData(nameof(Arrangements))]
    public void A_window_straddling_two_screens_is_left_where_it_was_put(string name, MonitorSnapshot[] screens, AcrossMode mode)
    {
        // Dropped over the seam on purpose: a manager that tidies this away is
        // one that argues with the person every time they park a window
        // between two screens.
        DeskFixture fixture = Desk(screens, mode);
        fixture.Open(1, frame: Inside(screens[0]), monitor: screens[0].Handle);
        fixture.Desk.SetFloating(DeskFixture.W(1), true);
        fixture.Turn();

        Rect over = OverTheSeam(screens[0].WorkArea, screens[1].WorkArea);
        fixture.Move(1, over);
        fixture.Turn();
        fixture.Turn();

        Rect want = AcrossMonitors.Resize(over, screens[0].WorkArea, screens[1].WorkArea, mode);
        Rect got = fixture.FrameOf(1);

        Assert.True(want.Width == got.Width && want.Height == got.Height,
            $"{name}: wanted {want}, ended at {got}");

        if (want == over)
        {
            // Nothing resized it, so it stays exactly across the seam.
            Assert.True(over.X == got.X && over.Y == got.Y,
                $"{name}: put at {over}, ended at {got}");
        }
        else
        {
            // Proportional shrank it for the new screen, from the corner the
            // person dropped -- which for a window that was mostly on the
            // other screen leaves it not touching this one at all. Then the
            // ordinary rule applies and it is pulled onto its screen. The
            // corner is kept rather than the centre on purpose: a window
            // shrinking around its middle walks out from under the pointer
            // that is still holding it.
            Assert.True(got.FractionInside(screens[1].WorkArea) > 0.5,
                $"{name}: {got} was not brought onto {screens[1].WorkArea}");
        }
    }

    [Theory]
    [MemberData(nameof(Arrangements))]
    public void A_window_dropped_on_another_screen_stays_there(string name, MonitorSnapshot[] screens, AcrossMode mode)
    {
        for (int from = 0; from < screens.Length; from++)
        {
            for (int to = 0; to < screens.Length; to++)
            {
                if (from == to)
                {
                    continue;
                }

                DeskFixture fixture = Desk(screens, mode);
                fixture.Open(1, frame: Inside(screens[from]), monitor: screens[from].Handle);
                fixture.Desk.MoveToWorkspace(DeskFixture.W(1), $"{from + 1}1");
                fixture.Desk.SetFloating(DeskFixture.W(1), true);
                fixture.Turn();

                Rect dropped = Inside(screens[to]);
                fixture.Move(1, dropped);
                fixture.Turn();
                fixture.Turn();

                Assert.True($"{to + 1}1" == fixture.Managed(1)!.Workspace,
                    $"{name}: dropped on screen {to + 1}, ended on workspace {fixture.Managed(1)!.Workspace}");

                // The corner is the person's in every mode; the size is the
                // mode's, and it is the mode's own arithmetic that says what
                // it should be.
                Rect want = AcrossMonitors.Resize(
                    dropped, screens[from].WorkArea, screens[to].WorkArea, mode);

                Rect got = fixture.FrameOf(1);
                Assert.True(want.Width == got.Width && want.Height == got.Height,
                    $"{name}: dropped at {dropped}, wanted {want}, ended at {got}");

                // The position only when nothing resized it: a window that
                // changes size is re-anchored on the pointer, which the
                // dedicated tests below check to the pixel.
                if (want == dropped)
                {
                    Assert.True(dropped == got, $"{name}: dropped at {dropped}, ended at {got}");
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(Arrangements))]
    public void A_window_too_big_for_a_screen_is_sized_to_fit_it(string name, MonitorSnapshot[] screens, AcrossMode mode)
    {
        // Diego's terminal, dropped on the BenQ: 2020x2591 going onto a screen
        // whose work area is 1920x1052. Windows says a window that size is on
        // whichever screen most of it covers -- which is the screen it came
        // from, every time -- so it was pulled straight back (2026-09-21). A
        // window that cannot fit on the screen it was dropped on is not a size
        // worth defending: keeping it is the same as losing the window.
        for (int at = 0; at < screens.Length; at++)
        {
            DeskFixture fixture = Desk(screens, mode);
            fixture.Open(1, frame: Inside(screens[0]), monitor: screens[0].Handle);
            fixture.Desk.SetFloating(DeskFixture.W(1), true);
            fixture.Turn();

            Rect area = screens[at].WorkArea;
            fixture.Move(1, new Rect(area.X + 20, area.Y + 20, area.Width + 600, area.Height + 900));
            fixture.Turn();
            fixture.Turn();

            Rect where = fixture.FrameOf(1);
            Assert.True(where.Width <= area.Width && where.Height <= area.Height,
                $"{name}: {where} does not fit {area}");
            Assert.True(where.FractionInside(area) > 0.99,
                $"{name}: {where} is not on {area}");
        }
    }

    /// <summary>Diego's example, through the whole desk rather than the arithmetic.</summary>
    /// <remarks>
    /// "si una ventana ocupa 990x990 de un monitor 1000x1000, y la movemos a
    /// un monitor 2000x2000, en modo proporcional, debe ocupar 1980x1980 al
    /// aterrizar. en modo absoluto, llegaria como 990x990."
    /// </remarks>
    [Theory]
    [InlineData(AcrossMode.Proportional, 1980, 1980)]
    [InlineData(AcrossMode.Absolute, 990, 990)]
    [InlineData(AcrossMode.Hybrid, 990, 990)]
    public void Ninety_nine_percent_of_a_small_screen_onto_a_big_one(AcrossMode mode, int width, int height)
    {
        MonitorSnapshot[] screens =
        [
            Screen(1, "main", new Rect(0, 0, 1000, 1000), 96, primary: true, taskbar: 0),
            Screen(2, "second", new Rect(1000, 0, 2000, 2000), 96),
        ];

        DeskFixture fixture = Desk(screens, mode);
        fixture.Open(1, frame: new Rect(5, 5, 990, 990), monitor: screens[0].Handle);
        fixture.Desk.SetFloating(DeskFixture.W(1), true);
        fixture.Turn();
        fixture.Move(1, new Rect(5, 5, 990, 990));
        fixture.Turn();

        // By command, which chooses the position as well.
        fixture.Desk.MoveToWorkspace(DeskFixture.W(1), "21");
        fixture.Turn();
        fixture.Turn();

        Rect where = fixture.FrameOf(1);
        Assert.Equal(width, where.Width);
        Assert.Equal(height, where.Height);
        Assert.True(where.FractionInside(screens[1].WorkArea) > 0.99, $"{where} is not on the big screen");
    }

    [Theory]
    [InlineData(AcrossMode.Proportional, 1980, 1980)]
    [InlineData(AcrossMode.Absolute, 990, 990)]
    [InlineData(AcrossMode.Hybrid, 990, 990)]
    public void And_the_same_window_dragged_there_by_hand(AcrossMode mode, int width, int height)
    {
        MonitorSnapshot[] screens =
        [
            Screen(1, "main", new Rect(0, 0, 1000, 1000), 96, primary: true, taskbar: 0),
            Screen(2, "second", new Rect(1000, 0, 2000, 2000), 96),
        ];

        DeskFixture fixture = Desk(screens, mode);
        fixture.Open(1, frame: new Rect(5, 5, 990, 990), monitor: screens[0].Handle);
        fixture.Desk.SetFloating(DeskFixture.W(1), true);
        fixture.Turn();
        fixture.Move(1, new Rect(5, 5, 990, 990));
        fixture.Turn();

        // Dropped at 1010,10 on the big screen: the size is the mode's, the
        // corner is his.
        fixture.Move(1, new Rect(1010, 10, 990, 990));
        fixture.Turn();
        fixture.Turn();

        Rect where = fixture.FrameOf(1);
        Assert.Equal(width, where.Width);
        Assert.Equal(height, where.Height);

        // And it is still held by the same point of itself. The pointer went
        // to 40,40 into the window when it was dropped; after a proportional
        // resize that is 40 * 1980/990 = 80 into a window twice the size.
        (int X, int Y) at = fixture.Platform.Cursor;
        Assert.Equal(at.X - (40 * width / 990), where.X);
    }

    [Fact]
    public void The_pointer_keeps_its_place_in_a_window_that_resizes_as_it_crosses()
    {
        // Diego chose this over keeping the corner: grabbed a third of the way
        // along, still a third of the way along after it shrinks, so the
        // pointer never ends up outside the window it is dragging.
        MonitorSnapshot[] screens =
        [
            Screen(1, "main", new Rect(0, 0, 2000, 2000), 96, primary: true, taskbar: 0),
            Screen(2, "second", new Rect(2000, 0, 1000, 1000), 96),
        ];

        DeskFixture fixture = Desk(screens, AcrossMode.Proportional);
        fixture.Open(1, frame: new Rect(100, 100, 600, 600), monitor: screens[0].Handle);
        fixture.Desk.SetFloating(DeskFixture.W(1), true);
        fixture.Turn();
        fixture.Move(1, new Rect(100, 100, 600, 600));
        fixture.Turn();

        // Held 200 across and 300 down of a 600x600 window: a third and a half.
        fixture.Move(1, new Rect(2200, 200, 600, 600), hand: (2400, 500));
        fixture.Turn();
        fixture.Turn();

        Rect where = fixture.FrameOf(1);
        Assert.Equal(300, where.Width);
        Assert.Equal(300, where.Height);

        // A third and a half of 300 is 100 and 150.
        Assert.Equal(2400 - 100, where.X);
        Assert.Equal(500 - 150, where.Y);
    }

    [Theory]
    [InlineData(AcrossMode.Absolute)]
    [InlineData(AcrossMode.Proportional)]
    [InlineData(AcrossMode.Hybrid)]
    public void A_window_bigger_than_the_small_screen_still_lands_on_it(AcrossMode mode)
    {
        // Diego's case exactly: a window far too big for the BenQ, dropped
        // with its top-left corner well inside it. Most of it lies over the
        // main monitor, so Windows says that is where it is, and it went home
        // every time.
        MonitorSnapshot[] screens =
        [
            Screen(1, "main", new Rect(0, 0, 3840, 2160), 144, primary: true, taskbar: 0, bar: 42),
            Screen(2, "second", new Rect(3840, -720, 1440, 2560), 120, bar: 35),
            Screen(3, "third", new Rect(-1920, -706, 1920, 1080), 96, bar: 28),
        ];

        DeskFixture fixture = Desk(screens, mode);
        fixture.Open(1, frame: Inside(screens[0]), monitor: screens[0].Handle);
        fixture.Desk.SetSticky(DeskFixture.W(1), true);
        fixture.Turn();

        // 2020x2591 at -1500,-300: the top-left is on the third screen, the
        // middle is not on any, and the bulk of it is over the main one.
        fixture.Move(1, new Rect(-1500, -300, 2020, 2591));
        fixture.Turn();
        fixture.Turn();

        Assert.Equal("third", fixture.Managed(1)!.StickyMonitor);

        Rect area = screens[2].WorkArea;
        Rect where = fixture.FrameOf(1);
        Assert.True(where.FractionInside(area) > 0.99, $"{where} is not on the BenQ {area}");
    }

    [Theory]
    [MemberData(nameof(Arrangements))]
    public void A_fullscreen_window_covers_its_screen_exactly(string name, MonitorSnapshot[] screens, AcrossMode mode)
    {
        for (int at = 0; at < screens.Length; at++)
        {
            DeskFixture fixture = Desk(screens, mode);
            fixture.Open(1, frame: Inside(screens[at]), monitor: screens[at].Handle);
            fixture.Desk.MoveToWorkspace(DeskFixture.W(1), $"{at + 1}1");
            fixture.Turn();

            fixture.Desk.SetFullscreen(DeskFixture.W(1), true);
            fixture.Turn();
            fixture.Turn();

            Assert.True(screens[at].Bounds == fixture.FrameOf(1),
                $"{name}: screen {screens[at].Bounds}, window {fixture.FrameOf(1)}");
        }
    }
}
