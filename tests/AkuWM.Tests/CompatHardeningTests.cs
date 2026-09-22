using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using AkuWM.Core.Commands;
using AkuWM.Core.Compat;
using AkuWM.Core.Config;
using AkuWM.Core.Ipc;
using Xunit;

namespace AkuWM.Tests;

/// <summary>
/// The bar's socket and the pipe against a client that is hostile, or merely
/// broken (security audit 2026-09-22).
/// </summary>
public class CompatHardeningTests
{
    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static async Task<ClientWebSocket> Connect(int port)
    {
        var client = new ClientWebSocket();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await client.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/"), deadline.Token);
        return client;
    }

    private static async Task<string> Ask(ClientWebSocket client, string request)
    {
        await client.SendAsync(Encoding.UTF8.GetBytes(request), WebSocketMessageType.Text, true, CancellationToken.None);
        var buffer = new byte[64 * 1024];
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        WebSocketReceiveResult result = await client.ReceiveAsync(buffer, deadline.Token);
        return Encoding.UTF8.GetString(buffer, 0, result.Count);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("null", true)]
    [InlineData("http://localhost:4200", true)]
    [InlineData("http://127.0.0.1", true)]
    [InlineData("app://zebar", true)]
    [InlineData("https://evil.example", false)]
    [InlineData("http://192.168.8.96:8080", false)]
    public void Only_a_page_from_this_machine_may_open_the_socket(string? origin, bool allowed) =>
        Assert.Equal(allowed, GlazeIpcServer.OriginAllowed(origin));

    [Fact]
    public async Task A_web_page_from_the_internet_is_refused_at_the_handshake()
    {
        int port = FreePort();
        await using var server = new GlazeIpcServer(port, _ => ExecResult.Ok());
        server.Start();

        using var raw = new TcpClient();
        await raw.ConnectAsync(IPAddress.Loopback, port);
        NetworkStream stream = raw.GetStream();
        byte[] request = Encoding.ASCII.GetBytes(
            "GET / HTTP/1.1\r\nHost: 127.0.0.1\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n"
            + "Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\nSec-WebSocket-Version: 13\r\n"
            + "Origin: https://evil.example\r\n\r\n");
        await stream.WriteAsync(request);

        var buffer = new byte[1024];
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        int read = await stream.ReadAsync(buffer, deadline.Token);

        Assert.StartsWith("HTTP/1.1 403", Encoding.ASCII.GetString(buffer, 0, read), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_client_that_stops_reading_does_not_stop_the_others()
    {
        int port = FreePort();
        await using var server = new GlazeIpcServer(port, _ => ExecResult.Ok());
        server.Start();

        using ClientWebSocket stalled = await Connect(port);
        using ClientWebSocket healthy = await Connect(port);
        await Ask(stalled, "sub -e all");
        await Ask(healthy, "sub -e all");

        // The healthy one reads as a bar does, all the time.
        var sawTheLastOne = new TaskCompletionSource();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Task reading = Task.Run(async () =>
        {
            var buffer = new byte[16 * 1024];
            while (!stop.IsCancellationRequested)
            {
                WebSocketReceiveResult result = await healthy.ReceiveAsync(buffer, stop.Token);
                if (Encoding.UTF8.GetString(buffer, 0, result.Count).Contains("\"last\":true", StringComparison.Ordinal))
                {
                    sawTheLastOne.TrySetResult();
                    return;
                }
            }
        });

        // The stalled one never reads again. Enough events to fill its
        // socket buffers and then its outbox, so it is dropped -- and none
        // of that waits on it.
        string padding = new('x', 4000);
        for (int i = 0; i < 3000; i++)
        {
            await server.Publish("workspace_activated", new JsonObject { ["padding"] = padding });
        }

        await server.Publish("focus_changed", new JsonObject { ["last"] = true });

        Assert.True(
            await Task.WhenAny(sawTheLastOne.Task, Task.Delay(10_000)) == sawTheLastOne.Task,
            "the healthy client waited on the stalled one");
        await reading;
    }

    [Fact]
    public async Task A_message_too_big_to_be_a_request_closes_the_connection()
    {
        int port = FreePort();
        await using var server = new GlazeIpcServer(port, _ => ExecResult.Ok());
        server.Start();

        using ClientWebSocket client = await Connect(port);
        byte[] huge = Encoding.UTF8.GetBytes("query " + new string('x', 70 * 1024));
        await client.SendAsync(huge, WebSocketMessageType.Text, true, CancellationToken.None);

        var buffer = new byte[1024];
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        WebSocketReceiveResult result = await client.ReceiveAsync(buffer, deadline.Token);

        Assert.Equal(WebSocketMessageType.Close, result.MessageType);
        Assert.Equal(WebSocketCloseStatus.MessageTooBig, result.CloseStatus);
    }

    [Fact]
    public async Task A_connection_cannot_subscribe_without_end()
    {
        int port = FreePort();
        await using var server = new GlazeIpcServer(port, _ => ExecResult.Ok());
        server.Start();

        using ClientWebSocket client = await Connect(port);
        for (int i = 0; i < 16; i++)
        {
            Assert.Contains("\"success\":true", await Ask(client, "sub -e focus_changed"));
        }

        Assert.Contains("\"success\":false", await Ask(client, "sub -e focus_changed"));
    }

    [Fact]
    public async Task A_pipe_request_longer_than_the_cap_is_dropped_not_buffered()
    {
        using var dir = new TempDir();
        string name = "akuwm-test-" + Guid.NewGuid().ToString("n")[..12];
        var paths = new ConfigPaths(dir.Path, "TEST", dir.Path);
        await using var server = new PipeServer(
            new CommandRouter(new ConfigCommands(paths), new DoctorCommand(paths, () => true)), name);
        server.Start(instances: 1);

        using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(2000);
        byte[] flood = Encoding.UTF8.GetBytes(new string('a', PipeServer.MaxLineBytes + 4096));

        // Hung up on, with nothing answered: either the write is refused
        // part-way or the read finds the other end gone.
        int read = -1;
        try
        {
            await client.WriteAsync(flood);
            var buffer = new byte[64];
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            read = await client.ReadAsync(buffer, deadline.Token);
        }
        catch (IOException)
        {
            read = 0;
        }

        Assert.Equal(0, read);

        // And the server is still there for the next client.
        Assert.True(new PipeClient(name).Send("version").Success);
    }
}
