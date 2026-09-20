using AkuWM.Core.Config;
using Xunit;

namespace AkuWM.Tests;

public class ConfigMergeTests
{
    [Fact]
    public void AnAbsentFieldLeavesTheLayerBelowAlone()
    {
        var common = new AkuWmConfig { Gaps = new GapsConfig { Inner = 8, ScaleWithDpi = true } };
        var machine = new AkuWmConfig { Gaps = new GapsConfig { Inner = 12 } };

        AkuWmConfig merged = ConfigMerge.Merge(common, machine);

        Assert.Equal(12, merged.Gaps!.Inner);
        Assert.True(merged.Gaps.ScaleWithDpi);
    }

    [Fact]
    public void NestedObjectsMergeFieldByField()
    {
        var common = new AkuWmConfig
        {
            Settings = new SettingsConfig
            {
                Git = new GitSettings { AutoCommit = true, AutoPush = false },
                Journal = new JournalSettings { IntervalS = 60 },
            },
        };
        var machine = new AkuWmConfig
        {
            Settings = new SettingsConfig { Git = new GitSettings { AutoPush = true } },
        };

        AkuWmConfig merged = ConfigMerge.Merge(common, machine);

        Assert.True(merged.Settings!.Git!.AutoCommit);
        Assert.True(merged.Settings.Git.AutoPush);
        Assert.Equal(60, merged.Settings.Journal!.IntervalS);
    }

    [Fact]
    public void ListEntriesMergeOnTheirId()
    {
        var common = new AkuWmConfig
        {
            Rules =
            [
                new RuleConfig { Id = "r-1", Name = "Telegram", Actions = ["float", "sticky"], Enabled = true },
                new RuleConfig { Id = "r-2", Name = "Spotify", Actions = ["float"], Enabled = true },
            ],
        };
        var machine = new AkuWmConfig
        {
            Rules = [new RuleConfig { Id = "r-2", Enabled = false }],
        };

        AkuWmConfig merged = ConfigMerge.Merge(common, machine);

        Assert.Equal(2, merged.Rules!.Count);
        Assert.Equal("Spotify", merged.Rules[1].Name);      // the rest of the entry survives
        Assert.False(merged.Rules[1].Enabled);              // only what the machine said changed
        Assert.True(merged.Rules[0].Enabled);
    }

    [Fact]
    public void AnUnknownIdIsAppendedAndTheOrderOfTheLayerBelowIsKept()
    {
        var common = new AkuWmConfig { Rules = [new RuleConfig { Id = "r-1" }, new RuleConfig { Id = "r-2" }] };
        var machine = new AkuWmConfig { Rules = [new RuleConfig { Id = "r-9", Name = "only here" }] };

        AkuWmConfig merged = ConfigMerge.Merge(common, machine);

        Assert.Equal(["r-1", "r-2", "r-9"], merged.Rules!.Select(r => r.Id));
    }

    [Fact]
    public void WorkspacesMergeOnTheirName()
    {
        var common = new AkuWmConfig
        {
            Workspaces = [new WorkspaceConfig { Name = "11", Monitor = "main", KeepAlive = false }],
        };
        var machine = new AkuWmConfig
        {
            Workspaces = [new WorkspaceConfig { Name = "11", KeepAlive = true }],
        };

        AkuWmConfig merged = ConfigMerge.Merge(common, machine);

        WorkspaceConfig workspace = Assert.Single(merged.Workspaces!);
        Assert.Equal("main", workspace.Monitor);
        Assert.True(workspace.KeepAlive);
    }

    [Fact]
    public void TheDefaultsAreTheBottomLayer()
    {
        AkuWmConfig merged = ConfigMerge.MergeAll(
            ConfigDefaults.Create(),
            new AkuWmConfig { Gaps = new GapsConfig { Inner = 4 } },
            new AkuWmConfig());

        Assert.Equal(4, merged.Gaps!.Inner);
        Assert.Equal("#c4a7e7", merged.Effects!.FocusedBorder);
        Assert.Equal(60, merged.Settings!.Journal!.IntervalS);
    }

    [Fact]
    public void MergingNeverMutatesTheLayersItWasGiven()
    {
        var common = new AkuWmConfig { Gaps = new GapsConfig { Inner = 8 } };
        var machine = new AkuWmConfig { Gaps = new GapsConfig { Inner = 12 } };

        ConfigMerge.Merge(common, machine);

        Assert.Equal(8, common.Gaps!.Inner);
        Assert.Equal(12, machine.Gaps!.Inner);
    }
}
