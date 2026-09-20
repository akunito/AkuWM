using AkuWM.Core;
using AkuWM.Core.Config;
using AkuWM.Core.Import;
using Xunit;

namespace AkuWM.Tests;

/// <summary>
/// The import is the proof that nothing was designed twice: the desk that
/// works today, read back as AkuWM configuration. The counts are the plan's
/// exit criterion for M0 -- 21 rules, 20 workspaces, 13 apps.
/// </summary>
public class ImporterTests : IDisposable
{
    public ImporterTests() => Ids.Generator = Ids.Sequential();

    public void Dispose() => Ids.Generator = prefix => Ids.Mint(prefix, Random.Shared);

    private static AkuWmConfig Import(out ImportSummary summary)
    {
        summary = new ImportSummary();
        return GlazeWmImporter.Import(Fixture.Read(Fixture.GlazeConfig), summary);
    }

    [Fact]
    public void TheDeskAsItIsTodayComesAcrossWhole()
    {
        AkuWmConfig config = Import(out ImportSummary summary);

        Assert.Equal(21, summary.Rules);
        Assert.Equal(20, summary.Workspaces);
        Assert.Equal(21, config.Rules!.Count);
        Assert.Equal(20, config.Workspaces!.Count);
    }

    [Fact]
    public void WorkspacesAreBoundToRolesInsteadOfMonitorIndexes()
    {
        AkuWmConfig config = Import(out _);

        Assert.Equal(10, config.Workspaces!.Count(w => w.Monitor == "main"));
        Assert.Equal(10, config.Workspaces!.Count(w => w.Monitor == "second"));
        Assert.Equal("11", config.Workspaces![0].Name);
        Assert.Equal("20", config.Workspaces![^1].Name);
    }

    [Fact]
    public void AMonitorRoleIsDeclaredForEveryRoleTheWorkspacesUse()
    {
        AkuWmConfig config = Import(out _);

        Assert.Equal(["main", "second"], config.Monitors!.Select(m => m.Id));
        Assert.True(config.Monitors![0].Primary);
        // The identity is not in the YAML; it is filled in on the desk.
        Assert.All(config.Monitors!, m => Assert.Null(m.Match));
    }

    [Fact]
    public void GlazeWmCommandsBecomeAkuWmActions()
    {
        AkuWmConfig config = Import(out _);

        RuleConfig telegram = config.Rules!.Single(r => r.Name == "Telegram");
        Assert.Equal(["float", "sticky"], telegram.Actions);

        RuleConfig zebar = config.Rules!.Single(r => r.Name == "zebar");
        Assert.Equal(["ignore"], zebar.Actions);
    }

    [Fact]
    public void EveryAlternativeBecomesARuleOfItsOwn()
    {
        AkuWmConfig config = Import(out _);

        // GlazeWM wrote three blocks; each alternative is a rule here, so one
        // of them can be switched off without touching the others.
        Assert.Equal(7, config.Rules!.Count(r => r.Actions!.Contains("ignore")));
        Assert.Equal(12, config.Rules!.Count(r => r.Actions!.SequenceEqual(["float", "sticky"])));
        Assert.Equal(2, config.Rules!.Count(r => r.Actions!.SequenceEqual(["float"])));
    }

    [Fact]
    public void TwoCriteriaOnOneAlternativeStayTogether()
    {
        AkuWmConfig config = Import(out _);

        MatchCriteria calculator = config.Rules!.Single(r => r.Name == "Calculator").Match!.Single();
        Assert.Equal("ApplicationFrameWindow", calculator.Class);
        Assert.Equal("Calculator", calculator.Title);
    }

    [Fact]
    public void RegexCriteriaKeepTheirMarker()
    {
        AkuWmConfig config = Import(out _);

        MatchCriteria pip = config.Rules!
            .Single(r => r.Name!.Contains("icture", StringComparison.Ordinal))
            .Match!.Single();

        Assert.StartsWith("re:", pip.Title, StringComparison.Ordinal);
        Assert.StartsWith("re:", pip.Class, StringComparison.Ordinal);
    }

