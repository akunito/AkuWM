using System.Text.Json;
using System.Text.Json.Serialization;

namespace AkuWM.Core.Config;

/// <summary>
/// Reads <c>"outer": 12</c> as well as <c>"outer": [12, 12, 12, 12]</c>.
/// </summary>
/// <remarks>
/// <para>
/// One number is what a person means almost every time, and it is the only
/// thing a slider in the GUI can write. Four is what somebody wants when the
/// taskbar is on one edge. Both are the same setting, so both are the same
/// key rather than two that can disagree.
/// </para>
/// <para>
/// Written back as one number when all four are equal, so a slider round-trips
/// to what it wrote rather than to an array it would then have to collapse
/// again.
/// </para>
/// </remarks>
public sealed class OuterGapsConverter : JsonConverter<int[]>
{
    public override int[] Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number)
        {
            int all = reader.GetInt32();
            return [all, all, all, all];
        }

        if (reader.TokenType != JsonTokenType.StartArray)
        {
            throw new JsonException("gaps.outer is a number, or four of them (top, right, bottom, left)");
        }

        var sides = new List<int>(4);
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType != JsonTokenType.Number)
            {
                throw new JsonException("gaps.outer holds numbers");
            }

            sides.Add(reader.GetInt32());
        }

        // Length is the validator's business: it can say which file and which
        // line, and a throw here would take the whole configuration with it.
        return [.. sides];
    }

    public override void Write(Utf8JsonWriter writer, int[] value, JsonSerializerOptions options)
    {
        if (value.Length == 4 && value[0] == value[1] && value[1] == value[2] && value[2] == value[3])
        {
            writer.WriteNumberValue(value[0]);
            return;
        }

        writer.WriteStartArray();
        foreach (int side in value)
        {
            writer.WriteNumberValue(side);
        }

        writer.WriteEndArray();
    }
}
