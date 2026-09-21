using System.Text.Json.Nodes;
using AkuWM.Core.Compat;
using AkuWM.Core.Config;
using Xunit;

namespace AkuWM.Tests;

/// <summary>
/// Which workspaces the bar is told about: all of them, only the ones with
/// something in them, or that plus a few you always want there.
/// </summary>
/// <remarks>
/// The model always has all of them -- focusing an empty one has to work, and
/// that is what brings it into view. This is only what the bar sees.
/// </remarks>
public class EmptyWorkspaceTests
{
    private static AkuWmConfig Bar(bool showEmpty, params string[] keepAlive)
    {
        AkuWmConfig config = DeskFixture.Configuration();
        config.General!.ShowEmptyWorkspaces = showEmpty;

        foreach (WorkspaceConfig workspace in config.Workspaces!)
        {
            workspace.KeepAlive = keepAlive.Contains(workspace.Name);
        }

        return config;
    }

    private static string[] OnTheBar(DeskFixture fixture, string role)
    {
        JsonArray monitors = GlazeView.Monitors(fixture.Desk);

        foreach (JsonNode? monitor in monitors)
        {
            if ((string?)monitor!["role"] == role)
            {
                return [.. monitor["children"]!.AsArray().Select(w => (string)w!["name"]!)];
            }
        }

        return [];
    }

    [Fact]
    public void By_default_every_workspace_the_configuration_gives_a_screen_is_listed()
    {
        var fixture = new DeskFixture(Bar(showEmpty: true));
        fixture.Open(1);
        fixture.Turn();

        // The bar as a fixed map of the number row, which is what this desk
        // has always had.
        Assert.Equal(["11", "12", "13"], OnTheBar(fixture, "main"));
    }

    [Fact]
    public void Asked_to_hide_the_empty_ones_it_lists_what_has_windows()
    {
        var fixture = new DeskFixture(Bar(showEmpty: false));
        fixture.Open(1);
        fixture.Desk.FocusWorkspace("12");
        fixture.Open(2);
        fixture.Turn();

        Assert.Equal(["11", "12"], OnTheBar(fixture, "main"));
    }

    [Fact]
    public void The_one_you_are_looking_at_is_listed_even_when_it_is_empty()
    {
        var fixture = new DeskFixture(Bar(showEmpty: false));
        fixture.Open(1);
        fixture.Desk.FocusWorkspace("13");
        fixture.Turn();

        // Otherwise the bar has nothing to highlight and the person is
        // somewhere it says does not exist.
        Assert.Contains("13", OnTheBar(fixture, "main"));
    }

    [Fact]
    public void A_workspace_marked_keep_alive_is_listed_while_empty()
    {
        var fixture = new DeskFixture(Bar(showEmpty: false, keepAlive: "13"));
        fixture.Open(1);
        fixture.Turn();

        // Ten assigned to a screen, and only the few you always want present.
        Assert.Equal(["11", "13"], OnTheBar(fixture, "main"));
    }

    [Fact]
    public void Hiding_one_from_the_bar_does_not_take_it_off_the_desk()
    {
        var fixture = new DeskFixture(Bar(showEmpty: false));
        fixture.Open(1);
        fixture.Turn();

        Assert.DoesNotContain("12", OnTheBar(fixture, "main"));

        // It is still there to be reached, and reaching it brings it into
        // view: the bar is a display, not the model.
        fixture.Desk.FocusWorkspace("12");
        fixture.Turn();

        Assert.Contains("12", OnTheBar(fixture, "main"));
        Assert.NotNull(fixture.Desk.Workspace("12"));
    }
}
