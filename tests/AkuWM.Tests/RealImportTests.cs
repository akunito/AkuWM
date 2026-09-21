using AkuWM.Core.Config;
using AkuWM.Core.Import;
using Xunit;

namespace AkuWM.Tests;

/// <summary>
/// The importer against the configuration this desk actually runs, not a
/// fixture. It is the one file that proves the model matches reality.
/// </summary>
public class RealImportTests
{
    /// <summary>
    /// The live file, or null where there is no dotfiles checkout to read.
    /// </summary>
    /// <remarks>
    /// The path was hardcoded to /home/akunito and asserted to exist, so both
    /// CI jobs failed on a runner that has no such directory -- for weeks, and
    /// invisibly, because a job log needs admin rights to read (2026-09-21).
    /// A fixture copy is not the answer: the whole point is that this file is
    /// the one the desk runs, and a copy goes stale.
    /// </remarks>
    private static string? Live()
    {
        if (Environment.GetEnvironmentVariable("AKUWM_LIVE_CONFIG") is { Length: > 0 } explicitly)
        {
            return File.Exists(explicitly) ? explicitly : null;
        }

        string? home = Environment.GetEnvironmentVariable("HOME")
            ?? Environment.GetEnvironmentVariable("USERPROFILE");

        if (home is null)
        {
            return null;
        }

        string dotfiles = Path.Combine(home, ".dotfiles");
        if (!Directory.Exists(dotfiles))
        {
            return null;
        }

        // A checkout that exists but has lost the file IS a failure: that is
        // the case this test is here to catch.
        string path = Path.Combine(dotfiles, "templates", "windows", "DESK_W11", "glazewm", "config.yaml");
        Assert.True(File.Exists(path), path);
        return path;
    }

    [Fact]
    public void The_live_configuration_of_this_desk_imports_whole()
    {
        if (Live() is not { } live)
        {
            return;
        }

        var summary = new ImportSummary();
        AkuWmConfig config = GlazeWmImporter.Import(File.ReadAllText(live), summary);

        // "keep the position the app asked for" -- the comment in that file,
        // and a setting the importer used to drop on the floor.
        Assert.False(config.Layout!.FloatCentered);

        Assert.Equal(20, config.Workspaces!.Count);
        Assert.Equal("11", config.Workspaces[0].Name);
        Assert.Equal("30", config.Workspaces[^1].Name);

        // Each screen owns a run of ten.
        Assert.Equal(10, config.Workspaces.Count(w => w.Monitor == config.Workspaces[0].Monitor));
    }
}
