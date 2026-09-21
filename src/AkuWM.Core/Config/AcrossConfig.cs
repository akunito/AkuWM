using System.Text.Json;
using System.Text.Json.Serialization;
using AkuWM.Core.Layout;

namespace AkuWM.Core.Config;

/// <summary>
/// How geometry is carried between screens, by what is being moved and by who
/// moved it.
/// </summary>
/// <remarks>
/// <para>
/// Four situations, because Diego asked for them separately (2026-09-21) and
/// they really are different. A window the person DRAGGED already has its
/// position chosen -- only its size is in question. One a COMMAND moved has
/// neither. And a TILED window's size on screen belongs to the layout whatever
/// this says; what the mode decides for it is the rectangle it remembers for
/// when it floats again, which is the thing that otherwise arrives from
/// another screen at the wrong size.
/// </para>
/// <para>
/// Written either as one word for all four -- <c>"across_monitors":
/// "hybrid"</c> -- or as an object naming the ones that differ. Both, because
/// one word is what a person writes by hand and the object is what a GUI
/// writes back.
/// </para>
/// </remarks>
[JsonConverter(typeof(AcrossConfigConverter))]
public sealed class AcrossConfig
{
    /// <summary>The value the four fall back to.</summary>
    public string? All { get; set; }

    public string? FloatDrag { get; set; }

    public string? FloatMove { get; set; }

    public string? TileDrag { get; set; }

    public string? TileMove { get; set; }

    public AcrossMode Mode(bool tiling, bool dragged)
    {
        string? name = (tiling, dragged) switch
        {
            (true, true) => TileDrag,
            (true, false) => TileMove,
            (false, true) => FloatDrag,
            _ => FloatMove,
        };

        return AcrossMonitors.Parse(name ?? All);
    }

    /// <summary>Every value set here, for the validator.</summary>
    public IEnumerable<(string Path, string Value)> Named()
    {
        if (All is { Length: > 0 } all)
        {
            yield return ("layout.across_monitors", all);
        }

        if (FloatDrag is { Length: > 0 } fd)
        {
            yield return ("layout.across_monitors.float_drag", fd);
        }

        if (FloatMove is { Length: > 0 } fm)
        {
            yield return ("layout.across_monitors.float_move", fm);
        }

        if (TileDrag is { Length: > 0 } td)
        {
            yield return ("layout.across_monitors.tile_drag", td);
        }

        if (TileMove is { Length: > 0 } tm)
        {
            yield return ("layout.across_monitors.tile_move", tm);
        }
    }
}

/// <summary>Reads the one-word form as well as the object.</summary>
internal sealed class AcrossConfigConverter : JsonConverter<AcrossConfig>
{
    public override AcrossConfig? Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        if (reader.TokenType == JsonTokenType.String)
        {
            return new AcrossConfig { All = reader.GetString() };
        }

        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("across_monitors is either a word or an object");
        }

        var across = new AcrossConfig();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                continue;
            }

            string name = reader.GetString() ?? string.Empty;
            reader.Read();
            string? value = reader.TokenType == JsonTokenType.Null ? null : reader.GetString();

            switch (name.ToLowerInvariant())
            {
                case "all": across.All = value; break;
                case "float_drag" or "floatdrag": across.FloatDrag = value; break;
                case "float_move" or "floatmove": across.FloatMove = value; break;
                case "tile_drag" or "tiledrag": across.TileDrag = value; break;
                case "tile_move" or "tilemove": across.TileMove = value; break;
                default: break;
            }
        }

        return across;
    }

    public override void Write(Utf8JsonWriter writer, AcrossConfig value, JsonSerializerOptions options)
    {
        // Always the object: a file a GUI has written is one shape, and the
        // one-word form stays valid for a file a person writes.
        writer.WriteStartObject();
        Field(writer, "all", value.All);
        Field(writer, "float_drag", value.FloatDrag);
        Field(writer, "float_move", value.FloatMove);
        Field(writer, "tile_drag", value.TileDrag);
        Field(writer, "tile_move", value.TileMove);
        writer.WriteEndObject();
    }

    private static void Field(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is { Length: > 0 })
        {
            writer.WriteString(name, value);
        }
    }
}
