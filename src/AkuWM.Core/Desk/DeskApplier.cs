using AkuWM.Core.Logging;
using AkuWM.Core.Model;
using AkuWM.Core.Platform;
using AkuWM.Core.State;

namespace AkuWM.Core.Desk;

/// <param name="Placed">Windows moved.</param>
/// <param name="Refused">Windows whose cloak did not take, read back from DWM.</param>
/// <param name="Elapsed">How long the whole batch took.</param>
/// <param name="Unmarked">Windows the shell would not take the fullscreen mark for.</param>
public readonly record struct ApplyResult(
    int Placed,
    IReadOnlySet<WindowHandle> Refused,
    TimeSpan Elapsed,
    IReadOnlySet<WindowHandle>? Unmarked = null,
    IReadOnlySet<WindowHandle>? Undecorated = null,
    bool FocusRefused = false,
    IReadOnlySet<WindowHandle>? Unbanded = null)
{
    public override string ToString() =>
        $"{Placed} placed, {Refused.Count} refused, {Elapsed.TotalMilliseconds:F2} ms";
}

/// <summary>
/// Carries out a <see cref="Redraw"/> against the real desk.
/// </summary>
/// <remarks>
/// <para>
/// The order is deliberate and is the difference between a desk that changes
/// and one that flickers. Windows are <em>placed first, while they are still
/// hidden</em>, so nothing is ever seen in the wrong position. Then the
/// outgoing workspace is cloaked, then the incoming one uncloaked, so the two
/// sets are never both on screen. Bands and the taskbar mark come last,
/// because they are the only ones a person cannot see happening.
/// </para>
/// <para>
/// Nothing is assumed to have worked. A cloak is read back from DWM, because
/// the shell has a spelling of that call which reports success and does
/// nothing at all -- and a window manager that believes it has hidden a window
/// it has not will draw the next workspace on top of the last one.
/// </para>
/// <para>
/// Both records are written <strong>before</strong> the change they describe.
/// A crash between the write and the call leaves an entry for something that
/// never happened, which costs one wasted check on the next start; a crash
/// the other way round leaves a window hidden with nobody who knows it.
/// </para>
/// </remarks>
public sealed class DeskApplier
{
    private readonly IPlatform _platform;
    private readonly IPlatformActions _actions;
    private readonly CloakLedger _ledger;
    private readonly GeometryJournal _journal;
    private readonly ITaskbar _taskbar;

    /// <summary>
    /// Everything else the platform can think of to undo a cloak, tried once
    /// when a window will not come back.
    /// </summary>
    private readonly Func<WindowHandle, IEnumerable<(string What, string? Error)>>? _lastResort;

    /// <summary>The snapshot the model already holds, when it holds one.</summary>
    private Func<WindowHandle, WindowSnapshot?>? _known;

    public void ReadsFrom(Func<WindowHandle, WindowSnapshot?> known) => _known = known;

    private Proof _proof = Proof.Untried;

    private enum Proof
    {
        Untried,
        Good,
        OneWay,
    }

    /// <param name="lastResort">
    /// Every other spelling of the uncloak, for the one moment it is worth
    /// trying them: a window that has been hidden and will not come back.
    /// </param>
    public DeskApplier(
        IPlatform platform,
        IPlatformActions actions,
        CloakLedger ledger,
        GeometryJournal journal,
        ITaskbar taskbar,
        Func<WindowHandle, IEnumerable<(string What, string? Error)>>? lastResort = null)
    {
        _platform = platform;
        _actions = actions;
        _ledger = ledger;
        _journal = journal;
        _taskbar = taskbar;
        _lastResort = lastResort;

        if (ledger.Broken)
        {
            // No record can be written, so no window may be hidden: what the
            // records exist to survive is exactly what would lose it.
            _proof = Proof.OneWay;
            Log.Error("the cloak ledger cannot be written; AkuWM will not hide any window this run");
        }
    }

    /// <summary>
    /// False once hiding has been shown to be a one-way door on this machine.
    /// </summary>
    /// <remarks>
    /// The model stops asking for windows to be hidden, which costs the
    /// workspaces their point and keeps every window on the screen. That is a
    /// bad desk; a window that cannot be brought back is a lost one.
    /// </remarks>
    public bool CanHide => _proof != Proof.OneWay;

