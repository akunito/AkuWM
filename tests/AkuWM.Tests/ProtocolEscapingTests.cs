using System.Text.Json.Nodes;
using AkuWM.Core.Ipc;
using Xunit;

namespace AkuWM.Tests;

/// <summary>
/// The wire escapes control characters (this test is the proof). The raw
/// 0x07 `akuwm-cli query windows` printed on 2026-09-30 was not one: it was
/// a bullet, best-fit by the console's OEM code page -- CP437 draws 0x07 as
/// a bullet -- which is why the CLI now sets the console to UTF-8.
/// </summary>
public class ProtocolEscapingTests
{
    private const string Bell = "\u0007";

    [Fact]
    public void A_control_character_in_a_title_is_escaped_on_the_wire_and_in_the_pretty_print()
    {
        var node = new JsonObject { ["title"] = Bell + " Discord | #mancos" };
        string wire = CommandResponse.Ok("query windows", node).ToLine();
        string pretty = node.ToJsonString(Protocol.Pretty);
        Assert.DoesNotContain(Bell, wire);
        Assert.DoesNotContain(Bell, pretty);
        Assert.Contains("\\u0007", pretty);
        Assert.Equal(Bell + " Discord | #mancos", JsonNode.Parse(pretty)!["title"]!.GetValue<string>());
    }
}
