using AkuWM.Core.Commands;
using Xunit;

namespace AkuWM.Tests;

/// <summary>
/// The release path is four files that never meet at build time: the tag
/// workflow, the bootstrap script, the installer and the staging script. A
/// name drifting in one of them shows up as a machine with three of the four
/// exes, so this is where they are held to the same names.
/// </summary>
public class ReleasePlumbingTests
{
    private static readonly string Root = FindRoot();

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AkuWM.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("AkuWM.sln not found above " + AppContext.BaseDirectory);
    }

    private static string Text(string relative) => File.ReadAllText(Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar)));

    [Fact]
    public void The_version_the_binaries_report_is_the_one_in_the_props()
    {
        string props = Text("Directory.Build.props");
        Assert.Contains($"<Version>{Build.Version}</Version>", props);
    }

    [Fact]
    public void The_tag_workflow_ships_every_exe_and_the_installer()
    {
        string wf = Text(".github/workflows/release.yml");
        Assert.Contains("tags: ['v*']", wf);
        foreach (string project in new[] { "src/AkuWM.App", "src/AkuWM.Cli", "src/AkuWM.Shim", "src/AkuWM.Gui" })
        {
            Assert.Contains($"dotnet publish {project} ", wf);
        }

        Assert.Contains("stage/app/akuwm.exe stage/cli/akuwm-cli.exe stage/shim/glazewm.exe stage/gui/akuwm-gui.exe release/", wf);
        Assert.Contains("cp tools/uia-install.ps1 release/", wf);
        Assert.Contains("uiAccess=\"true\"' release/akuwm.exe", wf);
        Assert.Contains("akuwm-${GITHUB_REF_NAME}-win-x64.zip", wf);
        Assert.Contains("dotnet test tests/AkuWM.Gui.Tests/AkuWM.Gui.Tests.csproj", wf);
    }

    [Fact]
    public void Bootstrap_fetches_the_zip_the_workflow_names_and_runs_the_installer_it_contains()
    {
        string boot = Text("tools/bootstrap.ps1");
        Assert.Contains("'akuwm-*-win-x64.zip'", boot);
        Assert.Contains("Join-Path $Into 'uia-install.ps1'", boot);
        Assert.Contains("Temp\\akuwm-uia", boot);
        Assert.Contains("releases/latest", boot);
        Assert.Contains("releases/tags/$Tag", boot);
    }

    [Fact]
    public void The_installer_and_the_staging_script_know_all_four_exes()
    {
        string installer = Text("tools/install-uiaccess.ps1");
        Assert.Contains("'akuwm-cli.exe'", installer);
        Assert.Contains("'akuwm-gui.exe'", installer);
        // The side files are copied with patience and never fatally: a
        // client running at the wrong moment ended the install before the
        // restart (2026-10-01 11:51), and the staging script restarts the
        // daemon in a finally.
        Assert.Contains("function CopyWithPatience", installer);
        Assert.Contains("CopyWithPatience $Cli", installer);
        Assert.Contains("CopyWithPatience $Gui", installer);
        string uiaInstall = Text("tools/uia-install.ps1");
        Assert.Contains("} finally {", uiaInstall);
        Assert.Contains("Startup\\AkuWM.lnk", uiaInstall);
        Assert.Contains("Programs\\AkuWM", installer);
        string staging = Text("tools/publish-uia.sh");
        Assert.Contains("publish src/AkuWM.Gui akuwm-gui.exe false", staging);
        Assert.Contains("publish src/AkuWM.Cli akuwm-cli.exe false", staging);
        Assert.Contains("cp \"$root/tools/uia-install.ps1\" \"$out/uia-install.ps1\"", staging);
        string ci = Text(".github/workflows/ci.yml");
        Assert.Contains("tests/AkuWM.Gui.Tests/AkuWM.Gui.Tests.csproj", ci);
        Assert.Contains("publish-gui/akuwm-gui.exe", ci);
    }
}
