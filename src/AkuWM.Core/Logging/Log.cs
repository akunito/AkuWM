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
        }
    }

    public static string? FileName => _fileName;

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

        lock (Gate)
        {
            _file?.WriteLine(line);
            if (Console)
            {
                System.Console.Error.WriteLine(line);
            }
        }
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
        lock (Gate)
        {
            _file?.Dispose();
            _file = null;
        }
    }
}
