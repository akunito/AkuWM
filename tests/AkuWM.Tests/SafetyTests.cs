using AkuWM.Core.State;
using Xunit;

namespace AkuWM.Tests;

/// <summary>
/// The two things that decide whether a bad run can be survived: knowing the
/// last one ended badly, and noticing that this one has stopped moving.
/// </summary>
public class SessionMarkerTests
{
    [Fact]
    public void A_first_run_is_an_ordinary_one()
    {
        using var dir = new TempDir();
        SessionVerdict verdict = new SessionMarker(dir.File("session.json")).Begin();

        Assert.Equal(0, verdict.UncleanInARow);
        Assert.False(verdict.SafeMode);
    }

    [Fact]
    public void One_bad_ending_is_an_accident()
    {
        using var dir = new TempDir();
        string file = dir.File("session.json");

        new SessionMarker(file).Begin(); // and then the process dies

        SessionVerdict verdict = new SessionMarker(file).Begin();

        Assert.Equal(1, verdict.UncleanInARow);
        Assert.False(verdict.SafeMode);
    }

    [Fact]
    public void Two_bad_endings_in_a_row_are_a_pattern()
    {
        using var dir = new TempDir();
        string file = dir.File("session.json");

        new SessionMarker(file).Begin();
        new SessionMarker(file).Begin();

        SessionVerdict verdict = new SessionMarker(file).Begin();

        Assert.Equal(2, verdict.UncleanInARow);
        Assert.True(verdict.SafeMode);
        Assert.Contains("--force", verdict.Reason);
    }

    [Fact]
    public void A_clean_ending_wipes_the_slate()
    {
        using var dir = new TempDir();
        string file = dir.File("session.json");

        new SessionMarker(file).Begin();
        new SessionMarker(file).Begin();

        var third = new SessionMarker(file);
        third.Begin();
        third.End();

        Assert.False(new SessionMarker(file).Begin().SafeMode);
    }

    [Fact]
    public void A_missing_marker_never_stops_a_start()
    {
        using var dir = new TempDir();
        string file = dir.File("session.json");
        File.WriteAllText(file, "{ this is not json");

        // A state file that only ever helps must never be the reason AkuWM
        // refuses to run: unreadable is treated as absent.
        Assert.False(new SessionMarker(file).Begin().SafeMode);
    }
}

public class WatchdogTests
{
    private DateTimeOffset _now = DateTimeOffset.UnixEpoch;

    [Fact]
    public void A_loop_that_keeps_answering_is_left_alone()
    {
        int rescues = 0;
        using var watchdog = new Watchdog(TimeSpan.FromSeconds(10), () => rescues++, () => _now);

        for (int i = 0; i < 20; i++)
        {
            _now = _now.AddSeconds(5);
            watchdog.Beat();
            watchdog.Check();
        }

        Assert.Equal(0, rescues);
        Assert.False(watchdog.Fired);
    }

    [Fact]
    public void A_loop_that_stops_answering_gives_the_desk_back()
    {
        int rescues = 0;
        using var watchdog = new Watchdog(TimeSpan.FromSeconds(10), () => rescues++, () => _now);

        watchdog.Beat();
        _now = _now.AddSeconds(11);

        Assert.True(watchdog.Check());
        Assert.Equal(1, rescues);
    }

    [Fact]
    public void The_rescue_happens_once_however_long_the_stall_lasts()
    {
        int rescues = 0;
        using var watchdog = new Watchdog(TimeSpan.FromSeconds(10), () => rescues++, () => _now);

        watchdog.Beat();
        _now = _now.AddSeconds(60);

        watchdog.Check();
        watchdog.Check();
        watchdog.Check();

        // Repeating it would fight whoever picked the desk up afterwards.
        Assert.Equal(1, rescues);
    }

    [Fact]
    public void A_rescue_that_throws_does_not_take_the_watchdog_with_it()
    {
        using var watchdog = new Watchdog(
            TimeSpan.FromSeconds(10), () => throw new InvalidOperationException("no"), () => _now);

        watchdog.Beat();
        _now = _now.AddSeconds(11);

        Assert.True(watchdog.Check());
        Assert.True(watchdog.Fired);
    }
}
