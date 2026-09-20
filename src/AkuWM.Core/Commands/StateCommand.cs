using AkuWM.Core.Config;
using AkuWM.Core.Ipc;
using AkuWM.Core.State;

namespace AkuWM.Core.Commands;

/// <summary>
/// <c>akuwm state</c>: what AkuWM has written down about the desk.
/// </summary>
/// <remarks>
/// The records live in memory-mapped files because they are written on the hot
/// path, which makes them binary. This is how a person or a script reads them.
/// </remarks>
public sealed class StateCommand
{
    private readonly ConfigPaths _paths;

    public StateCommand(ConfigPaths paths) => _paths = paths;

    public CommandResponse Execute(string line)
    {
        var ledger = new CloakLedger(_paths.CloakLedgerFile);
        var journal = new GeometryJournal(_paths.GeometryJournalFile);
        Session? session = new SessionMarker(_paths.SessionFile).Previous;

        return CommandResponse.Ok(line, new
        {
            hidden = ledger.Entries.Count,
            moved = journal.Entries.Count,
            windows = ledger.Entries
                .Select(e => new { handle = e.Handle, process = e.Process, title = e.Title, at = e.At })
                .ToArray(),
            geometry = journal.Entries
                .Select(e => new
                {
                    handle = e.Handle,
                    process = e.Process,
                    title = e.Title,
                    frame = e.Frame.ToString(),
                    maximized = e.Maximized,
                    minimized = e.Minimized,
                    topmost = e.Topmost,
                })
                .ToArray(),
            lastRun = session is null
                ? null
                : new { session.StartedAt, session.Pid, session.CleanExit, session.UncleanInARow },
        });
    }
}