    [Fact]
    public void TheLookAndTheGapsComeAcross()
    {
        AkuWmConfig config = Import(out _);

        Assert.Equal(8, config.Gaps!.Inner);
        Assert.Equal([0, 0, 0, 0], config.Gaps!.Outer!);
        Assert.True(config.Gaps.ScaleWithDpi);
        Assert.Equal("#c4a7e7", config.Effects!.FocusedBorder);
        // The other windows had a colour but the border was off: nothing to keep.
        Assert.Null(config.Effects.OtherBorder);
    }

    [Fact]
    public void FocusFollowsTheMouseEvenThoughGlazeWmHadItOff()
    {
        AkuWmConfig config = Import(out ImportSummary summary);

        // The YAML says false because GlazeWM's own implementation did not work
        // on this desk; AkuWM uses the native tracking, which does, and the
        // import says so instead of carrying the false across.
        Assert.True(config.General!.FocusFollowsMouse);
        Assert.Contains(summary.Notes, n => n.Contains("native tracking", StringComparison.Ordinal));
    }

    [Fact]
    public void TheVirtualDesktopFoldIsRecognisedInTheStartupCommands()
    {
        AkuWmConfig config = Import(out _);

        Assert.True(config.General!.StartupFoldVirtualDesktops);
    }

    [Fact]
    public void WhatIsImportedValidates()
    {
        AkuWmConfig config = Import(out _);

        ValidationResult result = ConfigValidator.Validate(ConfigMerge.Merge(ConfigDefaults.Create(), config));

        Assert.True(result.Ok, string.Join("; ", result.Errors.Select(e => e.ToString())));
    }

    [Theory]
    [InlineData("'8px'", 8)]
    [InlineData("12px", 12)]
    [InlineData("0", 0)]
    public void PixelValuesAreRead(string value, int expected) =>
        Assert.Equal(expected, GlazeWmImporter.Pixels(value.Trim('\'')));
}

public class AhkImporterTests : IDisposable
{
    public AhkImporterTests() => Ids.Generator = Ids.Sequential();

    public void Dispose() => Ids.Generator = prefix => Ids.Mint(prefix, Random.Shared);

    private static List<ShortcutConfig> Toggles(out ImportSummary summary)
    {
        summary = new ImportSummary();
        return AhkImporter.ImportToggles(Fixture.Read(Fixture.AhkToggles), summary);
    }

    [Fact]
    public void TheThirteenAppsComeAcrossAndNothingElseDoes()
    {
        List<ShortcutConfig> shortcuts = Toggles(out ImportSummary summary);

        Assert.Equal(13, shortcuts.Count);
        Assert.Equal(13, summary.Shortcuts);
        Assert.All(shortcuts, s => Assert.Equal("app", s.Kind));
        Assert.All(shortcuts, s => Assert.Equal("Apps", s.Category));
    }

    [Fact]
    public void AChordKeepsItsLetterAndItsShift()
    {
        List<ShortcutConfig> shortcuts = Toggles(out _);

        Assert.Equal("Hyper+L", shortcuts.Single(s => s.App == "Telegram.exe").Keys);
        Assert.Equal("Hyper+T", shortcuts.Single(s => s.App == "WindowsTerminal.exe").Keys);
    }

    [Theory]
    [InlineData("""A_ProgramFiles "\Alacritty\alacritty.exe" """, @"%ProgramFiles%\Alacritty\alacritty.exe")]
    [InlineData("""EnvGet("LOCALAPPDATA") "\Vivaldi\Application\vivaldi.exe" """, @"%LOCALAPPDATA%\Vivaldi\Application\vivaldi.exe")]
    [InlineData("""A_AppData "\Telegram Desktop\Telegram.exe" """, @"%APPDATA%\Telegram Desktop\Telegram.exe")]
    [InlineData("\"wt.exe\"", "wt.exe")]
    public void AutoHotkeyExpressionsBecomePlainEnvironmentPaths(string expression, string expected) =>
        Assert.Equal(expected, AhkImporter.TranslateExpression(expression.Trim()));

