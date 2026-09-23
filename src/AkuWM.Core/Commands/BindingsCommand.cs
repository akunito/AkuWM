using AkuWM.Core.Bindings;
using AkuWM.Core.Config;
using AkuWM.Core.Ipc;

namespace AkuWM.Core.Commands;

/// <summary>
/// <c>bindings validate | render | reload | poke | path</c>: the shortcuts
/// of the configuration as the AutoHotkey script binds them (Bindings).
/// </summary>
public sealed class BindingsCommand
{
    private readonly ConfigPaths _paths;
    private readonly Func<bool>? _poke;

    /// <param name="poke">Posts the reload message to the script's window; null where there is no such window (not Windows).</param>
    public BindingsCommand(ConfigPaths paths, Func<bool>? poke)
    {
        _paths = paths;
        _poke = poke;
    }

    public CommandResponse Execute(string line, string[] tokens)
    {
        string verb = tokens.Length > 1 ? tokens[1].ToLowerInvariant() : "validate";
        switch (verb)
        {
            case "path":
                return CommandResponse.Ok(line, new { file = _paths.BindingsFile });

            case "validate":
                {
                    (string text, List<string> problems, int count) = RenderNow();
                    return CommandResponse.Ok(line, new { file = _paths.BindingsFile, bindings = count, problems, ok = problems.Count == 0 });
                }

            case "render":
            case "reload":
                {
                    (string text, List<string> problems, int count) = RenderNow();
                    Bindings.Bindings.Write(_paths.BindingsFile, text);
                    bool? poked = verb == "reload" ? Poke() : null;
                    return CommandResponse.Ok(line, new { file = _paths.BindingsFile, bindings = count, problems, poked });
                }

            case "poke":
                return CommandResponse.Ok(line, new { poked = Poke() });

            default:
                return CommandResponse.Fail(line, "bindings needs a verb: validate, render, reload, poke, path");
        }
    }

    private bool Poke() => _poke?.Invoke() ?? false;

    private (string Text, List<string> Problems, int Count) RenderNow()
    {
        LoadedConfig loaded = ConfigStore.Load(_paths);
        var problems = new List<string>();
        string text = Bindings.Bindings.Render(loaded.Effective.Shortcuts, problems);
        int count = 0;
        foreach (char c in text)
        {
            if (c == '\n')
            {
                count++;
            }
        }

        return (text, problems, count - 1); // the comment line
    }
}
