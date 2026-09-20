using System.Text;
using System.Text.Json;
using AkuWM.Core.Logging;

namespace AkuWM.Core.State;

/// <summary>
/// Reading and writing the small state files AkuWM keeps outside its own
/// memory.
/// </summary>
/// <remarks>
/// These files exist for one reason: a process that hides and moves other
/// people's windows must leave a record of it that survives its own death. So
/// a write is atomic -- a temporary file next to the real one, then a move --
/// because a half-written recovery file is the one thing worse than no
/// recovery file, and a crash during the write is exactly the moment the
/// record matters.
/// </remarks>
public static class AtomicJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    /// <summary>The file's contents, or null when it is missing or unreadable.</summary>
    /// <remarks>
    /// Unreadable is deliberately not an error. These files only ever help; a
    /// corrupt one must never be the reason AkuWM refuses to start, so it is
    /// reported and treated as absent.
    /// </remarks>
    public static T? Read<T>(string file, string what)
        where T : class
    {
        if (!File.Exists(file))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(file, Encoding.UTF8), Options);
        }
        catch (Exception ex)
        {
            Log.Warn($"{what} could not be read ({ex.Message}); carrying on without it");
            return null;
        }
    }

    /// <summary>
    /// Writes the file, atomically, retrying briefly if somebody else has it.
    /// </summary>
    /// <remarks>
    /// Two processes legitimately write these at the same time: the daemon
    /// shutting down and an <c>akuwm rescue</c> that is what told it to. The
    /// temporary file is per process, so they never collide there; the move
    /// onto the real name can still lose a race with the other one's move, and
    /// Windows reports that as access denied. A moment later it is free.
    /// </remarks>
    public static void Write<T>(string file, T value, string what)
    {
        string? last = null;

        for (int attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                string directory = Path.GetDirectoryName(Path.GetFullPath(file))!;
                Directory.CreateDirectory(directory);

                string temporary = Path.Combine(
                    directory, $".{Path.GetFileName(file)}.{Environment.ProcessId}.tmp");

                File.WriteAllText(temporary, JsonSerializer.Serialize(value, Options), new UTF8Encoding(false));
                File.Move(temporary, file, overwrite: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                last = ex.Message;
                Thread.Sleep(30 * (attempt + 1));
            }
            catch (Exception ex)
            {
                Log.Error($"{what} could not be written: {ex.Message}");
                return;
            }
        }

        Log.Error($"{what} could not be written after four tries: {last}");
    }
}
