using System.Runtime.InteropServices;
using AkuWM.Core.Config;

namespace AkuWM.Core.Import;

/// <summary>
/// Reads the Windows Startup folder, so what the session launches today is
/// written down instead of being whatever the folder happens to hold.
/// </summary>
/// <remarks>
/// From M2 AkuWM launches these itself, in order, with Zebar started only once
/// the IPC server is listening -- the reason the entries carry an
/// <c>after</c> phase.
/// </remarks>
public static class StartupImporter
{
    private const string StartupUnderRoaming =
        "Microsoft/Windows/Start Menu/Programs/Startup";

    /// <summary>What AkuWM takes the place of, and must never launch.</summary>
    private static readonly string[] Replaces = ["glazewm", "hyper-desktops", "autohotkey"];

    public static List<StartupConfig> Import(string? folder, ImportSummary summary)
    {
        var entries = new List<StartupConfig>();

        if (folder is null || !Directory.Exists(folder))
        {
            summary.Notes.Add(
                folder is null
                    ? "the Windows Startup folder could not be located; startup is empty"
                    : $"the Startup folder {folder} is not there; startup is empty");
            return entries;
        }

        long now = ConfigStore.Now();
        foreach (string file in Directory.GetFiles(folder).OrderBy(f => f, StringComparer.Ordinal))
        {
            string extension = Path.GetExtension(file);
            if (!extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase)
                && !extension.Equals(".exe", StringComparison.OrdinalIgnoreCase)
                && !extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase)
                && !extension.Equals(".bat", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string name = Path.GetFileNameWithoutExtension(file);

            // The two programs AkuWM replaces are imported so the list is a
            // true picture of the session, but switched off: if AkuWM ever
            // launches this list itself (M2) it must not start its own
            // predecessor. Turning one back on is one flag in the GUI.
            bool replaced = Replaces.Any(r => name.Contains(r, StringComparison.OrdinalIgnoreCase));

            entries.Add(new StartupConfig
            {
                Id = Ids.New("u"),
                Name = name,
                Command = WindowsPath(file),
                After = name.Contains("zebar", StringComparison.OrdinalIgnoreCase) ? "ipc" : "now",
                DelayMs = 0,
                Enabled = !replaced,
                Notes = replaced
                    ? "found in the Windows Startup folder; off because AkuWM replaces it"
                    : "found in the Windows Startup folder",
                UpdatedAt = now,
            });
        }

        summary.Startup = entries.Count;
        if (entries.Count > 0)
        {
            summary.Notes.Add(
                $"{entries.Count} Startup-folder entries imported; AkuWM will launch them itself " +
                "once the shortcuts are removed (M2)");
        }

        return entries;
    }

    /// <summary>
    /// Where the Startup folder is, from either side of the WSL boundary.
    /// </summary>
    /// <remarks>
    /// The Python prototype built this path from the temp directory's
    /// <c>parents[2]</c>, which dropped the <c>AppData</c> segment and looked
    /// in <c>C:\Users\&lt;user&gt;\Roaming\...</c> -- a folder that does not
    /// exist, so the import always returned nothing while the real one held
    /// four shortcuts.
    /// </remarks>
    public static string? FindStartupFolder(Func<string, string?>? environment = null, string? windowsRoot = null)
    {
        environment ??= Environment.GetEnvironmentVariable;

        // An explicit root wins on every platform. Without that the Windows
        // branch below ignored it and answered with the REAL user's folder, so
        // the test that hands it a temp directory passed on Linux and failed on
        // Windows -- which is what the windows CI job had been failing on.
        // Production passes null and is unaffected.
        if (windowsRoot is null && RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            string folder = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
            return folder.Length > 0 ? folder : null;
        }

        // WSL, or an explicit root: the Windows drive is mounted, but the
        // Windows user name is not the Linux one. An explicit override wins,
        // then the single real user under <root>/Users.
        string? configured = environment("AKUWM_WINDOWS_USER");
        string usersDir = Path.Combine(windowsRoot ?? "/mnt/c", "Users");
        if (!Directory.Exists(usersDir))
        {
            return null;
        }

        IEnumerable<string> candidates = configured is { Length: > 0 }
            ? [Path.Combine(usersDir, configured)]
            : Directory.GetDirectories(usersDir).Where(IsRealUser);

        foreach (string home in candidates)
        {
            string folder = Path.Combine(home, "AppData", "Roaming", StartupUnderRoaming.Replace('/', Path.DirectorySeparatorChar));
            if (Directory.Exists(folder))
            {
                return folder;
            }
        }

        return null;
    }

    private static bool IsRealUser(string directory)
    {
        string name = Path.GetFileName(directory);
        return !name.Equals("Public", StringComparison.OrdinalIgnoreCase)
               && !name.Equals("Default", StringComparison.OrdinalIgnoreCase)
               && !name.Equals("Default User", StringComparison.OrdinalIgnoreCase)
               && !name.Equals("All Users", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A path AkuWM can hand to Windows: <c>/mnt/c/Users/x</c> seen from WSL is
    /// <c>C:\Users\x</c> to the process that will actually run it.
    /// </summary>
    internal static string WindowsPath(string path)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return path;
        }

        const string mnt = "/mnt/";
        if (path.StartsWith(mnt, StringComparison.Ordinal) && path.Length > mnt.Length + 1)
        {
            char drive = char.ToUpperInvariant(path[mnt.Length]);
            string rest = path[(mnt.Length + 1)..].TrimStart('/');
            return $"{drive}:\\{rest.Replace('/', '\\')}";
        }

        return path;
    }
}