    /// <summary>
    /// What the model already holds, or a read when it does not.
    /// </summary>
    /// <remarks>
    /// The desk has a snapshot of every window it manages. Asking Windows again
    /// for facts we are holding cost ~560 Win32 calls per workspace switch.
    /// The read-BACK after a cloak is different and stays a real read: the
    /// shell has a spelling of that call which reports success and does
    /// nothing.
    /// </remarks>
    private WindowSnapshot? Look(WindowHandle window) => _known?.Invoke(window) ?? _platform.Window(window);

    public ApplyResult Apply(Redraw redraw)
    {
        if (redraw.IsNothing)
        {
            return new ApplyResult(0, new HashSet<WindowHandle>(), TimeSpan.Zero);
        }

        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        var refused = new HashSet<WindowHandle>();

        // Before the placements: a minimised window has no frame to move, so
        // placing it first throws the move away; a maximised one ignores it.
        for (int i = 0; i < redraw.Restore.Count; i++)
        {
            _actions.SetMinimized(redraw.Restore[i], false);
        }

        for (int i = 0; i < redraw.Unmaximize.Count; i++)
        {
            _actions.SetMaximized(redraw.Unmaximize[i], false);
        }

        // Which window, which rectangle: the count alone could not say whose
        // placement a storm was made of.
        if (Log.DebugOn)
        {
            for (int i = 0; i < redraw.Place.Count; i++)
            {
                Placement p = redraw.Place[i];
                Log.Debug($"  place {p.Window} {Look(p.Window)?.ProcessName} -> {p.Frame} border {p.Border?.ToString() ?? "re-read"}");
            }

            for (int i = 0; i < redraw.Band.Count; i++)
            {
                Log.Debug($"  band {redraw.Band[i].Window} topmost={redraw.Band[i].Topmost}");
            }

            for (int i = 0; i < redraw.Raise.Count; i++)
            {
                Log.Debug($"  raise {redraw.Raise[i]} over the tiles");
            }

            for (int i = 0; i < redraw.Lower.Count; i++)
            {
                Log.Debug($"  lower {redraw.Lower[i]} behind the tiles");
            }
        }

        // Timed apart from the rest. The whole-redraw number could not answer
        // the question the log kept asking: 1683 redraws on this desk had a
        // median of 1.13 ms and a p99 of 626 ms, and the slow ones were six or
        // seven windows placed with nothing else to do. Whether that is
        // SetWindowPos waiting for each application, or anything of AkuWM's,
        // is one subtraction away once the two are logged separately.
        long placing = System.Diagnostics.Stopwatch.GetTimestamp();
        int placed = Place(redraw.Place);
        var placeTook = System.Diagnostics.Stopwatch.GetElapsedTime(placing);

        // And every other phase apart: "48 ms of SetTopmost and DWM calls"
        // in the plan was the remainder after the placement, unattributed,
        // and the focus fallbacks alone can be 120 ms of polling.
        long at = System.Diagnostics.Stopwatch.GetTimestamp();
        Cloak(redraw.Hide, true, refused);
        Cloak(redraw.Show, false, refused);
        var cloakTook = Lap(ref at);

        HashSet<WindowHandle>? unbanded = null;
        foreach ((WindowHandle window, bool topmost) in redraw.Band)
        {
            if (!_actions.SetTopmost(window, topmost))
            {
                (unbanded ??= []).Add(window);
            }
        }

        foreach ((WindowHandle window, WindowHandle game) in redraw.Behind)
        {
            _actions.PlaceBehind(window, game);
        }

        for (int i = 0; i < redraw.Raise.Count; i++)
        {
            _actions.Raise(redraw.Raise[i]);
        }

        for (int i = 0; i < redraw.Lower.Count; i++)
        {
            _actions.Lower(redraw.Lower[i]);
        }

        var bandTook = Lap(ref at);

        // The shell can refuse, and does when explorer.exe has just restarted.
        // Recording the mark as applied anyway left the taskbar sitting over a
        // game for the rest of the session, with the model certain it had told
        // it otherwise. Allocated only when something actually fails.
        HashSet<WindowHandle>? unmarked = null;
        HashSet<WindowHandle>? undecorated = null;

        // Last of the visible changes, and cheapest: two shell calls that
        // change nothing a person could lose. A refusal is reported, not
        // recorded as done: UIPI refuses an elevated window from a build
        // without uiAccess, and the model believed the border was there.
        foreach ((WindowHandle window, Decoration how) in redraw.Decorate)
        {
            if (!_actions.Decorate(window, how, redraw.Forced.Contains(window)))
            {
                (undecorated ??= []).Add(window);
            }
        }

        for (int i = 0; i < redraw.Outline.Count; i++)
        {
            Outline o = redraw.Outline[i];
            _actions.Outline(o.Window, o.Frame, o.Colour, o.Topmost);
        }

        var decorateTook = Lap(ref at);

        // Before the cloak would have been the wrong order: a button taken off
        // a window that is still on screen looks like the window vanished from
        // the bar for no reason. After it, the two happen together.
        foreach ((WindowHandle window, bool shown) in redraw.TaskbarButton)
        {
            _taskbar.ShowInTaskbar(window, shown);
        }

        foreach ((WindowHandle window, bool fullscreen) in redraw.TaskbarMark)
        {
            if (!_taskbar.MarkFullscreen(window, fullscreen))
            {
                (unmarked ??= []).Add(window);
                continue;
            }

            // Logged even on success, and at INF: the shell accepts this call
            // and then decides for itself, so "did AkuWM ask?" and "did the
            // taskbar move?" are separate questions and only the first one is
            // answerable from inside this process.
            Logging.Log.Info($"taskbar told {window} is {(fullscreen ? "fullscreen" : "not fullscreen")}");
        }

        var taskbarTook = Lap(ref at);

        bool focusRefused = false;
        if (!redraw.Focus.IsNone)
        {
            focusRefused = !_actions.Focus(redraw.Focus);
        }
        else if (redraw.Unfocus)
        {
            _actions.Unfocus();
        }

        var focusTook = Lap(ref at);
        var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(started);
        Log.Debug(() => $"redraw: {redraw} -> {placed} placed, {refused.Count} refused, "
            + $"{elapsed.TotalMilliseconds:F2} ms (place {placeTook.TotalMilliseconds:F2}, cloak {cloakTook.TotalMilliseconds:F2}, "
            + $"band {bandTook.TotalMilliseconds:F2}, decorate {decorateTook.TotalMilliseconds:F2}, "
            + $"taskbar {taskbarTook.TotalMilliseconds:F2}, focus {focusTook.TotalMilliseconds:F2})");

        // The slow tail of the day's redraws was six or seven windows placed
        // with nothing else to do, about 100 ms each: SetWindowPos waiting
        // for the application. Which application is the question, and it is
        // answered here, once per slow placement, by name.
        if (placeTook.TotalMilliseconds > SlowPlacementMs && redraw.Place.Count > 0)
        {
            Log.Info($"placing {redraw.Place.Count} window(s) took {placeTook.TotalMilliseconds:F0} ms: {Names(redraw.Place)}");
        }

        return new ApplyResult(placed, refused, elapsed, unmarked, undecorated, focusRefused, unbanded);
    }

