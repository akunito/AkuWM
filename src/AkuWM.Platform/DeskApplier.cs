using AkuWM.Core.Desk;
using AkuWM.Core.Logging;
using AkuWM.Core.Model;
using AkuWM.Core.State;

namespace AkuWM.Platform;

/// <param name="Placed">Windows moved.</param>
/// <param name="Refused">Windows whose cloak did not take, read back from DWM.</param>
/// <param name="Elapsed">How long the whole batch took.</param>
public readonly record struct ApplyResult(int Placed, IReadOnlySet<WindowHandle> Refused, TimeSpan Elapsed)
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
    private readonly WindowsPlatform _platform;
    private readonly CloakLedger _ledger;
    private readonly GeometryJournal _journal;
    private readonly Win32Taskbar _taskbar;

    private Proof _proof = Proof.Untried;

    private enum Proof
    {
        Untried,
        Good,
        OneWay,
    }

    public DeskApplier(
        WindowsPlatform platform, CloakLedger ledger, GeometryJournal journal, Win32Taskbar taskbar)
    {
        _platform = platform;
        _ledger = ledger;
        _journal = journal;
        _taskbar = taskbar;
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

    public ApplyResult Apply(Redraw redraw)
    {
        if (redraw.IsNothing)
        {
            return new ApplyResult(0, new HashSet<WindowHandle>(), TimeSpan.Zero);
        }

        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        var refused = new HashSet<WindowHandle>();

        int placed = Place(redraw.Place);
        Cloak(redraw.Hide, true, refused);
        Cloak(redraw.Show, false, refused);

        foreach ((WindowHandle window, bool topmost) in redraw.Band)
        {
            Win32Position.SetTopmost(window, topmost);
        }

        foreach ((WindowHandle window, bool fullscreen) in redraw.TaskbarMark)
        {
            _taskbar.MarkFullscreen(window, fullscreen);
        }

        if (!redraw.Focus.IsNone)
        {
            Win32Focus.Focus(redraw.Focus);
        }

        var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(started);
        Log.Debug(() => $"redraw: {redraw} -> {placed} placed, {refused.Count} refused, {elapsed.TotalMilliseconds:F2} ms");

        return new ApplyResult(placed, refused, elapsed);
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

        foreach (Placement placement in placements)
        {
            if (_platform.Window(placement.Window) is { } snapshot)
            {
                _journal.Remember(snapshot);
            }
        }

        return Win32Position.Place(placements);
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
        _proof = Proof.OneWay; // until shown otherwise

        _platform.SetCloak(handle, true);
        bool hid = _platform.Window(handle)?.Cloak.HasFlag(CloakKind.Shell) == true;

        if (!hid)
        {
            Log.Error("windows cannot be hidden on this machine: the cloak does not take. Workspaces will show everything.");
            return;
        }

        _platform.SetCloak(handle, false);
        bool back = _platform.Window(handle)?.Cloak.HasFlag(CloakKind.Shell) != true;

        if (back)
        {
            _proof = Proof.Good;
            Log.Info("hiding a window and bringing it back works on this machine");
            return;
        }

        // It went one way. Everything, in order, before giving up on it.
        Log.Error("a window was hidden and would not come back; trying every way of undoing it");
        foreach ((string what, string? error) in new ImmersiveShell().TryEveryUncloak(handle))
        {
            if (_platform.Window(handle)?.Cloak.HasFlag(CloakKind.Shell) != true)
            {
                Log.Warn($"it came back with {what}");
                break;
            }

            Log.Debug(() => $"  {what}: {error ?? "no error, no effect"}");
        }

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
            WindowSnapshot? before = _platform.Window(handle);
            if (before is null)
            {
                continue;
            }

            if (hidden)
            {
                // Written first: a window nobody can see, with no record of
                // who hid it, is a window that is simply gone.
                _ledger.Record(before);
            }

            string? error = _platform.SetCloak(handle, hidden);
            bool nowCloaked = _platform.Window(handle)?.Cloak.HasFlag(CloakKind.Shell) == true;

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
