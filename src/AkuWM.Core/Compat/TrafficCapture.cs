using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Nodes;
using AkuWM.Core.Logging;

namespace AkuWM.Core.Compat;

/// <summary>
/// Writes every frame the compatibility server sees to a JSONL file.
/// </summary>
/// <remarks>
/// <para>
/// The point of it is the bar: nothing on this side knows which requests Zebar
/// makes or which events it subscribes to, and the only way to find out is to
/// let it connect and write down what it said. The capture is then replayed in
/// the tests, so a change that stops answering one of them fails on Linux
/// rather than on the desk.
/// </para>
/// <para>
/// Queued and written on a thread of its own: the frames arrive on the socket
/// threads, and a capture that blocked one of those would make the bar wait on
/// a disk.
/// </para>
/// </remarks>
public sealed class TrafficCapture : IDisposable
{
    private const int Backlog = 4096;

    private readonly BlockingCollection<(long Ms, bool Out, string Text)> _queue =
        new(new ConcurrentQueue<(long, bool, string)>(), Backlog);

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly StreamWriter _writer;
    private readonly Thread _thread;

    private long _dropped;
    private int _disposed;

    private TrafficCapture(StreamWriter writer)
    {
        _writer = writer;
        _thread = new Thread(Drain) { IsBackground = true, Name = "akuwm-capture" };
        _thread.Start();
    }

    public long Frames { get; private set; }

    /// <summary>Null when the file cannot be written; the daemon carries on without it.</summary>
    public static TrafficCapture? Open(string file)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file) ?? ".");
            var writer = new StreamWriter(file, append: false) { AutoFlush = false };
            Log.Info($"capturing compatibility traffic to {file}");
            return new TrafficCapture(writer);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Log.Warn($"the traffic capture could not be opened: {ex.Message}");
            return null;
        }
    }

    public void Watch(GlazeIpcServer server) => server.Traffic += Record;

    public void Record(bool outbound, string text)
    {
        if (!_queue.TryAdd((_clock.ElapsedMilliseconds, outbound, text)))
        {
            Interlocked.Increment(ref _dropped);
        }
    }

    private void Drain()
    {
        foreach ((long ms, bool outbound, string text) in _queue.GetConsumingEnumerable())
        {
            var line = new JsonObject
            {
                ["ms"] = ms,
                ["dir"] = outbound ? "out" : "in",
                ["text"] = text,
            };

            try
            {
                _writer.WriteLine(line.ToJsonString(GlazeProtocol.Compact));
                Frames++;

                if (_queue.Count == 0)
                {
                    _writer.Flush();
                }
            }
            catch (IOException ex)
            {
                Log.Warn($"the traffic capture stopped writing: {ex.Message}");
                return;
            }
        }
    }

    public void Dispose()
    {
        // The daemon disposes it on its way out and a caller may already have;
        // the second one must not throw on a writer that is gone.
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _queue.CompleteAdding();
        _thread.Join(TimeSpan.FromSeconds(2));

        if (Interlocked.Read(ref _dropped) is > 0 and var dropped)
        {
            Log.Warn($"the traffic capture dropped {dropped} frame(s): more traffic than it could write");
        }

        _writer.Flush();
        _writer.Dispose();
    }
}
