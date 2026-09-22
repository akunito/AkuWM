using AkuWM.Core.Desk;
using AkuWM.Core.Model;
using AkuWM.Core.State;
using Xunit;

namespace AkuWM.Tests;

/// <summary>
/// The crash-survival files, audited 2026-09-22: what a second mapping, a
/// full store, a capacity bump, a reboot or a sleep does to them.
/// </summary>
public class RecordStoreTests
{
    private static StoredWindow Record(long handle, string process = "zen") =>
        new(handle, process, "a window", 1);

    [Fact]
    public void A_full_store_says_so_instead_of_pretending()
    {
        using var dir = new TempDir();
        using var store = new RecordStore(dir.File("r.bin"), capacity: 1);

        Assert.True(store.Put(Record(1)));
        Assert.False(store.Put(Record(2)));
        Assert.True(store.Put(Record(1)), "a window that has a slot keeps it");
    }

    [Fact]
    public void A_broken_store_says_so()
    {
        using var dir = new TempDir();
        using var store = new RecordStore(dir.Path); // a directory, not a file

        Assert.True(store.Broken);
        Assert.False(store.Put(Record(1)));
    }

    [Fact]
    public void Removing_by_a_stale_map_never_erases_another_windows_record()
    {
        using var dir = new TempDir();
        string file = dir.File("r.bin");
        using var daemon = new RecordStore(file, capacity: 2);
        using var rescue = new RecordStore(file, capacity: 2);

        daemon.Put(Record(1));

        // The other process gives window 1 back and frees its slot, then the
        // daemon hides window 2 into the slot that just came free.
        rescue.Rescan();
        rescue.Remove(1);
        daemon.Put(Record(2));

        // The daemon's map still says window 1 lives in slot 0. Forgetting 1
        // by that map zeroed slot 0 -- window 2's record, while 2 was hidden.
        daemon.Remove(1);

        rescue.Rescan();
        Assert.Contains(rescue.All(), r => r.Handle == 2);
    }

    [Fact]
    public void A_reader_sees_what_the_writer_wrote_after_it_opened()
    {
        using var dir = new TempDir();
        string file = dir.File("r.bin");
        using var reader = new RecordStore(file);
        using var writer = new RecordStore(file);

        writer.Put(Record(7));
        Assert.Empty(reader.All());

        reader.Rescan();
        Assert.Single(reader.All(), r => r.Handle == 7);
    }

    [Fact]
    public void Changing_the_capacity_carries_the_records_over()
    {
        using var dir = new TempDir();
        string file = dir.File("r.bin");

        using (var small = new RecordStore(file, capacity: 4))
        {
            small.Put(Record(1));
            small.Put(Record(2, "game"));
        }

        using var bigger = new RecordStore(file, capacity: 8);
        List<StoredWindow> all = bigger.All();

        Assert.Equal(2, all.Count);
        Assert.Contains(all, r => r.Handle == 2 && r.Process == "game");
    }

    [Fact]
    public void A_file_of_another_layout_is_started_fresh_not_misread()
    {
        using var dir = new TempDir();
        string file = dir.File("r.bin");
        byte[] garbage = new byte[16 + (256 * 4)];
        new Random(1).NextBytes(garbage);
        File.WriteAllBytes(file, garbage);

        using var store = new RecordStore(file, capacity: 4);

        Assert.False(store.Broken);
        Assert.Empty(store.All());
    }
}

public class LedgerAuditTests
{
    [Fact]
    public void A_record_that_did_not_reach_the_disk_refuses_the_cloak()
    {
        using var dir = new TempDir();
        var platform = new FakePlatform();
        var ledger = new CloakLedger(dir.File("cloaked.bin"), capacity: 2);
        var applier = new DeskApplier(
            platform, platform, ledger, new GeometryJournal(dir.File("geometry.bin")), new FakeTaskbar());

        platform.WindowList.Add(FakePlatform.Window(1, "zen"));
        platform.WindowList.Add(FakePlatform.Window(2, "zen"));
        platform.WindowList.Add(FakePlatform.Window(3, "zen"));

        ApplyResult result = applier.Apply(new Redraw { Hide = [new(1), new(2), new(3)] });

        Assert.Contains(new WindowHandle(3), result.Refused);
        Assert.False(platform.Window(new WindowHandle(3))!.Cloak.HasFlag(CloakKind.Shell));
        Assert.True(platform.Window(new WindowHandle(2))!.Cloak.HasFlag(CloakKind.Shell));
    }

    [Fact]
    public void A_broken_ledger_means_nothing_is_hidden()
    {
        using var dir = new TempDir();
        var platform = new FakePlatform();
        var ledger = new CloakLedger(dir.Path); // a directory: cannot be opened
        var applier = new DeskApplier(
            platform, platform, ledger, new GeometryJournal(dir.File("geometry.bin")), new FakeTaskbar());
        platform.WindowList.Add(FakePlatform.Window(1, "zen"));

        Assert.False(applier.CanHide);
        ApplyResult result = applier.Apply(new Redraw { Hide = [new(1)] });

        Assert.Contains(new WindowHandle(1), result.Refused);
        Assert.False(platform.Window(new WindowHandle(1))!.Cloak.HasFlag(CloakKind.Shell));
    }

