using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using AkuWM.Core.Logging;

namespace AkuWM.Core.Compat;

/// <summary>
/// The WebSocket server the bar and the scripts already connect to.
/// </summary>
/// <remarks>
/// <para>
/// Port 6123, text frames, one request per frame, the request being the
/// command-line string. Captured black-box from the window manager AkuWM
/// replaces; nothing was read from its source. It exists so that Zebar and
/// 1,961 lines of working AutoHotkey keep working through the milestone where
/// everything underneath them is replaced.
/// </para>
/// <para>
/// The handshake is done by hand over a plain TCP listener rather than through
/// <c>HttpListener</c>, which on Windows wants a URL reservation and therefore
/// an administrator, once, on a machine that has one. A window manager that
/// needs an elevation prompt to start is not one anybody will keep.
/// </para>
/// </remarks>
public sealed class GlazeIpcServer : IAsyncDisposable
{
    /// <summary>
    /// The constant every WebSocket handshake appends to the client's key
    /// before hashing it. Part of the protocol, not a secret.
    /// </summary>
    /// <remarks>
    /// Checked against RFC 6455's own worked example in the tests, because
    /// writing it from memory produces something that looks right and fails
    /// every handshake: one transposed character here and no client on the
    /// machine will connect, with an error that names the value the server
    /// sent and not the one it should have.
    /// </remarks>
    private const string HandshakeSalt = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

    private readonly int _port;
    private readonly Func<string, ExecResult> _handle;
    private readonly List<Subscriber> _subscribers = [];
    private readonly CancellationTokenSource _stopping = new();
    private readonly object _gate = new();

    private TcpListener? _listener;
    private Task? _accepting;

    /// <param name="handle">
    /// Answers one request. Runs on the connection's thread, so whatever is
    /// behind it must be the thing that owns the model -- never the model
    /// itself.
    /// </param>
    public GlazeIpcServer(int port, Func<string, ExecResult> handle)
    {
        _port = port;
        _handle = handle;
    }

    /// <summary>
    /// Every frame, both ways, for the capture that answers what the bar needs.
    /// </summary>
    /// <remarks>
    /// Raised on the connection's thread, so a handler must not block: the
    /// capture queues and writes elsewhere.
    /// </remarks>
    public event Action<bool, string>? Traffic;

    public int Connections
    {
        get
        {
            lock (_gate)
            {
                return _subscribers.Count;
            }
        }
    }

    public bool Listening => _listener is not null;

    /// <summary>Why the port could not be taken, when it could not.</summary>
    public string? Unavailable { get; private set; }

    /// <summary>How long a taken port is waited out before giving up on it.</summary>
    /// <remarks>
    /// The sockets of a window manager that DIED do not go away when its
    /// process does: its half-closed connections keep the endpoint reserved,
    /// and on this desk that outlived the process by minutes. Trying once and
    /// giving up left the bar with no live updates until somebody noticed and
    /// restarted the daemon by hand (2026-09-21). Waiting is free -- the
    /// window manager is already arranging the desk without it.
    /// </remarks>
    public const int WaitForThePortMs = 120_000;
    private const int RetryEveryMs = 3_000;

    /// <summary>
    /// After the first two minutes, how often the port is tried again, for
    /// ever. A bind attempt costs nothing, and the socket of a manager that
    /// died can stay bound until a reboot (an exited watcher stuck in kernel
    /// teardown held it for a whole day on this desk, 2026-09-22); the bar
    /// must come back the moment it is free, not after somebody notices.
    /// </summary>
    private const int RetrySlowlyEveryMs = 30_000;

    /// <summary>Raised on the thread that bound the port, once it is listening.</summary>
    public event Action? Bound;

    public bool Start()
    {
        if (Bind())
        {
            return true;
        }

        // Taken, not broken: wait it out in the background. Anything else is
        // a real failure and retrying it would only repeat the same message.
        if (_lastError == SocketError.AddressAlreadyInUse)
        {
            _ = Task.Run(WaitForThePort);
        }

        return false;
    }

