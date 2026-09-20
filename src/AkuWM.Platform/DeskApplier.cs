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

    public DeskApplier(
        WindowsPlatform platform, CloakLedger ledger, GeometryJournal journal, Win32Taskbar taskbar)
    {
        _platform = platform;
        _ledger = ledger;
        _journal = journal;
        _taskbar = taskbar;
    }

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

    private void Cloak(IReadOnlyList<WindowHandle> windows, bool hidden, HashSet<WindowHandle> refused)
    {
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
