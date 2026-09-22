using System.Diagnostics;
using System.Text;

namespace AkuWM.Core.Logging;

public enum LogLevel
{
    Debug,
    Info,
    Warn,
    Error,
}

/// <summary>
/// One rolling file plus the console, written from any thread.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately small: a window manager's log is read when something went
/// wrong on the desk, so the format is one line per event, sortable, with the
/// thread that produced it -- the platform thread, the wm thread and the ipc
/// thread all write here and telling them apart is most of the diagnosis.
/// </para>
/// <para>
/// Debug is off by default and costs nothing when off: the message is passed
/// as a closure and is not built unless the level is on. It is switched at
/// runtime with <c>akuwm debug on</c>, which drops a marker file -- the idea is
/// taken from the AutoHotkey stack it replaces, where it earned its keep.
/// </para>
/// </remarks>
public static class Log
{
    private static readonly object Gate = new();
    private static readonly Stopwatch Uptime = Stopwatch.StartNew();
    private static StreamWriter? _file;
    private static string? _fileName;

    public static LogLevel Level { get; set; } = LogLevel.Info;

    /// <summary>Also write to stderr. On for the CLI, off for the daemon.</summary>
    public static bool Console { get; set; } = true;

    public static bool DebugOn => Level <= LogLevel.Debug;

    /// <summary>
    /// Starts writing to <paramref name="directory"/>, rolling the file when it
    /// passes <paramref name="maxBytes"/> (one generation kept: the desk
    /// produces a few hundred kilobytes a day and the interesting part is
    /// always the end).
    /// </summary>
    public static void ToDirectory(string directory, long maxBytes = 4 * 1024 * 1024)
    {
        Flush(); // what was queued goes to the file it was written for
        lock (Gate)
        {
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "akuwm.log");

            if (File.Exists(path) && new FileInfo(path).Length > maxBytes)
            {
                string previous = Path.Combine(directory, "akuwm.log.1");
                File.Delete(previous);
                File.Move(path, previous);
            }

            _file?.Dispose();
            _file = new StreamWriter(
                new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite),
                new UTF8Encoding(false))
            {
                AutoFlush = true,
            };
            _fileName = path;
            _maxBytes = maxBytes;
            _written = File.Exists(path) ? new FileInfo(path).Length : 0;
        }
    }

    public static string? FileName => _fileName;

    /// <summary>Lets the log file go. For tests, which must be able to delete it.</summary>
    public static void ToConsoleOnly()
    {
        Flush();
        lock (Gate)
        {
            _file?.Dispose();
            _file = null;
            _fileName = null;
            _written = 0;
        }
    }

    public static void Debug(Func<string> message)
    {
        if (DebugOn)
        {
            Write(LogLevel.Debug, message());
        }
    }

    public static void Debug(string message) => Write(LogLevel.Debug, message);

    public static void Info(string message) => Write(LogLevel.Info, message);

    public static void Warn(string message) => Write(LogLevel.Warn, message);

    public static void Error(string message) => Write(LogLevel.Error, message);

    public static void Error(string message, Exception exception) =>
        Write(LogLevel.Error, $"{message}: {exception.GetType().Name}: {exception.Message}");

    private static void Write(LogLevel level, string message)
    {
        if (level < Level)
        {
            return;
        }

        string line = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {Name(level)} [{Thread.CurrentThread.Name ?? "t" + Environment.CurrentManagedThreadId}] {message}");

        // Off the caller's thread. The wm thread logs on the gesture path --
        // at DBG, which this desk runs at, every redraw -- and each line was
        // a formatted write and a flush to disk under a global lock, with
        // Defender scanning the directory: tens of microseconds on a good
        // day, milliseconds on a bad one, inside a 5 ms budget. Errors are
        // still written through, because the line after an error may be the
        // crash. A bounded queue: if the writer falls 4096 lines behind, the
        // caller waits rather than the process eating memory.
        if (level >= LogLevel.Error || _queue is null)
        {
            Emit(line);
            return;
        }

        if (!_queue.Writer.TryWrite(line))
        {
            _queue.Writer.WriteAsync(line).AsTask().GetAwaiter().GetResult();
        }
    }

    private static readonly System.Threading.Channels.Channel<string> _queue =
        System.Threading.Channels.Channel.CreateBounded<string>(new System.Threading.Channels.BoundedChannelOptions(4096)
        {
            SingleReader = true,
            FullMode = System.Threading.Channels.BoundedChannelFullMode.Wait,
        });

    private static readonly Thread WriterThread = Start();

    private static Thread Start()
    {
        var thread = new Thread(() =>
        {
            while (_queue.Reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
            {
                while (_queue.Reader.TryRead(out string? line))
                {
                    Emit(line);
                }
            }
        })
        {
            IsBackground = true,
            Name = "akuwm-log",
        };
        thread.Start();
        return thread;
    }

    /// <summary>Writes what the queue holds. Before a Close, and before an exit.</summary>
    public static void Flush()
    {
        SpinWait.SpinUntil(() => _queue.Reader.Count == 0, 2000);
        lock (Gate)
        {
            _file?.Flush();
        }
    }

    private static void Emit(string line)
    {
        lock (Gate)
        {
            if (_file is not null)
            {
                _file.WriteLine(line);

                // Rotation used to happen only when the daemon STARTED, which
                // was fine while the log was a few hundred kilobytes a day.
                // With debug tracing left on it is megabytes an hour -- every
                // frame the bar exchanges goes through here -- and a daemon
                // that runs for days never got the chance. Counted rather than
                // asked: a FileInfo per line would be a syscall per line.
                _written += line.Length + 2;
                if (_written > _maxBytes)
                {
                    Roll();
                }
            }

            if (Console)
            {
                System.Console.Error.WriteLine(line);
            }
        }
    }

    private static long _written;
    private static long _maxBytes = 4 * 1024 * 1024;

    /// <summary>Called with the lock held.</summary>
    private static void Roll()
    {
        if (_fileName is not { } path)
        {
            return;
        }

        _file?.Dispose();
        _file = null;

        try
        {
            string previous = path + ".1";
            File.Delete(previous);
            File.Move(path, previous);
        }
        catch (IOException)
        {
            // Somebody has it open (a tail, an editor). Keep writing to the
            // one we have rather than losing the log over a tidy-up.
        }

        _file = new StreamWriter(
            new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite),
            new UTF8Encoding(false))
        {
            AutoFlush = true,
        };
        _written = new FileInfo(path).Length;
    }

    private static string Name(LogLevel level) => level switch
    {
        LogLevel.Debug => "DBG",
        LogLevel.Info => "INF",
        LogLevel.Warn => "WRN",
        LogLevel.Error => "ERR",
        _ => "???",
    };

    /// <summary>How long this process has been up, for <c>doctor</c>.</summary>
    public static TimeSpan Elapsed => Uptime.Elapsed;

    public static void Close()
    {
        Flush();
        lock (Gate)
        {
            _file?.Dispose();
            _file = null;
        }
    }
}
