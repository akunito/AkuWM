using System.Runtime.InteropServices;

namespace AkuWM.Core.Config;

/// <summary>
/// Where the configuration and the runtime state live, from either side of the
/// WSL boundary.
/// </summary>
/// <remarks>
/// The dotfiles repo is the same tree seen twice -- <c>/home/&lt;user&gt;/.dotfiles</c>
/// from WSL, <c>C:\Users\&lt;user&gt;\.dotfiles</c> from Windows -- so paths are
/// resolved from where the process is actually running. <c>AKUWM_STATE_DIR</c>
/// overrides the lot, which is how the tests and a second checkout stay out of
/// the real configuration.
/// </remarks>
public sealed class ConfigPaths
{
    public const string StateDirVariable = "AKUWM_STATE_DIR";
    public const string ProfileVariable = "AKUWM_PROFILE";
    public const string DefaultProfile = "DESK_W11";

    /// <summary>Relative to the repository root, written with the separator of the platform in use.</summary>
    public static readonly string ConfigDirInRepo =
        Path.Combine("templates", "windows", "DESK_W11", "akuwm");

    public ConfigPaths(string configDir, string profile, string runtimeDir)
    {
        ConfigDir = configDir;
        Profile = profile;
        RuntimeDir = runtimeDir;
    }

    /// <summary>Holds <c>common.json</c> and <c>&lt;profile&gt;.json</c>; lives in the repo, git versions it.</summary>
    public string ConfigDir { get; }

    /// <summary>The machine layer's name, from <c>AKUWM_PROFILE</c> or <c>ENV_PROFILE</c>.</summary>
    public string Profile { get; }

    /// <summary>Logs and the journal: per machine, changes every minute, never committed.</summary>
    public string RuntimeDir { get; }

    public string CommonFile => Path.Combine(ConfigDir, "common.json");

    public string ProfileFile => Path.Combine(ConfigDir, Profile + ".json");

    public string LogDir => Path.Combine(RuntimeDir, "logs");

    public string JournalFile => Path.Combine(RuntimeDir, "journal.json");

    /// <summary>
    /// The windows AkuWM has hidden, written before each cloak so a crash
    /// cannot take them with it.
    /// </summary>
    public string CloakLedgerFile => Path.Combine(RuntimeDir, "cloaked.bin");

    /// <summary>
    /// Where each window was before AkuWM moved it, written before the first
    /// move so the desk can be put back by a process that is not this one.
    /// </summary>
    public string GeometryJournalFile => Path.Combine(RuntimeDir, "geometry.bin");

    /// <summary>Where each window is, in AkuWM's terms, for the next start.</summary>
    public string PlacementsFile => Path.Combine(RuntimeDir, "placements.bin");

    /// <summary>How the last run ended. Two bad endings in a row means safe mode.</summary>
    public string SessionFile => Path.Combine(RuntimeDir, "session.json");

    /// <summary>The marker file that turns debug logging on without a restart.</summary>
    public string DebugMarkerFile => Path.Combine(RuntimeDir, "debug");

    public static ConfigPaths Discover(Func<string, string?>? environment = null)
    {
        environment ??= Environment.GetEnvironmentVariable;

        string profile =
            FirstSet(environment(ProfileVariable), environment("ENV_PROFILE")) ?? DefaultProfile;

        string? configured = FirstSet(environment(StateDirVariable));
        string configDir = configured ?? Path.Combine(FindRepoRoot(environment), ConfigDirInRepo);

        return new ConfigPaths(configDir, profile, FindRuntimeDir(environment));
    }

    /// <summary>
    /// The dotfiles checkout: the one this executable sits in if it does, else
    /// the home directory's.
    /// </summary>
    private static string FindRepoRoot(Func<string, string?> environment)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, ConfigDirInRepo)))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        string home = FirstSet(environment("HOME"), environment("USERPROFILE"))
                      ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".dotfiles");
    }

    private static string FindRuntimeDir(Func<string, string?> environment)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            string local = FirstSet(environment("LOCALAPPDATA"))
                           ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(local, "akuwm");
        }

        string? xdg = FirstSet(environment("XDG_STATE_HOME"));
        if (xdg is not null)
        {
            return Path.Combine(xdg, "akuwm");
        }

        string home = FirstSet(environment("HOME"))
                      ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".local", "state", "akuwm");
    }

    private static string? FirstSet(params string?[] candidates) =>
        candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c));
}
