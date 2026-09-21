using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using AkuWM.Core.Compat;
using Xunit;

namespace AkuWM.Tests;

/// <summary>
/// The server the bar and the scripts connect to, exercised by a real
/// WebSocket client -- which is the only way to know the handshake, written by
/// hand here, is one a client will accept.
/// </summary>
public class GlazeIpcServerTests
{
    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    [Fact]
    public async Task A_port_that_is_taken_is_waited_out_rather_than_given_up_on()
    {
        int port = FreePort();

        // Somebody else has it: the sockets of a window manager that died keep
        // the endpoint reserved for minutes after its process is gone.
        var squatter = new TcpListener(IPAddress.Loopback, port);
        squatter.Start();

        await using var server = new GlazeIpcServer(
            port, _ => ExecResult.Ok(data: new JsonObject { ["ran"] = true }));

        Assert.False(server.Start());
        Assert.NotNull(server.Unavailable);

        squatter.Stop();

        // It comes up by itself. Without the wait the bar had no live updates
        // until somebody restarted the daemon by hand.
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (server.Unavailable is not null && DateTime.UtcNow < deadline)
        {
            await Task.Delay(250);
        }

        Assert.Null(server.Unavailable);

        using ClientWebSocket client = await Connect(port);
        Assert.Contains("\"ran\":true", await Ask(client, "command focus --direction left"));
    }

    private static async Task<string> Ask(ClientWebSocket client, string request)
    {
        await client.SendAsync(
            Encoding.UTF8.GetBytes(request), WebSocketMessageType.Text, true, CancellationToken.None);

        var buffer = new byte[64 * 1024];
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        WebSocketReceiveResult result = await client.ReceiveAsync(buffer, deadline.Token);
        return Encoding.UTF8.GetString(buffer, 0, result.Count);
    }

    private static async Task<ClientWebSocket> Connect(int port)
    {
        var client = new ClientWebSocket();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await client.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/"), deadline.Token);
        return client;
    }

    [Fact]
    public async Task The_bar_socket_will_not_run_a_program()
    {
        // The reason this test exists: a WebSocket handshake is not subject to
        // the same-origin policy, so any page in any browser can open this
        // socket. `shell-exec` on it meant one text frame from a web page
        // could start any program on the desk -- and AkuWM runs with uiAccess.
        int port = FreePort();
        await using var server = new GlazeIpcServer(
            port, _ => ExecResult.Ok(data: new JsonObject { ["ran"] = true }));
        Assert.True(server.Start());

        using ClientWebSocket client = await Connect(port);

        foreach (string forbidden in new[]
        {
            "command shell-exec calc.exe",
            "command shell-exec \"C:\\Windows\\System32\\cmd.exe\"",
            "command wm-exit",
            "command close",
            "command wm-reload-config",
        })
        {
            JsonObject reply = (JsonObject)JsonNode.Parse(await Ask(client, forbidden))!;

            Assert.False((bool)reply["success"]!, forbidden + " was allowed");
            Assert.Contains("not available", (string)reply["error"]!, StringComparison.Ordinal);
            Assert.Null(reply["data"]);
        }
    }

    [Fact]
    public async Task The_bar_socket_still_does_everything_a_bar_needs()
    {
        int port = FreePort();
        await using var server = new GlazeIpcServer(
            port, request => ExecResult.Ok(data: new JsonObject { ["asked"] = request }));
        Assert.True(server.Start());

        using ClientWebSocket client = await Connect(port);

        // Captured from Zebar on the desk, plus what a pill click sends.
        foreach (string allowed in new[]
        {
            "query monitors",
            "query windows",
            "query focused",
            "query paused",
            "command focus --workspace 11",
            "command toggle-floating",
            "command wm-redraw",
        })
        {
            JsonObject reply = (JsonObject)JsonNode.Parse(await Ask(client, allowed))!;
            Assert.True((bool)reply["success"]!, allowed + " was refused");
        }
    }

    [Fact]
    public void The_handshake_matches_the_worked_example_in_the_specification()
    {
        // RFC 6455's own pair. The constant behind this is one that looks
        // right when written from memory and is not, and the failure it causes
        // is every client on the machine refusing to connect.
        Assert.Equal("s3pPLMBiTxaQ9kYGzzhZRbK+xOo=", GlazeIpcServer.AcceptFor("dGhlIHNhbXBsZSBub25jZQ=="));
    }

