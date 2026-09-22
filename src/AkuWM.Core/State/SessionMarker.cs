using AkuWM.Core.Logging;

namespace AkuWM.Core.State;

/// <param name="StartedAt">Seconds since the epoch.</param>
/// <param name="Pid">The process that wrote it.</param>
/// <param name="CleanExit">Set on the way out; false means the run ended some other way.</param>
/// <param name="UncleanInARow">How many runs in a row have ended badly, this one included.</param>
public sealed record Session(long StartedAt, int Pid, bool CleanExit, int UncleanInARow);

/// <summary>
/// How the last run ended, and what to do about it.
/// </summary>
/// <remarks>
/// <para>
/// A window manager that crashes while it is hiding windows will, on restart,
/// hide them again -- and crash again. Two runs in a row that never reached
/// their own shutdown is enough evidence that taking over the desk a third
/// time is not what the person sitting in front of it wants.
/// </para>
/// <para>
/// So AkuWM comes up in <strong>safe mode</strong> instead: the desk is
/// restored, nothing is managed, and the reason is on the console and in
/// <c>doctor</c>. It is a refusal to act, never a refusal to start, because a
/// window manager that will not start is a window manager that cannot put
/// anything back.
/// </para>
/// </remarks>
public sealed class SessionMarker
{
    /// <summary>Two bad endings in a row. One is an accident; two is a pattern.</summary>
    public const int SafeModeAfter = 2;

    private readonly string _file;
    private readonly Func<long> _bootedAt;

    /// <param name="bootedAt">
    /// When the machine came up, in seconds since the epoch. Injected by the
    /// tests; the real one is now minus the uptime.
    /// </param>
    public SessionMarker(string file, Func<long>? bootedAt = null)
    {
        _file = file;
        _bootedAt = bootedAt ?? (() => DateTimeOffset.UtcNow.ToUnixTimeSeconds() - (Environment.TickCount64 / 1000));
    }

    public Session? Previous => AtomicJson.Read<Session>(_file, "the session marker");

    /// <summary>Opens a run, and says whether the two before it ended badly.</summary>
    public SessionVerdict Begin()
    {
        Session? previous = Previous;

        // A run that started before this boot was ended by the machine going
        // down, not by anything of its own: a reboot or a logoff kills the
        // daemon before its exit path can write the marker, and two of those
        // in a row would have put the third boot in safe mode, managing
        // nothing. Windows does not run the exit handlers of a hidden console
        // application at shutdown reliably enough to count on.
        bool endedByTheMachine = previous is not null && previous.StartedAt < _bootedAt();
        int unclean = previous is null || previous.CleanExit || endedByTheMachine ? 0 : previous.UncleanInARow + 1;

        AtomicJson.Write(
            _file,
            new Session(DateTimeOffset.UtcNow.ToUnixTimeSeconds(), Environment.ProcessId, false, unclean),
            "the session marker");

        if (unclean > 0)
        {
            Log.Warn($"the previous run did not shut down cleanly ({unclean} in a row)");
        }

        return new SessionVerdict(unclean, unclean >= SafeModeAfter);
    }

    /// <summary>Closes a run. Reaching this is what makes the next start an ordinary one.</summary>
    public void End() =>
        AtomicJson.Write(
            _file,
            new Session(DateTimeOffset.UtcNow.ToUnixTimeSeconds(), Environment.ProcessId, true, 0),
            "the session marker");

    /// <summary>Forgets the bad runs, so the next start manages the desk again.</summary>
    public void Forgive() => End();
}

/// <param name="UncleanInARow">Runs in a row that ended badly, this one included.</param>
/// <param name="SafeMode">True when AkuWM should restore the desk and manage nothing.</param>
public readonly record struct SessionVerdict(int UncleanInARow, bool SafeMode)
{
    public string Reason => SafeMode
        ? $"the last {UncleanInARow} runs ended without shutting down. AkuWM is managing nothing "
          + "until you say otherwise: start it with `akuwm daemon --force` to take over anyway, "
          + "or `akuwm rescue` to put the desk back and stay out."
        : "the previous run ended cleanly";
}
