using System.Text;
using AkuWM.Core.Config;
using AkuWM.Core.Import;
using AkuWM.Core.Ipc;
using AkuWM.Core.Logging;

namespace AkuWM.App.Commands;

/// <summary>
/// <c>akuwm config ...</c>: show it, check it, and the one-time import of the
/// GlazeWM stack this desk runs today.
/// </summary>
public sealed class ConfigCommands
{
    private readonly ConfigPaths _paths;

    public ConfigCommands(ConfigPaths paths) => _paths = paths;

    public CommandResponse Execute(string line, string[] tokens)
    {
        if (tokens.Length < 2)
        {
            return CommandResponse.Fail(line, "config needs a verb: show, path, validate, import");
        }

        Dictionary<string, string?> options = CommandLine.Options(tokens, 2);

        return tokens[1].ToLowerInvariant() switch
        {
            "path" => Path(line),
            "show" => Show(line, options),
            "validate" => Validate(line),
            "import" => Import(line, tokens, options),
            _ => CommandResponse.Fail(line, $"'{tokens[1]}' is not show, path, validate or import"),
        };
    }

    private CommandResponse Path(string line) => CommandResponse.Ok(line, new
    {
        configDir = _paths.ConfigDir,
        profile = _paths.Profile,
        common = _paths.CommonFile,
        profileFile = _paths.ProfileFile,
        runtimeDir = _paths.RuntimeDir,
        logDir = _paths.LogDir,
        commonExists = File.Exists(_paths.CommonFile),
        profileFileExists = File.Exists(_paths.ProfileFile),
    });

    private CommandResponse Show(string line, Dictionary<string, string?> options)
    {
        LoadedConfig loaded = ConfigStore.Load(_paths);
        string layer = options.TryGetValue("layer", out string? value) && value is { Length: > 0 }
            ? value
            : "effective";

        AkuWmConfig? shown = layer.ToLowerInvariant() switch
        {
            "effective" => loaded.Effective,
            "common" => loaded.Common,
            "defaults" => ConfigDefaults.Create(),
            _ when string.Equals(layer, _paths.Profile, StringComparison.OrdinalIgnoreCase) => loaded.Profile,
            "profile" => loaded.Profile,
            _ => null,
        };

        if (shown is null)
        {
            return CommandResponse.Fail(
                line, $"there is no '{layer}' layer (try effective, common, {_paths.Profile}, defaults)");
        }

        return CommandResponse.Ok(line, System.Text.Json.Nodes.JsonNode.Parse(ConfigJson.Write(shown)));
    }

    private CommandResponse Validate(string line)
    {
        LoadedConfig loaded;
        try
        {
            loaded = ConfigStore.Load(_paths);
        }
        catch (ConfigException ex)
        {
            return CommandResponse.Fail(line, ex.Message);
        }

        var issues = loaded.Validation.Issues
            .Select(i => new { severity = i.Severity.ToString().ToLowerInvariant(), path = i.Path, message = i.Message })
            .ToList();

        return CommandResponse.Ok(line, new
        {
            ok = loaded.Validation.Ok,
            common = loaded.CommonExists ? _paths.CommonFile : null,
            profile = loaded.ProfileExists ? _paths.ProfileFile : null,
            rules = loaded.Effective.Rules?.Count ?? 0,
            workspaces = loaded.Effective.Workspaces?.Count ?? 0,
            shortcuts = loaded.Effective.Shortcuts?.Count ?? 0,
            errors = issues.Count(i => i.severity == "error"),
            warnings = issues.Count(i => i.severity == "warning"),
            issues,
        });
    }