    [Fact]
    public void AnExpressionAkuWmCannotTranslateIsFlaggedRatherThanStoredVerbatim()
    {
        // The Python prototype kept these as AutoHotkey source, which nothing
        // but AutoHotkey could ever evaluate.
        Assert.Null(AhkImporter.TranslateExpression("SomeFunction(x) \"\\tail\""));
    }

    [Fact]
    public void EveryImportedCommandIsRunnableWithoutAutoHotkey()
    {
        List<ShortcutConfig> shortcuts = Toggles(out _);

        Assert.All(shortcuts, s => Assert.False(string.IsNullOrEmpty(s.Command)));
        Assert.DoesNotContain(shortcuts, s => s.Command!.Contains("A_", StringComparison.Ordinal));
        Assert.DoesNotContain(shortcuts, s => s.Command!.Contains("EnvGet", StringComparison.Ordinal));
    }
}

public class StartupImporterTests : IDisposable
{
    public StartupImporterTests() => Ids.Generator = Ids.Sequential();

    public void Dispose() => Ids.Generator = prefix => Ids.Mint(prefix, Random.Shared);

    [Fact]
    public void TheStartupFolderIsReadFromTheRightPlace()
    {
        // The Python prototype built this path from the temp directory's
        // grandparent and dropped AppData, so it always found nothing.
        using var dir = new TempDir();
        string startup = Path.Combine(
            dir.Path, "Users", "diego", "AppData", "Roaming",
            "Microsoft", "Windows", "Start Menu", "Programs", "Startup");
        Directory.CreateDirectory(startup);
        File.WriteAllText(Path.Combine(startup, "Zebar.lnk"), string.Empty);

        string? found = StartupImporter.FindStartupFolder(_ => null, dir.Path);

        Assert.Equal(startup, found);
    }

    [Fact]
    public void ShortcutsBecomeEntriesAndZebarWaitsForTheIpc()
    {
        using var dir = new TempDir();
        foreach (string name in new[] { "Zebar.lnk", "ShareX.lnk", "readme.txt" })
        {
            File.WriteAllText(dir.File(name), string.Empty);
        }

        var summary = new ImportSummary();
        List<StartupConfig> entries = StartupImporter.Import(dir.Path, summary);

        Assert.Equal(2, entries.Count);
        Assert.Equal("ipc", entries.Single(e => e.Name == "Zebar").After);
        Assert.Equal("now", entries.Single(e => e.Name == "ShareX").After);
    }

    [Fact]
    public void WhatAkuWmReplacesIsImportedButLeftOff()
    {
        using var dir = new TempDir();
        foreach (string name in new[] { "GlazeWM.lnk", "hyper-desktops.lnk", "Zebar.lnk" })
        {
            File.WriteAllText(dir.File(name), string.Empty);
        }

        List<StartupConfig> entries = StartupImporter.Import(dir.Path, new ImportSummary());

        Assert.False(entries.Single(e => e.Name == "GlazeWM").Enabled);
        Assert.False(entries.Single(e => e.Name == "hyper-desktops").Enabled);
        Assert.True(entries.Single(e => e.Name == "Zebar").Enabled);
    }

    [Fact]
    public void AMissingFolderIsSaidOutLoud()
    {
        var summary = new ImportSummary();

        Assert.Empty(StartupImporter.Import(null, summary));
        Assert.Contains(summary.Notes, n => n.Contains("could not be located", StringComparison.Ordinal));
    }

    [Fact]
    public void AWslPathIsHandedToWindowsAsAWindowsPath() =>
        Assert.Equal(
            OperatingSystem.IsWindows() ? "/mnt/c/Users/diego/x.lnk" : @"C:\Users\diego\x.lnk",
            StartupImporter.WindowsPath("/mnt/c/Users/diego/x.lnk"));
}
