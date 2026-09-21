using System.Collections.Generic;
using AkuWM.Core.Model;
using AkuWM.Core.Platform;
using Xunit;

namespace AkuWM.Tests;

/// <summary>
/// The safety net under the display-change notification.
/// </summary>
/// <remarks>
/// The notification is a Windows broadcast and it was not arriving at all:
/// AkuWM listened on a message-only window, which broadcasts skip. Measured
/// 2026-09-21 with a third monitor plugged in -- AutoHotkey saw it at once,
/// AkuWM went on answering with the two screens it had seen at startup. That
/// is fixed at the source; this is what makes a missed one survivable.
/// </remarks>
public class ScreenWatchTests
{
    private long _now = 1000;

    private ScreenWatch Watch() => new(() => _now);

    private static MonitorSnapshot Screen(long handle, Rect bounds) => new()
    {
        Handle = new MonitorHandle(handle),
        DeviceName = $@"\\.\DISPLAY{handle}",
        FriendlyName = $"screen {handle}",
        HardwareId = $"ID{handle}",
        Bounds = bounds,
        WorkArea = bounds,
        Dpi = 96,
        IsPrimary = handle == 1,
    };

    [Fact]
    public void The_first_look_is_a_change()
    {
        ScreenWatch watch = Watch();
        Assert.True(watch.Changed(() => [Screen(1, new Rect(0, 0, 1920, 1080))]));
    }

    [Fact]
    public void Unless_it_was_primed_with_them()
    {
        // The desk is built from a list the caller read itself; the watch is
        // told what that was, so the first pass does not report a change that
        // never happened.
        ScreenWatch watch = Watch();
        IReadOnlyList<MonitorSnapshot> screens = [Screen(1, new Rect(0, 0, 1920, 1080))];

        watch.Prime(screens);
        _now += ScreenWatch.EveryMs;

        Assert.False(watch.Changed(() => screens));
    }

    [Fact]
    public void The_same_screens_are_not()
    {
        ScreenWatch watch = Watch();
        IReadOnlyList<MonitorSnapshot> screens = [Screen(1, new Rect(0, 0, 1920, 1080))];

        watch.Changed(() => screens);
        _now += ScreenWatch.EveryMs;

        Assert.False(watch.Changed(() => screens));
    }

    [Fact]
    public void A_monitor_plugged_in_is()
    {
        ScreenWatch watch = Watch();
        watch.Changed(() => [Screen(1, new Rect(0, 0, 1920, 1080))]);
        _now += ScreenWatch.EveryMs;

        Assert.True(watch.Changed(() =>
            [Screen(1, new Rect(0, 0, 1920, 1080)), Screen(2, new Rect(1920, 0, 1440, 2560))]));
    }

    [Fact]
    public void And_so_is_one_that_moved()
    {
        // Diego moved the vertical monitor: same screen, same id, a rectangle
        // 312 px further up. Nothing about the SET of screens changed.
        ScreenWatch watch = Watch();
        watch.Changed(() => [Screen(2, new Rect(3840, -373, 1440, 2525))]);
        _now += ScreenWatch.EveryMs;

        Assert.True(watch.Changed(() => [Screen(2, new Rect(3840, -685, 1440, 2525))]));
    }

    [Fact]
    public void A_taskbar_that_moved_is_a_change_too()
    {
        ScreenWatch watch = Watch();
        MonitorSnapshot before = Screen(1, new Rect(0, 0, 1920, 1080));
        watch.Changed(() => [before]);
        _now += ScreenWatch.EveryMs;

        // Same bounds, less work area: the taskbar changed side or grew.
        Assert.True(watch.Changed(() =>
            [before with { WorkArea = new Rect(0, 0, 1920, 1040) }]));
    }

    [Fact]
    public void It_does_not_read_the_list_more_often_than_it_should()
    {
        // The list is read with EnumDisplayMonitors on the window-manager
        // thread. Every pass would be a syscall per idle second for nothing.
        ScreenWatch watch = Watch();
        int reads = 0;

        IReadOnlyList<MonitorSnapshot> Read()
        {
            reads++;
            return [Screen(1, new Rect(0, 0, 1920, 1080))];
        }

        watch.Changed(Read);
        _now += ScreenWatch.EveryMs - 1;
        watch.Changed(Read);
        watch.Changed(Read);

        Assert.Equal(1, reads);

        _now += 1;
        watch.Changed(Read);
        Assert.Equal(2, reads);
    }
}
