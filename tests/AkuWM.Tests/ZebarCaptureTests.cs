using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using AkuWM.Core.Compat;
using Xunit;

namespace AkuWM.Tests;

/// <summary>
/// The bar, replayed. Every request in the fixture came off the socket on
/// DESK_W11 on 2026-09-21 with Zebar 3.3.1 connected to AkuWM; nothing was
/// read from Zebar's source. A change that stops answering one of them fails
/// here, on Linux, instead of on the desk.
/// </summary>
public class ZebarCaptureTests
{
    private readonly DeskFixture _fixture = new();
    private readonly GlazeExecutor _executor;

    public ZebarCaptureTests() => _executor = new GlazeExecutor(_fixture.Desk, new FakeDeskPlatform());

    private static string[] Requests() =>
        [.. Fixture.Read(Fixture.ZebarRequests)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !line.StartsWith('#'))];

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    [Fact]
    public void The_capture_is_the_whole_of_what_the_bar_asks_for()
    {
        // If this ever changes, the capture was re-taken against a new Zebar
        // and the rest of this file has to be looked at again.
        Assert.Equal(
            [
                "query focused",
                "query binding-modes",
                "query tiling-direction",
                "query paused",
                "query monitors",
                "query windows",
                "sub --events all",
            ],
            Requests());
    }

    [Fact]
    public async Task Every_request_the_bar_makes_is_answered()
    {
        _fixture.Open(1, process: "zen", title: "a tab", className: "MozillaWindowClass");
        _fixture.Turn();

        int port = FreePort();
        await using var server = new GlazeIpcServer(port, _executor.Ask);
        Assert.True(server.Start());

        using var client = new ClientWebSocket();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await client.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/"), deadline.Token);

        var buffer = new byte[256 * 1024];

        foreach (string request in Requests())
        {
            await client.SendAsync(
                Encoding.UTF8.GetBytes(request), WebSocketMessageType.Text, true, deadline.Token);

            WebSocketReceiveResult result = await client.ReceiveAsync(buffer, deadline.Token);
            JsonObject reply = (JsonObject)JsonNode.Parse(
                Encoding.UTF8.GetString(buffer, 0, result.Count))!;

            Assert.True(
                (bool)reply["success"]!,
                $"'{request}' was refused: {reply["error"]}");
            Assert.Equal(request, (string?)reply["clientMessage"]);
        }
    }

    [Fact]
    public void The_pill_finds_every_field_it_draws_from()
    {
        _fixture.Open(1);
        _fixture.Turn();

        // workspaces.html reads exactly these off the monitor it sits on, and
        // sends `focus --workspace <name>` when one is clicked.
        JsonArray monitors = _executor.Query("monitors").Data!["monitors"]!.AsArray();

        foreach (JsonNode? monitor in monitors)
        {
            JsonArray workspaces = monitor!["children"]!.AsArray();
            Assert.NotEmpty(workspaces);

            foreach (JsonNode? workspace in workspaces)
            {
                Assert.False(string.IsNullOrEmpty((string?)workspace!["name"]));
                Assert.False(string.IsNullOrEmpty((string?)workspace["displayName"]));
                Assert.NotNull(workspace["isDisplayed"]);
                Assert.NotNull(workspace["hasFocus"]);
            }

            Assert.Single(workspaces, w => (bool)w!["isDisplayed"]!);
        }
    }

    [Fact]
    public void Clicking_a_pill_switches_the_workspace()
    {
        _fixture.Open(1);
        _fixture.Turn();

        Assert.True(_executor.Command("focus --workspace 12").Success);
        Assert.Equal("12", _fixture.Desk.MonitorByRole("main")!.Displayed!.Name);
    }
}
