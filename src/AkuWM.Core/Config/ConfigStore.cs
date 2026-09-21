using System.Text;

namespace AkuWM.Core.Config;

/// <summary>What was found on disk, and the configuration that comes out of it.</summary>
public sealed class LoadedConfig
{
    public required ConfigPaths Paths { get; init; }

    /// <summary>The shared layer as written, or null when the file is not there.</summary>
    public AkuWmConfig? Common { get; init; }

    /// <summary>This machine's layer as written, or null when the file is not there.</summary>
    public AkuWmConfig? Profile { get; init; }

    /// <summary>Defaults + common + machine, which is what AkuWM runs on.</summary>
    public required AkuWmConfig Effective { get; init; }

    public required ValidationResult Validation { get; init; }

    public bool CommonExists => Common is not null;

    public bool ProfileExists => Profile is not null;
}

/// <summary>
/// Reads and writes the configuration layers.
/// </summary>
/// <remarks>
/// Writes are atomic (a temporary file in the same directory, then a rename)
/// and keep the order of the lists, so a diff shows the change and nothing
/// else -- the same discipline <c>sway-apps</c> has on the other desk.
/// </remarks>
public static class ConfigStore
{
    public static LoadedConfig Load(ConfigPaths paths)
    {
        AkuWmConfig? common = ReadIfPresent(paths.CommonFile);
        AkuWmConfig? profile = ReadIfPresent(paths.ProfileFile);

        AkuWmConfig effective = ConfigMerge.MergeAll(
            ConfigDefaults.Create(),
            common ?? new AkuWmConfig(),
            profile ?? new AkuWmConfig());

        return new LoadedConfig
        {
            Paths = paths,
            Common = common,
            Profile = profile,
            Effective = effective,
            Validation = ConfigValidator.Validate(effective),
        };
    }

    public static AkuWmConfig? ReadIfPresent(string file)
    {
        if (!File.Exists(file))
        {
            return null;
        }

        try
        {
            return ConfigJson.Read(File.ReadAllText(file, Encoding.UTF8));
        }
        catch (Exception ex) when (ex is not ConfigException)
        {
            throw new ConfigException($"{file}: {ex.Message}", ex);
        }
        catch (ConfigException ex)
        {
            throw new ConfigException($"{file}: {ex.Message}", ex);
        }
    }

    /// <summary>Writes one layer, atomically, creating its directory.</summary>
    /// <summary>
    /// Writes text that is already a configuration layer, atomically.
    /// </summary>
    /// <remarks>
    /// What <c>config set</c> uses: it edits the JSON rather than the typed
    /// model, so that a key this build has never heard of survives the trip
    /// instead of being dropped on read and never written back.
    /// </remarks>
    public static void SaveText(string file, string json)
    {
        string directory = Path.GetDirectoryName(Path.GetFullPath(file))
                           ?? throw new ConfigException($"{file} has no directory");
        Directory.CreateDirectory(directory);

        string temporary = Path.Combine(directory, $".{Path.GetFileName(file)}.{Environment.ProcessId}.tmp");
        File.WriteAllText(temporary, json + Environment.NewLine, new UTF8Encoding(false));
        File.Move(temporary, file, overwrite: true);
    }

    public static void Save(string file, AkuWmConfig layer)
    {
        string directory = Path.GetDirectoryName(Path.GetFullPath(file))
                           ?? throw new ConfigException($"{file} has no directory");
        Directory.CreateDirectory(directory);

        string temporary = Path.Combine(directory, $".{Path.GetFileName(file)}.{Environment.ProcessId}.tmp");
        File.WriteAllText(temporary, ConfigJson.Write(layer) + Environment.NewLine, new UTF8Encoding(false));

        // File.Move over an existing file is atomic on both NTFS and ext4, so a
        // reader never sees a half-written configuration.
        File.Move(temporary, file, overwrite: true);
    }

    /// <summary>Seconds since the epoch, the stamp every list item carries.</summary>
    public static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
}