    private SocketError _lastError;

    private bool Bind()
    {
        try
        {
            _listener = new TcpListener(IPAddress.Loopback, _port);
            _listener.Start();
        }
        catch (SocketException ex)
        {
            // Almost always the old window manager still running. Saying which
            // is more use than the number.
            _lastError = ex.SocketErrorCode;
            Unavailable = $"port {_port} is taken ({ex.SocketErrorCode})";
            Log.Warn($"the compatibility server could not start: {Unavailable}");
            _listener = null;
            return false;
        }

        Unavailable = null;
        _accepting = Task.Run(AcceptLoop);
        Log.Info($"compatibility server listening on 127.0.0.1:{_port}");
        Bound?.Invoke();
        return true;
    }

    private async Task WaitForThePort()
    {
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(WaitForThePortMs);
        bool slowly = false;

        while (!_stopping.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(slowly ? RetrySlowlyEveryMs : RetryEveryMs, _stopping.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (Bind())
            {
                Log.Info($"port {_port} came free; the bar and the scripts can reach AkuWM again");
                return;
            }

            if (!slowly && DateTime.UtcNow >= deadline)
            {
                slowly = true;
                Log.Warn($"port {_port} was still taken after {WaitForThePortMs / 1000} s; trying every {RetrySlowlyEveryMs / 1000} s from now on");
            }
        }
    }

    private async Task AcceptLoop()
    {
        while (!_stopping.IsCancellationRequested && _listener is { } listener)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(_stopping.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                return;
            }

            _ = Task.Run(() => Serve(client));
        }
    }

    private async Task Serve(TcpClient client)
    {
        var subscriber = new Subscriber();

        try
        {
            using (client)
            {
                client.NoDelay = true;
                NetworkStream stream = client.GetStream();

                if (await Handshake(stream).ConfigureAwait(false) is not { } request)
                {
                    return;
                }

                using WebSocket socket = WebSocket.CreateFromStream(
                    stream, new WebSocketCreationOptions { IsServer = true });

                subscriber.Socket = socket;
                lock (_gate)
                {
                    _subscribers.Add(subscriber);
                }

                Log.Debug(() => $"a client connected: {request}");
                await Converse(socket, subscriber).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or OperationCanceledException)
        {
            // A bar that was restarted, a script that exited after one command.
            Log.Debug(() => $"a client went away: {ex.Message}");
        }
        catch (Exception ex)
        {
            Log.Error("a compatibility client failed", ex);
        }
        finally
        {
            lock (_gate)
            {
                _subscribers.Remove(subscriber);
            }
        }
    }

    /// <summary>The answer to a client's key, as the specification defines it.</summary>
    public static string AcceptFor(string key) =>
        Convert.ToBase64String(SHA1.HashData(Encoding.UTF8.GetBytes(key + HandshakeSalt)));

    /// <summary>The HTTP upgrade, done by hand.</summary>
    private static async Task<string?> Handshake(NetworkStream stream)
    {
        var buffer = new byte[8192];
        var request = new StringBuilder();
        int total = 0;

        while (!request.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
        {
            int read = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total)).ConfigureAwait(false);
            if (read <= 0)
            {
                return null;
            }

            total += read;
            request.Clear();
            request.Append(Encoding.UTF8.GetString(buffer, 0, total));

            if (total >= buffer.Length)
            {
                return null;
            }
        }

        string text = request.ToString();
        string? key = text
            .Split("\r\n")
            .FirstOrDefault(line => line.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase))
            ?.Split(':', 2)[1]
            .Trim();

        if (key is null)
        {
            return null;
        }

        string accept = AcceptFor(key);

        byte[] response = Encoding.UTF8.GetBytes(
            "HTTP/1.1 101 Switching Protocols\r\n"
            + "Upgrade: websocket\r\n"
            + "Connection: Upgrade\r\n"
            + $"Sec-WebSocket-Accept: {accept}\r\n\r\n");