    /// <summary>
    /// <c>config import glazewm</c>: the GlazeWM YAML, the AutoHotkey toggle
    /// table and the Startup folder become the first <c>common.json</c>.
    /// </summary>
    private CommandResponse Import(string line, string[] tokens, Dictionary<string, string?> options)
    {
        if (tokens.Length < 3 || !tokens[2].Equals("glazewm", StringComparison.OrdinalIgnoreCase))
        {
            return CommandResponse.Fail(line, "the only import AkuWM has is 'config import glazewm'");
        }

        string? yamlPath = options.GetValueOrDefault("from") ?? FindSource("glazewm/config.yaml");
        string? ahkPath = options.GetValueOrDefault("ahk") ?? FindSource("hyper-desktops.ahk");
        bool dryRun = options.ContainsKey("dry-run");
        bool force = options.ContainsKey("force");

        if (yamlPath is null || !File.Exists(yamlPath))
        {
            return CommandResponse.Fail(
                line, $"the GlazeWM config was not found ({yamlPath ?? "no candidate"}); pass --from <config.yaml>");
        }

        var summary = new ImportSummary();
        AkuWmConfig imported = GlazeWmImporter.Import(File.ReadAllText(yamlPath, Encoding.UTF8), summary);

        if (ahkPath is not null && File.Exists(ahkPath))
        {
            imported.Shortcuts = AhkImporter.ImportToggles(ReadTextFile(ahkPath), summary);
        }
        else
        {
            summary.Notes.Add(
                $"the AutoHotkey file was not found ({ahkPath ?? "no candidate"}); no shortcuts imported");
        }

        string? startupDir = options.GetValueOrDefault("startup-dir") ?? StartupImporter.FindStartupFolder();
        imported.Startup = StartupImporter.Import(startupDir, summary);

        imported.Apps = new AppsConfig { Catalogue = "../winget-packages.json" };

        ValidationResult validation = ConfigValidator.Validate(
            ConfigMerge.Merge(ConfigDefaults.Create(), imported));

        bool exists = File.Exists(_paths.CommonFile);
        bool written = false;

        if (!dryRun)
        {
            if (exists && !force)
            {
                return CommandResponse.Fail(
                    line,
                    $"{_paths.CommonFile} is already there; pass --force to overwrite it or --dry-run to see what the import makes");
            }

            if (!validation.Ok)
            {
                return CommandResponse.Fail(
                    line,
                    "the imported configuration does not validate, so nothing was written: " +
                    string.Join("; ", validation.Errors.Select(e => e.ToString())));
            }

            ConfigStore.Save(_paths.CommonFile, imported);
            written = true;
            Log.Info($"imported {summary} into {_paths.CommonFile}");

            if (!File.Exists(_paths.ProfileFile))
            {
                // The machine layer starts empty on purpose: everything imported
                // is shared, and what is specific to this desk (the monitor
                // EDIDs) is written here when it is known.
                ConfigStore.Save(_paths.ProfileFile, new AkuWmConfig { Version = ConfigDefaults.SchemaVersion });
                summary.Notes.Add($"created an empty machine layer at {_paths.ProfileFile}");
            }
        }

        return CommandResponse.Ok(line, new
        {
            dryRun,
            written,
            target = _paths.CommonFile,
            source = yamlPath,
            ahk = ahkPath,
            startupDir,
            counts = new
            {
                rules = summary.Rules,
                workspaces = summary.Workspaces,
                shortcuts = summary.Shortcuts,
                monitors = summary.Monitors,
                startup = summary.Startup,
            },
            valid = validation.Ok,
            errors = validation.Errors.Select(e => e.ToString()).ToList(),
            warnings = validation.Warnings.Select(e => e.ToString()).ToList(),
            notes = summary.Notes,
            preview = dryRun ? System.Text.Json.Nodes.JsonNode.Parse(ConfigJson.Write(imported)) : null,
        });
    }

    /// <summary>A file of the DESK_W11 template directory, next to the config dir.</summary>
    private string? FindSource(string relative)
    {
        // templates/windows/DESK_W11/akuwm -> templates/windows/DESK_W11
        string? deskDir = System.IO.Path.GetDirectoryName(_paths.ConfigDir.TrimEnd(
            System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));
        if (deskDir is null)
        {
            return null;
        }

        string candidate = System.IO.Path.Combine(deskDir, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));
        return File.Exists(candidate) ? candidate : null;
    }

    /// <summary>The AutoHotkey files are UTF-8 with a byte-order mark.</summary>
    private static string ReadTextFile(string path)
    {
        using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }
}