    [Fact]
    public void The_proofs_guinea_pig_is_in_the_ledger_while_it_is_hidden()
    {
        using var dir = new TempDir();
        var platform = new FakePlatform();
        var ledger = new CloakLedger(dir.File("cloaked.bin"));
        var applier = new DeskApplier(
            platform, platform, ledger, new GeometryJournal(dir.File("geometry.bin")), new FakeTaskbar());
        platform.WindowList.Add(FakePlatform.Window(1, "zen"));
        platform.RefusesToUncloak.Add(1);

        applier.Apply(new Redraw { Hide = [new(1)] });

        // It went one way, so the record stays for `rescue` to act on.
        Assert.False(applier.CanHide);
        Assert.Contains(ledger.Entries, e => e.Handle == 1);
    }

    [Fact]
    public void A_guinea_pig_that_closes_mid_proof_leaves_the_proof_untried()
    {
        using var dir = new TempDir();
        var platform = new FakePlatform();
        var ledger = new CloakLedger(dir.File("cloaked.bin"));
        var applier = new DeskApplier(
            platform, platform, ledger, new GeometryJournal(dir.File("geometry.bin")), new FakeTaskbar());
        platform.WindowList.Add(FakePlatform.Window(1, "zen"));
        platform.WindowList.Add(FakePlatform.Window(2, "zen"));

        // Gone between the cloak call and the read-back.
        var closing = new FakePlatform();
        closing.WindowList.Add(FakePlatform.Window(2, "zen"));
        var applierOfAClosingWindow = new DeskApplier(
            closing, new ClosesOnCloak(closing, 1), ledger, new GeometryJournal(dir.File("g2.bin")), new FakeTaskbar());
        closing.WindowList.Add(FakePlatform.Window(1, "zen"));

        ApplyResult result = applierOfAClosingWindow.Apply(new Redraw { Hide = [new(1), new(2)] });

        Assert.True(applierOfAClosingWindow.CanHide);
        Assert.DoesNotContain(ledger.Entries, e => e.Handle == 1);
        Assert.DoesNotContain(new WindowHandle(2), result.Refused);
        Assert.True(closing.Window(new WindowHandle(2))!.Cloak.HasFlag(CloakKind.Shell));
    }

    /// <summary>Actions whose first cloak makes the window vanish.</summary>
    private sealed class ClosesOnCloak(FakePlatform platform, long handle) : Core.Platform.IPlatformActions
    {
        public string? SetCloak(WindowHandle window, bool cloaked)
        {
            if (window.Value == handle)
            {
                platform.WindowList.RemoveAll(w => w.Handle == window);
                return null;
            }

            return platform.SetCloak(window, cloaked);
        }

        public int Place(IReadOnlyList<Placement> placements, bool activate = false) => platform.Place(placements, activate);

        public int PlaceEach(IReadOnlyList<Placement> placements, bool activate = false) => platform.PlaceEach(placements, activate);

        public void SetMaximized(WindowHandle window, bool maximized) => platform.SetMaximized(window, maximized);

        public void SetMinimized(WindowHandle window, bool minimized) => platform.SetMinimized(window, minimized);

        public void SetTopmost(WindowHandle window, bool topmost) => platform.SetTopmost(window, topmost);

        public void PlaceBehind(WindowHandle window, WindowHandle behind) => platform.PlaceBehind(window, behind);

        public bool Focus(WindowHandle window) => platform.Focus(window);

        public void Unfocus() => platform.Unfocus();

        public bool Decorate(WindowHandle window, Decoration decoration) => platform.Decorate(window, decoration);
    }
}

public class SessionAcrossRebootTests
{
    [Fact]
    public void A_run_ended_by_a_reboot_is_not_a_bad_ending()
    {
        using var dir = new TempDir();
        string file = dir.File("session.json");
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // Two runs that never wrote their clean exit, because the machine
        // was restarted under each of them.
        new SessionMarker(file, bootedAt: () => now - 1000).Begin();
        new SessionMarker(file, bootedAt: () => now + 10).Begin();

        SessionVerdict verdict = new SessionMarker(file, bootedAt: () => now + 20).Begin();

        Assert.Equal(0, verdict.UncleanInARow);
        Assert.False(verdict.SafeMode);
    }

    [Fact]
    public void A_run_that_died_since_this_boot_still_counts()
    {
        using var dir = new TempDir();
        string file = dir.File("session.json");
        long booted = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 1000;

        new SessionMarker(file, bootedAt: () => booted).Begin();
        new SessionMarker(file, bootedAt: () => booted).Begin();

        Assert.True(new SessionMarker(file, bootedAt: () => booted).Begin().SafeMode);
    }
}

public class WatchdogAcrossSleepTests
{
    private DateTimeOffset _now = DateTimeOffset.UnixEpoch;

    [Fact]
    public void A_sleeping_machine_is_not_a_stalled_loop()
    {
        int rescues = 0;
        using var watchdog = new Watchdog(TimeSpan.FromSeconds(10), () => rescues++, () => _now);
        watchdog.Beat();

        // The watchdog asked for one second and got an hour: the whole
        // process was suspended, the loop with it.
        _now = _now.AddHours(1);
        Assert.True(watchdog.ForgiveSuspension(TimeSpan.FromSeconds(1), TimeSpan.FromHours(1)));

        Assert.False(watchdog.Check());
        Assert.Equal(0, rescues);
    }

    [Fact]
    public void A_wait_that_took_about_as_long_as_asked_forgives_nothing()
    {
        int rescues = 0;
        using var watchdog = new Watchdog(TimeSpan.FromSeconds(10), () => rescues++, () => _now);
        watchdog.Beat();
        _now = _now.AddSeconds(11);

        Assert.False(watchdog.ForgiveSuspension(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1.2)));
        Assert.True(watchdog.Check());
        Assert.Equal(1, rescues);
    }
}