        await stream.WriteAsync(response).ConfigureAwait(false);
        return text.Split("\r\n")[0];
    }

    private async Task Converse(WebSocket socket, Subscriber subscriber)
    {
        var buffer = new byte[16 * 1024];

        while (socket.State == WebSocketState.Open && !_stopping.IsCancellationRequested)
        {
            var message = new StringBuilder();
            WebSocketReceiveResult result;

            do
            {
                result = await socket.ReceiveAsync(buffer, _stopping.Token).ConfigureAwait(false);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await socket.CloseAsync(
                        WebSocketCloseStatus.NormalClosure, null, CancellationToken.None).ConfigureAwait(false);
                    return;
                }

                message.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            }
            while (!result.EndOfMessage);

            string request = message.ToString().Trim();
            if (request.Length == 0)
            {
                continue;
            }

            Traffic?.Invoke(false, request);
            string reply = Answer(request, subscriber);
            Traffic?.Invoke(true, reply);
            await Send(subscriber, reply).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// What a client on this socket is allowed to ask for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This socket is not a private channel. It is plain TCP on loopback with
    /// no authentication, and a WebSocket handshake is not subject to the
    /// same-origin policy: a page in any browser can open one, and the browser
    /// will not stop it. So everything reachable from here is reachable by any
    /// web page the person happens to visit.
    /// </para>
    /// <para>
    /// <c>shell-exec</c> was, which meant one text frame from a web page could
    /// start any program on the desk -- and AkuWM is installed to run with
    /// uiAccess, so the program it starts may inherit a token the page's own
    /// process could never obtain. That is the whole of the reason this list
    /// exists. <c>wm-exit</c> was a one-frame kill switch and <c>close</c>
    /// destroys work, so neither is here either.
    /// </para>
    /// <para>
    /// Everything left is window arrangement: a hostile page can shuffle
    /// windows, which is a nuisance and not a breach. The scripts are
    /// unaffected -- they reach AkuWM over the named pipe, which no page can
    /// open. Deliberately not configurable: an option to widen this is an
    /// option to hand a web page a shell.
    /// </para>
    /// </remarks>
    private static readonly HashSet<string> OnTheSocket = new(StringComparer.OrdinalIgnoreCase)
    {
        "focus", "move", "resize", "move-workspace", "drag-tile", "drag-to-top",
        "toggle-floating", "set-floating", "toggle-tiling", "set-tiling",
        "toggle-fullscreen", "set-fullscreen",
        "toggle-minimized", "set-minimized",
        "toggle-sticky", "set-sticky", "unset-sticky",
        "toggle-tiling-direction", "set-tiling-direction",
        "wm-redraw", "wm-toggle-pause",
        "ignore", "adjust-borders", "set-title-bar-visibility", "set-transparency",
    };

    /// <summary>One request, one reply, in the envelope the callers expect.</summary>
    private string Answer(string request, Subscriber subscriber)
    {
        string verb = request.Split(' ', 2)[0].ToLowerInvariant();
        string rest = request.Length > verb.Length ? request[(verb.Length + 1)..] : string.Empty;

        ExecResult result;
        try
        {
            result = verb switch
            {
                "sub" or "subscribe" => Subscribe(subscriber, rest),
                "unsub" or "unsubscribe" => Unsubscribe(subscriber, rest),
                "command" when !OnTheSocket.Contains(GlazeCommandLine.Parse(rest).Verb) =>
                    Refuse(rest),
                _ => _handle(request),
            };
        }
        catch (Exception ex)
        {
            // The envelope has error and success fields for exactly this. An
            // escape used to drop the connection, and the bar came back having
            // lost every subscription it had made.
            Log.Error($"'{request}' threw; answering with an error rather than dropping the client", ex);
            result = ExecResult.Fail($"{ex.GetType().Name}: {ex.Message}");
        }

        return GlazeProtocol.Reply(request, result).ToJsonString(GlazeProtocol.Compact);
    }

    private static ExecResult Refuse(string rest)
    {
        string verb = GlazeCommandLine.Parse(rest).Verb;
        Log.Warn(
            $"'{verb}' was asked for on the bar's socket and refused: anything reachable there is "
            + "reachable by a web page. Scripts should use the akuwm pipe.");

        return ExecResult.Fail($"'{verb}' is not available on this connection");
    }

    private ExecResult Subscribe(Subscriber subscriber, string rest)
    {
        ParsedCommand parsed = GlazeCommandLine.Parse("sub " + rest);
        string? events = parsed.Value("events") ?? parsed.Value("e");

        // `sub -e a,b` -- a single dash, which the option parser does not treat
        // as an option, so it lands among the positional arguments.
        if (events is null)
        {
            for (int at = 0; at + 1 < parsed.Positional.Count; at++)
            {
                if (parsed.Positional[at] is "-e" or "-events")
                {
                    events = parsed.Positional[at + 1];
                    break;
                }
            }
        }

        var id = Guid.NewGuid();
        string[] wanted = (events ?? "all")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        lock (_gate)
        {
            subscriber.Subscriptions[id] = wanted;
        }
        Log.Info($"a client subscribed to {string.Join(", ", wanted)}");

        return ExecResult.Ok(data: new JsonObject { ["subscriptionId"] = id.ToString() });
    }

    private ExecResult Unsubscribe(Subscriber subscriber, string rest)
    {
        ParsedCommand parsed = GlazeCommandLine.Parse("unsub " + rest);

        lock (_gate)
        {
            if (parsed.Value("id") is { Length: > 0 } text && Guid.TryParse(text, out Guid id))
            {
                subscriber.Subscriptions.Remove(id);
            }
            else
            {
                subscriber.Subscriptions.Clear();
            }
        }

        return ExecResult.Ok();
    }

    /// <summary>
    /// Tells every subscriber that something happened.
    /// </summary>
    /// <remarks>
    /// Once per matching subscription, as the callers expect: a client that
    /// subscribed twice is told twice, because that is how the bar counts what
    /// it asked for.
    /// </remarks>
    public async Task Publish(string eventType, JsonObject payload)
    {
        List<(Subscriber Subscriber, Guid Id)> targets;

        lock (_gate)
        {
            targets =
            [
                .. _subscribers.SelectMany(s => s.Subscriptions
                    .Where(sub => sub.Value.Contains("all") || sub.Value.Contains(eventType))
                    .Select(sub => (Subscriber: s, sub.Key)))
            ];
        }

        if (targets.Count == 0)
        {
            return;
        }

        // One frame, re-aimed. Only the subscription id differs between
        // subscribers, and building it per target cloned and re-serialised the
        // whole payload once per widget on the bar.
        JsonObject frame = GlazeProtocol.Event(targets[0].Id, eventType, payload);

        foreach ((Subscriber subscriber, Guid id) in targets)
        {
            GlazeProtocol.Subscription(frame, id);
            string text = frame.ToJsonString(GlazeProtocol.Compact);
            Traffic?.Invoke(true, text);
            await Send(subscriber, text).ConfigureAwait(false);
        }
    }

    private static async Task Send(Subscriber subscriber, string text)
    {
        if (subscriber.Socket is not { State: WebSocketState.Open } socket)
        {
            return;
        }

        // One writer at a time: a reply and an event racing on the same socket
        // interleave their frames and the client sees neither.
        await subscriber.Writing.WaitAsync().ConfigureAwait(false);
        try
        {
            await socket.SendAsync(
                Encoding.UTF8.GetBytes(text),
                WebSocketMessageType.Text,
                endOfMessage: true,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or IOException)
        {
            // The client went away between the check and the send.
        }
        finally
        {
            subscriber.Writing.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stopping.Cancel();
        _listener?.Stop();
        _listener = null;

        if (_accepting is not null)
        {
            try
            {
                await _accepting.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
                // Nothing left to wait for.
            }
        }

        _stopping.Dispose();
    }

    private sealed class Subscriber
    {
        public WebSocket? Socket { get; set; }

        public Dictionary<Guid, string[]> Subscriptions { get; } = [];

        public SemaphoreSlim Writing { get; } = new(1, 1);
    }
}
