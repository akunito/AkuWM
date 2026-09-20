using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AkuWM.Core.Compat;
using Xunit;

namespace AkuWM.Tests;

/// <summary>
/// The capture that turns what the bar asks for into a file the tests can
/// replay.
/// </summary>
public class TrafficCaptureTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "akuwm-capture-" + Guid.NewGuid().ToString("N"));

    private string File(string name) => Path.Combine(_dir, name);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }

        GC.SuppressFinalize(this);
    }

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static JsonObject[] Read(string file) =>
        [.. System.IO.File.ReadAllLines(file)
            .Where(l => l.Length > 0)
            .Select(l => (JsonObject)JsonNode.Parse(l)!)];

    [Fact]
    public void It_writes_a_line_for_every_frame_in_the_order_they_happened()
    {
        string file = File("plain.jsonl");

        using (TrafficCapture? capture = TrafficCapture.Open(file))
        {
            Assert.NotNull(capture);
            capture.Record(outbound: false, "query workspaces");
            capture.Record(outbound: true, "{\"success\":true}");
        }

        JsonObject[] lines = Read(file);
        Assert.Equal(2, lines.Length);
        Assert.Equal("in", (string?)lines[0]["dir"]);
        Assert.Equal("query workspaces", (string?)lines[0]["text"]);
        Assert.Equal("out", (string?)lines[1]["dir"]);
    }

    [Fact]
    public void A_frame_with_quotes_and_newlines_comes_back_exactly_as_it_went_in()
    {
        // The file is read by a test and by a person; a frame that broke the
        // line would lose every frame after it.
        string awkward = "sub -e \"a,b\"\n\tand a tab \\ and a backslash";
        string file = File("awkward.jsonl");

        using (TrafficCapture? capture = TrafficCapture.Open(file))
        {
            capture!.Record(outbound: false, awkward);
        }

        Assert.Single(Read(file));
        Assert.Equal(awkward, (string?)Read(file)[0]["text"]);
    }

    [Fact]
    public void It_refuses_a_file_it_cannot_write_and_says_so_rather_than_throwing()
    {
        Directory.CreateDirectory(_dir);
        System.IO.File.WriteAllText(File("in-the-way"), "");

        Assert.Null(TrafficCapture.Open(Path.Combine(_dir, "in-the-way", "deeper.jsonl")));
    }

    [Fact]
    public async Task It_records_both_sides_of_a_real_conversation_and_the_events()
    {
        int port = FreePort();
        string file = File("live.jsonl");

        TrafficCapture? capture = TrafficCapture.Open(file);
        await using (var server = new GlazeIpcServer(
            port, request => ExecResult.Ok(data: new JsonObject { ["asked"] = request })))
        {
            capture!.Watch(server);
            Assert.True(server.Start());

            using var client = new ClientWebSocket();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await client.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/"), deadline.Token);

            var buffer = new byte[64 * 1024];

            await client.SendAsync(
                Encoding.UTF8.GetBytes("sub -e workspace_activated"),
                WebSocketMessageType.Text, true, deadline.Token);
            await client.ReceiveAsync(buffer, deadline.Token);

            await server.Publish("workspace_activated", new JsonObject { ["activatedWorkspace"] = "1" });
            await client.ReceiveAsync(buffer, deadline.Token);
        }

        capture!.Dispose();

        string[] text = [.. Read(file).Select(l => (string)l["text"]!)];
        Assert.Contains(text, t => t == "sub -e workspace_activated");
        Assert.Contains(text, t => t.Contains("subscriptionId", StringComparison.Ordinal));
        Assert.Contains(text, t => t.Contains("workspace_activated", StringComparison.Ordinal)
            && t.Contains("activatedWorkspace", StringComparison.Ordinal));
    }

    [Fact]
    public void Every_line_is_valid_json_on_its_own_so_the_replay_can_read_it_lazily()
    {
        string file = File("lazy.jsonl");

        using (TrafficCapture? capture = TrafficCapture.Open(file))
        {
            for (int i = 0; i < 200; i++)
            {
                capture!.Record(i % 2 == 0, $"frame {i} \"quoted\"");
            }
        }

        string[] lines = System.IO.File.ReadAllLines(file);
        Assert.Equal(200, lines.Length);

        foreach (string line in lines)
        {
            using JsonDocument parsed = JsonDocument.Parse(line);
            Assert.True(parsed.RootElement.TryGetProperty("ms", out _));
        }
    }
}
