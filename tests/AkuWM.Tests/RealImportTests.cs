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
    private const string Live =
        "/home/akunito/.dotfiles/templates/windows/DESK_W11/glazewm/config.yaml";

    [Fact]
    public void The_live_configuration_of_this_desk_imports_whole()
    {
        Assert.True(File.Exists(Live), Live);

        var summary = new ImportSummary();
        AkuWmConfig config = GlazeWmImporter.Import(File.ReadAllText(Live), summary);

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
