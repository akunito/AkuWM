using System.Text.Json.Nodes;
using AkuWM.Core.Model;

namespace AkuWM.Core.Commands;

/// <summary>One window as the window manager in charge reports it.</summary>
/// <param name="Handle">The hwnd, which is what the two views are joined on.</param>
public readonly record struct ExternalWindow(
    long Handle,
    string ProcessName,
    string ClassName,
    string Title,
    string State,
    bool Sticky,
    long MonitorHandle,
    string Workspace);

/// <param name="Handle">The window both sides are talking about.</param>
/// <param name="Field">What they disagree on.</param>
/// <param name="Mine">What AkuWM says.</param>
/// <param name="Theirs">What the manager in charge says.</param>
public readonly record struct Disagreement(long Handle, string What, string Field, string Mine, string Theirs);

/// <summary>The outcome of one comparison.</summary>
public sealed record ShadowDiffResult
{
    public required int Mine { get; init; }
    public required int Theirs { get; init; }
    public required IReadOnlyList<string> OnlyMine { get; init; }
    public required IReadOnlyList<string> OnlyTheirs { get; init; }
    public required IReadOnlyList<Disagreement> Disagreements { get; init; }

    public bool Agrees => OnlyMine.Count == 0 && OnlyTheirs.Count == 0 && Disagreements.Count == 0;
}

/// <summary>
/// Compares AkuWM's view of the desk with the view of whatever is managing it
/// today.
/// </summary>
/// <remarks>
/// <para>
/// This is how M1 is judged: with GlazeWM still in charge, AkuWM watches the
/// same desktop and the two lists are put side by side, window by window. A
/// disagreement is a bug in AkuWM's filter, its rules or its classifier, found
/// before anything is handed over.
/// </para>
/// <para>
/// Workspace names are deliberately <strong>not</strong> compared. Which
/// workspace a hidden window sits on is a fact only the manager in charge
/// knows -- from outside, every hidden window looks the same, cloaked -- so
/// that column becomes comparable at M2, when AkuWM is the one assigning them.
/// The monitor is compared instead, which is observable from both sides.
/// </para>
/// </remarks>
public static class ShadowDiff
{
    public static ShadowDiffResult Compare(ShadowView view, IReadOnlyList<ExternalWindow> theirs)
    {
        Dictionary<long, ManagedWindow> mine = view.Managed
            .GroupBy(w => w.Window.Handle.Value)
            .ToDictionary(g => g.Key, g => g.First());
        Dictionary<long, ExternalWindow> other = theirs
            .GroupBy(w => w.Handle)
            .ToDictionary(g => g.Key, g => g.First());

        var onlyMine = new List<string>();
        var onlyTheirs = new List<string>();
        var disagreements = new List<Disagreement>();

        foreach ((long handle, ManagedWindow window) in mine)
        {
            if (!other.ContainsKey(handle))
            {
                onlyMine.Add(Describe(window));
            }
        }

        foreach ((long handle, ExternalWindow window) in other)
        {
            if (!mine.ContainsKey(handle))
            {
                onlyTheirs.Add($"0x{handle:x} {window.ProcessName} [{window.ClassName}] \"{window.Title}\"");
            }
        }

        foreach ((long handle, ManagedWindow window) in mine)
        {
            if (!other.TryGetValue(handle, out ExternalWindow theirWindow))
            {
                continue;
            }

            string what = $"{window.Window.ProcessName} \"{Shorten(window.Window.Title)}\"";

            string myState = window.State.ToString().ToLowerInvariant();
            if (!string.Equals(myState, theirWindow.State, StringComparison.OrdinalIgnoreCase))
            {
                disagreements.Add(new Disagreement(handle, what, "state", myState, theirWindow.State));
            }

            if (window.Sticky != theirWindow.Sticky)
            {
                disagreements.Add(new Disagreement(
                    handle, what, "sticky", window.Sticky.ToString(), theirWindow.Sticky.ToString()));
            }

            if (theirWindow.MonitorHandle != 0 && window.Window.Monitor.Value != theirWindow.MonitorHandle)
            {
                disagreements.Add(new Disagreement(
                    handle, what, "monitor",
                    $"0x{window.Window.Monitor.Value:x}", $"0x{theirWindow.MonitorHandle:x}"));
            }
        }

        return new ShadowDiffResult
        {
            Mine = mine.Count,
            Theirs = other.Count,
            OnlyMine = onlyMine,
            OnlyTheirs = onlyTheirs,
            Disagreements = disagreements,
        };
    }