    /// <summary>Above this, a placement names the windows it waited on.</summary>
    public const int SlowPlacementMs = 50;

    private static TimeSpan Lap(ref long since)
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        var took = System.Diagnostics.Stopwatch.GetElapsedTime(since, now);
        since = now;
        return took;
    }

    private string Names(IReadOnlyList<Placement> placements)
    {
        var names = new System.Text.StringBuilder();
        for (int i = 0; i < placements.Count && i < 8; i++)
        {
            if (i > 0)
            {
                names.Append(", ");
            }

            names.Append(Look(placements[i].Window)?.ProcessName ?? placements[i].Window.ToString());
        }

        return names.ToString();
    }

    /// <summary>
    /// Moves windows, remembering where each one was the first time it is
    /// touched.
    /// </summary>
    private int Place(IReadOnlyList<Placement> placements)
    {
        if (placements.Count == 0)
        {
            return 0;
        }

        // A full window read is fourteen Win32 calls plus a COM one. After the
        // first redraw of a run the journal already knows every window being
        // moved, so all of them were waste.
        foreach (Placement placement in placements)
        {
            if (_journal.Knows(placement.Window))
            {
                continue;
            }

            if (Look(placement.Window) is { } snapshot)
            {
                _journal.Remember(snapshot);
            }
        }

        return _actions.Place(placements);
    }

    /// <summary>
    /// Hides one window and brings it straight back, once, before AkuWM hides
    /// anything in earnest.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Hiding a window is the one operation a window manager cannot be allowed
    /// to get wrong: there is no undo for a window nobody can see, find in
    /// Alt+Tab, or click on the taskbar. The call that does it is undocumented
    /// and has a spelling -- measured, spike S8 -- that hides a window and
    /// cannot be undone by anything at all.
    /// </para>
    /// <para>
    /// So the first hide of every run proves the round trip on the window it
    /// is about to hide, and if the window does not come back, AkuWM stops
    /// hiding windows for the rest of the run and says so. Two extra calls,
    /// once, against losing somebody's work.
    /// </para>
    /// </remarks>
    private void ProveTheRoundTrip(WindowHandle handle)
    {
        // The guinea pig is a hidden window like any other: recorded BEFORE
        // the cloak, so that a daemon dying between the two calls, or a
        // cloak that never comes off, leaves a record for `rescue` to act on.
        // The proof ran for a day without one.
        WindowSnapshot? before = Look(handle);
        if (before is null)
        {
            return; // gone before it could be tried; the next hide proves it
        }

        if (!_ledger.Record(before))
        {
            _proof = Proof.OneWay;
            Log.Error("the cloak ledger would not take a record; AkuWM will not hide any window this run");
            return;
        }

        _proof = Proof.OneWay; // until shown otherwise

        _actions.SetCloak(handle, true);
        WindowSnapshot? after = _platform.Window(handle);
        if (after is null)
        {
            // Closed between the two calls. Nothing is known about the cloak
            // on this machine yet, and nothing is hidden.
            _ledger.Forget(handle);
            _proof = Proof.Untried;
            return;
        }

        if (!after.Cloak.HasFlag(CloakKind.Shell))
        {
            _ledger.Forget(handle);
            Log.Error("windows cannot be hidden on this machine: the cloak does not take. Workspaces will show everything.");
            return;
        }

        _actions.SetCloak(handle, false);
        bool back = _platform.Window(handle)?.Cloak.HasFlag(CloakKind.Shell) != true;

        if (back)
        {
            _ledger.Forget(handle);
            _proof = Proof.Good;
            Log.Info("hiding a window and bringing it back works on this machine");
            return;
        }

        // It went one way. Everything, in order, before giving up on it.
        Log.Error("a window was hidden and would not come back; trying every way of undoing it");
        foreach ((string what, string? error) in _lastResort?.Invoke(handle) ?? [])
        {
            if (_platform.Window(handle)?.Cloak.HasFlag(CloakKind.Shell) != true)
            {
                _ledger.Forget(handle);
                Log.Warn($"it came back with {what}");
                break;
            }

            Log.Debug(() => $"  {what}: {error ?? "no error, no effect"}");
        }

        // Still hidden: the record stays, and `rescue` is what gives it back.

        Log.Error(
            "AkuWM will not hide any window this run: hiding one is a one-way door on this machine. "
            + "Every workspace will show all of its windows. Report this with `akuwm doctor`.");
    }

    private void Cloak(IReadOnlyList<WindowHandle> windows, bool hidden, HashSet<WindowHandle> refused)
    {
        if (hidden && _proof == Proof.Untried && windows.Count > 0)
        {
            ProveTheRoundTrip(windows[0]);
        }

        if (hidden && _proof == Proof.OneWay)
        {
            foreach (WindowHandle handle in windows)
            {
                refused.Add(handle);
            }

            return;
        }

        foreach (WindowHandle handle in windows)
        {
            WindowSnapshot? before = Look(handle);
            if (before is null)
            {
                continue;
            }

            if (hidden && !_ledger.Record(before))
            {
                // Written first: a window nobody can see, with no record of
                // who hid it, is a window that is simply gone. No record, no
                // cloak.
                refused.Add(handle);
                Log.Warn($"not hiding {before.ProcessName} \"{before.Title}\": its record could not be written");
                continue;
            }

            string? error = _actions.SetCloak(handle, hidden);
            bool nowCloaked = _platform.CloakOf(handle).HasFlag(CloakKind.Shell);

            if (error is not null || nowCloaked != hidden)
            {
                refused.Add(handle);
                Log.Warn(
                    $"{(hidden ? "hiding" : "showing")} {before.ProcessName} \"{before.Title}\" did not take"
                    + (error is null ? " (the call reported success)" : $": {error}"));

                if (hidden)
                {
                    _ledger.Forget(handle); // it is not hidden, so there is nothing to give back
                }

                continue;
            }

            if (!hidden)
            {
                _ledger.Forget(handle);
            }
        }
    }
}