    [Fact]
    public async Task A_client_connects_and_gets_an_answer_in_the_envelope()
    {
        int port = FreePort();
        await using var server = new GlazeIpcServer(
            port, request => ExecResult.Ok(data: new JsonObject { ["asked"] = request }));

        Assert.True(server.Start());

        using ClientWebSocket client = await Connect(port);
        string reply = await Ask(client, "query windows");

        JsonNode envelope = JsonNode.Parse(reply)!;
        Assert.Equal("client_response", envelope["messageType"]!.GetValue<string>());
        // The scripts check for this field to know the window manager answered
        // at all; an empty answer reads exactly like "the app is not running".
        Assert.Equal("query windows", envelope["clientMessage"]!.GetValue<string>());
        Assert.True(envelope["success"]!.GetValue<bool>());
        Assert.Equal("query windows", envelope["data"]!["asked"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_refusal_keeps_the_connection_open()
    {
        int port = FreePort();
        await using var server = new GlazeIpcServer(port, _ => ExecResult.Fail("unrecognized subcommand 'x'"));
        server.Start();

        using ClientWebSocket client = await Connect(port);

        // A verb the socket allows, refused by the window manager itself.
        JsonNode first = JsonNode.Parse(await Ask(client, "command focus --nowhere"))!;
        Assert.False(first["success"]!.GetValue<bool>());
        Assert.Equal("unrecognized subcommand 'x'", first["error"]!.GetValue<string>());

        // A script that sends a command the window manager does not know must
        // not have to reconnect to send the next one -- and neither must one
        // that sends a verb this socket does not carry.
        JsonNode second = JsonNode.Parse(await Ask(client, "command shell-exec calc.exe"))!;
        Assert.False(second["success"]!.GetValue<bool>());

        JsonNode third = JsonNode.Parse(await Ask(client, "query windows"))!;
        Assert.False(third["success"]!.GetValue<bool>());
    }

    [Fact]
    public async Task A_subscriber_is_told_when_something_happens()
    {
        int port = FreePort();
        await using var server = new GlazeIpcServer(port, _ => ExecResult.Ok());
        server.Start();

        using ClientWebSocket client = await Connect(port);
        JsonNode subscription = JsonNode.Parse(await Ask(client, "sub -e workspace_activated"))!;
        Assert.True(subscription["data"]!["subscriptionId"] is not null);

        await server.Publish("workspace_activated", new JsonObject { ["name"] = "12" });

        var buffer = new byte[16 * 1024];
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        WebSocketReceiveResult result = await client.ReceiveAsync(buffer, deadline.Token);
        JsonNode published = JsonNode.Parse(Encoding.UTF8.GetString(buffer, 0, result.Count))!;

        Assert.Equal("event_subscription", published["messageType"]!.GetValue<string>());
        Assert.Equal("workspace_activated", published["data"]!["eventType"]!.GetValue<string>());
        Assert.Equal("12", published["data"]!["name"]!.GetValue<string>());
        Assert.Equal(
            subscription["data"]!["subscriptionId"]!.GetValue<string>(),
            published["subscriptionId"]!.GetValue<string>());
    }

    [Fact]
    public async Task An_event_nobody_asked_for_is_not_sent()
    {
        int port = FreePort();
        await using var server = new GlazeIpcServer(port, _ => ExecResult.Ok());
        server.Start();

        using ClientWebSocket client = await Connect(port);
        await Ask(client, "sub -e workspace_activated");
        await server.Publish("window_managed", new JsonObject());

        // Nothing should arrive; asking for one and getting the other would
        // have the bar redrawing on every window that opens.
        var buffer = new byte[1024];
        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await client.ReceiveAsync(buffer, deadline.Token));
    }

    [Fact]
    public async Task Unsubscribing_stops_the_events()
    {
        int port = FreePort();
        await using var server = new GlazeIpcServer(port, _ => ExecResult.Ok());
        server.Start();

        using ClientWebSocket client = await Connect(port);
        await Ask(client, "sub -e all");
        await Ask(client, "unsub");
        await server.Publish("focus_changed", new JsonObject());

        var buffer = new byte[1024];
        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await client.ReceiveAsync(buffer, deadline.Token));
    }

    [Fact]
    public async Task Two_clients_are_served_at_once()
    {
        int port = FreePort();
        await using var server = new GlazeIpcServer(port, request => ExecResult.Ok(
            data: new JsonObject { ["asked"] = request }));
        server.Start();

        // The bar holds a connection open for hours while scripts open one per
        // command; if the second had to wait for the first, every gesture on
        // the desk would block behind the bar.
        using ClientWebSocket bar = await Connect(port);
        using ClientWebSocket script = await Connect(port);

        Assert.Contains("query workspaces", await Ask(bar, "query workspaces"));
        Assert.Contains("command focus", await Ask(script, "command focus --workspace 11"));
        Assert.Contains("query monitors", await Ask(bar, "query monitors"));
    }

    [Fact]
    public async Task A_request_split_across_frames_is_put_back_together()
    {
        int port = FreePort();
        await using var server = new GlazeIpcServer(port, request => ExecResult.Ok(
            data: new JsonObject { ["length"] = request.Length }));
        server.Start();

        using ClientWebSocket client = await Connect(port);

        // A WebSocket message can arrive in any number of frames, and the
        // sender decides. Reading one frame and answering it would answer half
        // a command.
        string first = "command focus --workspace ";
        string second = new string('1', 40_000);

        await client.SendAsync(
            Encoding.UTF8.GetBytes(first), WebSocketMessageType.Text, false, CancellationToken.None);
        await client.SendAsync(
            Encoding.UTF8.GetBytes(second), WebSocketMessageType.Text, true, CancellationToken.None);

        var buffer = new byte[64 * 1024];
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        WebSocketReceiveResult result = await client.ReceiveAsync(buffer, deadline.Token);
        JsonNode reply = JsonNode.Parse(Encoding.UTF8.GetString(buffer, 0, result.Count))!;

        Assert.Equal(first.Length + second.Length, reply["data"]!["length"]!.GetValue<int>());
    }

    [Fact]
    public async Task A_client_that_goes_away_mid_sentence_does_not_take_the_server_with_it()
    {
        int port = FreePort();
        await using var server = new GlazeIpcServer(port, _ => ExecResult.Ok());
        server.Start();

        var rude = new ClientWebSocket();
        await rude.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/"), CancellationToken.None);
        await rude.SendAsync(
            Encoding.UTF8.GetBytes("command focus"), WebSocketMessageType.Text, false, CancellationToken.None);
        rude.Abort();
        rude.Dispose();

        await Task.Delay(200);

        // The bar is restarted, a script is killed: the desk carries on.
        using ClientWebSocket next = await Connect(port);
        Assert.Contains("client_response", await Ask(next, "query windows"));
    }

    [Fact]
    public async Task A_command_that_throws_is_answered_not_hung_up_on()
    {
        int port = FreePort();
        int asked = 0;
        await using var server = new GlazeIpcServer(port, _ =>
            ++asked == 1 ? throw new InvalidOperationException("a bad rectangle") : ExecResult.Ok());
        server.Start();

        using ClientWebSocket client = await Connect(port);

        JsonNode first = JsonNode.Parse(await Ask(client, "command focus --direction left"))!;
        Assert.False(first["success"]!.GetValue<bool>());
        Assert.Contains("a bad rectangle", first["error"]!.GetValue<string>());

        // The connection has to survive it: the bar comes back having lost
        // every subscription it made, and a script loses the rest of its
        // sequence.
        JsonNode second = JsonNode.Parse(await Ask(client, "query windows"))!;
        Assert.True(second["success"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Subscribing_while_events_are_firing_does_not_drop_them()
    {
        int port = FreePort();
        await using var server = new GlazeIpcServer(port, _ => ExecResult.Ok());
        server.Start();

        using ClientWebSocket client = await Connect(port);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        Task publishing = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                await server.Publish("workspace_activated", new JsonObject { ["n"] = 1 });
            }
        });

        // The subscription map used to be mutated outside the lock Publish
        // reads it under, so a sub arriving mid-event threw and the event was
        // silently dropped.
        for (int i = 0; i < 20 && !stop.IsCancellationRequested; i++)
        {
            await Ask(client, "sub -e all");
            await Ask(client, "unsub");
        }

        stop.Cancel();
        await publishing;
    }

    [Fact]
    public async Task A_port_that_is_already_taken_is_reported_rather_than_thrown()
    {
        int port = FreePort();
        var holder = new TcpListener(IPAddress.Loopback, port);
        holder.Start();

        try
        {
            await using var server = new GlazeIpcServer(port, _ => ExecResult.Ok());

            // Almost always the old window manager still running, and the
            // daemon has to come up anyway and say so.
            Assert.False(server.Start());
            Assert.Contains($"port {port}", server.Unavailable);
        }
        finally
        {
            holder.Stop();
        }
    }
}
