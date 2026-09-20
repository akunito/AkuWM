using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AkuWM.Core.Config;

/// <summary>
/// How the configuration is written to disk: snake_case keys, indented, and
/// absent instead of null, so a machine layer only ever holds what it changes
/// and a diff shows the change and nothing else.
/// </summary>
public static class ConfigJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        // Apostrophes and back-ticks belong in a note, not as \u0027 escapes:
        // this file is read and edited by a person.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Compact form, for the pipe and the log.</summary>
    public static readonly JsonSerializerOptions Wire = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Write(AkuWmConfig config) => JsonSerializer.Serialize(config, Options);

    public static AkuWmConfig Read(string json) =>
        JsonSerializer.Deserialize<AkuWmConfig>(json, Options)
        ?? throw new ConfigException("the file is empty or holds a bare null");
}

/// <summary>A configuration that cannot be read or cannot be used.</summary>
public sealed class ConfigException : Exception
{
    public ConfigException(string message) : base(message)
    {
    }

    public ConfigException(string message, Exception inner) : base(message, inner)
    {
    }
}