    private static string Describe(ManagedWindow window) =>
        $"{window.Window.Handle} {window.Window.ProcessName} [{window.Window.ClassName}] " +
        $"\"{Shorten(window.Window.Title)}\" {window.State.ToString().ToLowerInvariant()}";

    private static string Shorten(string title) =>
        title.Length <= 40 ? title : title[..37] + "...";

    /// <summary>
    /// Reads the window list out of a GlazeWM <c>query windows</c> reply.
    /// </summary>
    /// <remarks>
    /// Parsed from the shape captured black-box in the plan, section 7, and
    /// tested against a recorded reply. Nothing here needs the other program's
    /// source, only its output.
    /// </remarks>
    public static List<ExternalWindow> ParseGlazeWindows(string json)
    {
        var windows = new List<ExternalWindow>();
        JsonNode? root = JsonNode.Parse(json);
        JsonArray? list = root?["data"]?["windows"]?.AsArray();
        if (list is null)
        {
            return windows;
        }

        foreach (JsonNode? node in list)
        {
            if (node is null)
            {
                continue;
            }

            windows.Add(new ExternalWindow(
                Handle: node["handle"]?.GetValue<long>() ?? 0,
                ProcessName: node["processName"]?.GetValue<string>() ?? string.Empty,
                ClassName: node["className"]?.GetValue<string>() ?? string.Empty,
                Title: node["title"]?.GetValue<string>() ?? string.Empty,
                State: node["state"]?["type"]?.GetValue<string>() ?? string.Empty,
                Sticky: node["sticky"]?.GetValue<bool>() ?? false,
                MonitorHandle: 0,
                Workspace: node["parentId"]?.GetValue<string>() ?? string.Empty));
        }

        return windows;
    }

    /// <summary>
    /// GlazeWM reports a window's parent by id, so the monitor each one is on
    /// comes from walking its <c>query monitors</c> reply: monitor → workspace
    /// → window.
    /// </summary>
    public static Dictionary<string, (string Workspace, string Monitor)> ParseGlazeWorkspaces(string monitorsJson)
    {
        var map = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
        JsonArray? monitors = JsonNode.Parse(monitorsJson)?["data"]?["monitors"]?.AsArray();
        if (monitors is null)
        {
            return map;
        }

        foreach (JsonNode? monitor in monitors)
        {
            string device = monitor?["deviceName"]?.GetValue<string>() ?? string.Empty;
            foreach (JsonNode? workspace in monitor?["children"]?.AsArray() ?? [])
            {
                string id = workspace?["id"]?.GetValue<string>() ?? string.Empty;
                string name = workspace?["name"]?.GetValue<string>() ?? string.Empty;
                if (id.Length > 0)
                {
                    map[id] = (name, device);
                }
            }
        }

        return map;
    }

    /// <summary>Renders a comparison the way the command prints it.</summary>
    public static string Format(ShadowDiffResult result)
    {
        var text = new System.Text.StringBuilder();
        text.AppendLine(
            System.Globalization.CultureInfo.InvariantCulture,
            $"AkuWM manages {result.Mine}, the manager in charge manages {result.Theirs}");

        foreach (string window in result.OnlyMine)
        {
            text.AppendLine("  + only AkuWM would manage: " + window);
        }

        foreach (string window in result.OnlyTheirs)
        {
            text.AppendLine("  - AkuWM would leave alone:  " + window);
        }

        foreach (Disagreement d in result.Disagreements)
        {
            text.AppendLine(
                System.Globalization.CultureInfo.InvariantCulture,
                $"  ! {d.What}: {d.Field} mine={d.Mine} theirs={d.Theirs}");
        }

        text.AppendLine(result.Agrees ? "  they agree" : "  they do not agree yet");
        return text.ToString();
    }
}
