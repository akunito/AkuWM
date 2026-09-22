using System.Globalization;
using AkuWM.Core.Logging;
using AkuWM.Core.Model;

namespace AkuWM.Core.State;

/// <summary>How a window of one application was last closed: floating or tiled, and where.</summary>
/// <param name="Floating">It was floating.</param>
/// <param name="Width">Its frame, in pixels.</param>
/// <param name="OffsetX">Its frame's origin from the work area of the screen it was on, in pixels.</param>
public readonly record struct AppRecord(bool Floating, int Width, int Height, int OffsetX, int OffsetY);

/// <summary>
/// What each application's window looked like when it was last closed, so
/// the next one opens the same way.
/// </summary>
/// <remarks>
/// <para>
/// Keyed by process and window class, not by handle (that is the placement
/// journal's business, for a restart). Written only for a window no rule
/// spoke about: an explicit rule is the person's standing instruction and
/// outranks what they did last time. Elevated windows are left out; AkuWM
/// cannot place them anyway.
/// </para>
/// <para>
/// One small text file, rewritten whole on every change: a few hundred lines
/// at most, and a window closes far less often than anything else here.
/// </para>
/// </remarks>
public sealed class AppMemory
{
    private readonly string? _file;
    private readonly Dictionary<string, AppRecord> _records = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="file">Where to keep it; null keeps it in memory only.</param>
    public AppMemory(string? file = null)
    {
        _file = file;
        Load();
    }

    public int Count => _records.Count;

    public static string KeyOf(WindowSnapshot window) => window.ProcessName + "|" + window.ClassName;

    public AppRecord? Recall(string key) => _records.TryGetValue(key, out AppRecord record) ? record : null;

    public void Remember(string key, AppRecord record)
    {
        if (_records.TryGetValue(key, out AppRecord had) && had == record)
        {
            return;
        }

        _records[key] = record;
        Save();
    }

    public void Forget(string key)
    {
        if (_records.Remove(key))
        {
            Save();
        }
    }

    private void Load()
    {
        if (_file is null || !File.Exists(_file))
        {
            return;
        }

        try
        {
            foreach (string line in File.ReadLines(_file))
            {
                string[] parts = line.Split('\t');
                if (parts.Length != 6
                    || !bool.TryParse(parts[1], out bool floating)
                    || !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int w)
                    || !int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int h)
                    || !int.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out int x)
                    || !int.TryParse(parts[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out int y))
                {
                    continue;
                }

                _records[parts[0]] = new AppRecord(floating, w, h, x, y);
            }
        }
        catch (IOException ex)
        {
            Log.Warn($"the application memory could not be read: {ex.Message}");
        }
    }

    private void Save()
    {
        if (_file is null)
        {
            return;
        }

        try
        {
            var lines = new List<string>(_records.Count);
            foreach ((string key, AppRecord r) in _records)
            {
                lines.Add(string.Join('\t', key, r.Floating, r.Width.ToString(CultureInfo.InvariantCulture), r.Height.ToString(CultureInfo.InvariantCulture), r.OffsetX.ToString(CultureInfo.InvariantCulture), r.OffsetY.ToString(CultureInfo.InvariantCulture)));
            }

            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            File.WriteAllLines(_file, lines);
        }
        catch (IOException ex)
        {
            Log.Warn($"the application memory could not be written: {ex.Message}");
        }
    }
}
