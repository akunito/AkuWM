using AkuWM.Core.Model;
using AkuWM.Core.State;
using Xunit;

namespace AkuWM.Tests;

/// <summary>
/// The other half of the promise: a window AkuWM moved goes back where it was.
/// </summary>
/// <remarks>
/// These are written from the failure they exist to prevent -- a manager that
/// died mid-layout and left a desk nobody recognised -- so each one ends by
/// asking the fake desk what it looks like, not by counting calls.
/// </remarks>
public class GeometryJournalTests
{
    private static WindowSnapshot At(long handle, Rect frame, bool maximized = false, bool minimized = false) =>
        FakePlatform.Window(handle, "zen", title: $"window {handle}", frame: frame) with
        {
            IsMaximized = maximized,
            IsMinimized = minimized,
        };

    [Fact]
    public void A_window_goes_back_where_it_was()
    {
        using var dir = new TempDir();
        var platform = new FakePlatform();
        WindowSnapshot window = At(1, new Rect(200, 300, 900, 700));
        platform.WindowList.Add(window);

        var journal = new GeometryJournal(dir.File("geometry.json"));
        journal.Remember(window);

        // AkuWM tiles it somewhere else.
        platform.Place([new Placement(window.Handle, new Rect(0, 0, 1920, 2118))]);
        Assert.Equal(new Rect(0, 0, 1920, 2118), platform.Window(window.Handle)!.FrameBounds);

        GeometryRestoreResult result = journal.Restore(platform, platform);

        Assert.Single(result.Restored);
        Assert.Equal(new Rect(200, 300, 900, 700), platform.Window(window.Handle)!.FrameBounds);
    }

    [Fact]
    public void The_first_touch_is_the_one_remembered()
    {
        using var dir = new TempDir();
        var platform = new FakePlatform();
        WindowSnapshot window = At(1, new Rect(200, 300, 900, 700));
        platform.WindowList.Add(window);

        var journal = new GeometryJournal(dir.File("geometry.json"));
        journal.Remember(window);

        // Every later move must not become the thing that gets restored: what
        // has to come back is the desk before the window manager, not the desk
        // before its last command.
        platform.Place([new Placement(window.Handle, new Rect(0, 0, 1920, 2118))]);
        journal.Remember(platform.Window(window.Handle)!);

        journal.Restore(platform, platform);

        Assert.Equal(new Rect(200, 300, 900, 700), platform.Window(window.Handle)!.FrameBounds);
    }

    [Fact]
    public void A_maximised_window_comes_back_maximised()
    {
        using var dir = new TempDir();
        var platform = new FakePlatform();
        WindowSnapshot window = At(1, new Rect(0, 0, 3840, 2118), maximized: true);
        platform.WindowList.Add(window);

        var journal = new GeometryJournal(dir.File("geometry.json"));
        journal.Remember(window);

        platform.SetMaximized(window.Handle, false);
        platform.Place([new Placement(window.Handle, new Rect(100, 100, 400, 400))]);

        journal.Restore(platform, platform);

        Assert.True(platform.Window(window.Handle)!.IsMaximized);
    }

    [Fact]
    public void A_minimised_window_is_left_minimised()
    {
        using var dir = new TempDir();
        var platform = new FakePlatform();
        WindowSnapshot window = At(1, new Rect(200, 300, 900, 700), minimized: true);
        platform.WindowList.Add(window);

        var journal = new GeometryJournal(dir.File("geometry.json"));
        journal.Remember(window);
        platform.SetMinimized(window.Handle, false);

        journal.Restore(platform, platform);

        Assert.True(platform.Window(window.Handle)!.IsMinimized);
    }

    [Fact]
    public void The_journal_survives_the_process_that_wrote_it()
    {
        using var dir = new TempDir();
        string file = dir.File("geometry.json");
        var platform = new FakePlatform();
        WindowSnapshot window = At(1, new Rect(200, 300, 900, 700));
        platform.WindowList.Add(window);

        new GeometryJournal(file).Remember(window);
        platform.Place([new Placement(window.Handle, new Rect(0, 0, 1920, 2118))]);

        // A different instance, as the next run after a crash would be.
        GeometryRestoreResult result = new GeometryJournal(file).Restore(platform, platform);

        Assert.Single(result.Restored);
        Assert.Equal(new Rect(200, 300, 900, 700), platform.Window(window.Handle)!.FrameBounds);
    }

    [Fact]
    public void A_handle_that_now_belongs_to_somebody_else_is_dropped()
    {
        using var dir = new TempDir();
        string file = dir.File("geometry.json");
        var platform = new FakePlatform();
        platform.WindowList.Add(At(1, new Rect(200, 300, 900, 700)));

        new GeometryJournal(file).Remember(platform.WindowList[0]);

        // Windows hands handle 1 to a different program, which happens to have
        // a window of its own there. Restoring onto it would move a stranger.
        platform.WindowList[0] = FakePlatform.Window(1, "explorer", frame: new Rect(50, 50, 300, 300));

        GeometryRestoreResult result = new GeometryJournal(file).Restore(platform, platform);

        Assert.Empty(result.Restored);
        Assert.Single(result.Stale);
        Assert.Equal(new Rect(50, 50, 300, 300), platform.Window(new WindowHandle(1))!.FrameBounds);
    }

    [Fact]
    public void Restoring_twice_is_harmless()
    {
        using var dir = new TempDir();
        string file = dir.File("geometry.json");
        var platform = new FakePlatform();
        WindowSnapshot window = At(1, new Rect(200, 300, 900, 700));
        platform.WindowList.Add(window);

        var journal = new GeometryJournal(file);
        journal.Remember(window);
        platform.Place([new Placement(window.Handle, new Rect(0, 0, 1920, 2118))]);
        journal.Restore(platform, platform);

        // The exit handler and the next start both restore; the second must
        // not move a window the person has since put somewhere they wanted.
        platform.Place([new Placement(window.Handle, new Rect(10, 10, 500, 500))]);
        GeometryRestoreResult again = journal.Restore(platform, platform);

        Assert.Empty(again.Restored);
        Assert.Equal(new Rect(10, 10, 500, 500), platform.Window(window.Handle)!.FrameBounds);
    }

    [Fact]
    public void A_released_window_is_no_longer_ours_to_put_back()
    {
        using var dir = new TempDir();
        var platform = new FakePlatform();
        WindowSnapshot window = At(1, new Rect(200, 300, 900, 700));
        platform.WindowList.Add(window);

        var journal = new GeometryJournal(dir.File("geometry.json"));
        journal.Remember(window);
        journal.Forget(window.Handle);

        platform.Place([new Placement(window.Handle, new Rect(0, 0, 1920, 2118))]);
        journal.Restore(platform, platform);

        Assert.Equal(new Rect(0, 0, 1920, 2118), platform.Window(window.Handle)!.FrameBounds);
    }
}
