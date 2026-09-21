using System.Collections.Generic;
using System.Linq;
using AkuWM.Core.Config;
using AkuWM.Core.Desk;
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

    /// <summary>The arrangements. Each is a name and the screens, primary first.</summary>
    public static TheoryData<string, MonitorSnapshot[]> Arrangements() => new()
    {
        {
            // This desk: 4K at 150 % with a portrait panel at 125 % to its
            // right, whose top is ABOVE the main one's (a negative y).
            "this desk",
            [
                Screen(1, "main", new Rect(0, 0, 3840, 2160), 144, primary: true),
                Screen(2, "second", new Rect(3840, -408, 1440, 2560), 120),
            ]
        },
        {
            // The second screen to the LEFT, so every x on it is negative --
            // the case that catches code treating 0 as the left of the world.
            "second monitor on the left",
            [
                Screen(1, "main", new Rect(0, 0, 3840, 2160), 144, primary: true),
                Screen(2, "second", new Rect(-1920, 0, 1920, 1080), 96),
            ]
        },
        {
            // Stacked, the second one above: negative y, same as a laptop
            // under an external screen.
            "second monitor above",
            [
                Screen(1, "main", new Rect(0, 0, 2560, 1440), 96, primary: true),
                Screen(2, "second", new Rect(0, -1440, 2560, 1440), 96),
            ]
        },
        {
            // The two extremes of scaling next to each other.
            "100 % beside 200 %",
            [
                Screen(1, "main", new Rect(0, 0, 1920, 1080), 96, primary: true),
                Screen(2, "second", new Rect(1920, 0, 3840, 2160), 192),
            ]
        },
        {
            // Three, the main one in the middle: the arrangement Diego said he
            // may plug in.
            "three, main in the middle",
            [
                Screen(1, "main", new Rect(0, 0, 3840, 2160), 144, primary: true),
                Screen(2, "second", new Rect(3840, -408, 1440, 2560), 120),
                Screen(3, "third", new Rect(-2560, 100, 2560, 1440), 96),
            ]
        },
        {
            // THIS desk, as it stands tonight: `monitors list` 2026-09-21,
            // after the ZOWIE went on the left and the vertical one moved up.
            // Three scales at once, two screens starting above the main one's
            // top, one starting left of zero, and a bar on each.
            "this desk with three screens",
            [
                Screen(1, "main", new Rect(0, 0, 3840, 2160), 144, primary: true, taskbar: 0, bar: 42),
                Screen(2, "second", new Rect(3840, -720, 1440, 2560), 120, bar: 35),
                Screen(3, "third", new Rect(-1920, -706, 1920, 1080), 96, bar: 28),
            ]
        },
    };

    /// <summary>Two workspaces per screen, named after it.</summary>
    private static AkuWmConfig Configuration(MonitorSnapshot[] screens) => new()
    {
        General = new GeneralConfig { ToggleWorkspaceOnRefocus = true },
        Gaps = new GapsConfig { Inner = 8, Outer = [0, 0, 0, 0], ScaleWithDpi = true },
        Layout = new LayoutConfig { DefaultDirection = "auto", FloatUnresizable = true },
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

    private static DeskFixture Desk(MonitorSnapshot[] screens) =>
        new(Configuration(screens), screens);

    private static string Show(IEnumerable<Rect> tiles) => string.Join(" | ", tiles);

    /// <summary>A rectangle a third of the way into a screen, and small.</summary>
    private static Rect Inside(MonitorSnapshot screen) => new(
        screen.WorkArea.X + (screen.WorkArea.Width / 3),
        screen.WorkArea.Y + (screen.WorkArea.Height / 3),
        300,
        200);

    [Theory]
    [MemberData(nameof(Arrangements))]
    public void Tiles_reach_every_edge_of_the_screen_they_are_on(string name, MonitorSnapshot[] screens)
    {
        for (int at = 0; at < screens.Length; at++)
        {
            DeskFixture fixture = Desk(screens);
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
    public void A_window_left_where_no_screen_is_comes_back_onto_one(string name, MonitorSnapshot[] screens)
    {
        for (int at = 0; at < screens.Length; at++)
        {
            DeskFixture fixture = Desk(screens);
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
    public void A_window_straddling_two_screens_is_left_where_it_was_put(string name, MonitorSnapshot[] screens)
    {
        // Dropped over the seam on purpose: a manager that tidies this away is
        // one that argues with the person every time they park a window
        // between two screens.
        DeskFixture fixture = Desk(screens);
        fixture.Open(1, frame: Inside(screens[0]), monitor: screens[0].Handle);
        fixture.Desk.SetFloating(DeskFixture.W(1), true);
        fixture.Turn();

        Rect over = OverTheSeam(screens[0].WorkArea, screens[1].WorkArea);
        fixture.Move(1, over);
        fixture.Turn();
        fixture.Turn();

        Assert.True(over == fixture.FrameOf(1), $"{name}: put at {over}, ended at {fixture.FrameOf(1)}");
    }

    [Theory]
    [MemberData(nameof(Arrangements))]
    public void A_window_dropped_on_another_screen_stays_there(string name, MonitorSnapshot[] screens)
    {
        for (int from = 0; from < screens.Length; from++)
        {
            for (int to = 0; to < screens.Length; to++)
            {
                if (from == to)
                {
                    continue;
                }

                DeskFixture fixture = Desk(screens);
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
                Assert.True(dropped == fixture.FrameOf(1),
                    $"{name}: dropped at {dropped}, ended at {fixture.FrameOf(1)}");
            }
        }
    }

    [Theory]
    [MemberData(nameof(Arrangements))]
    public void A_fullscreen_window_covers_its_screen_exactly(string name, MonitorSnapshot[] screens)
    {
        for (int at = 0; at < screens.Length; at++)
        {
            DeskFixture fixture = Desk(screens);
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
