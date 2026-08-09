using System.Text.Json;
using System.Text.Json.Serialization;
using HoomNote.Core.Documents;

namespace HoomNote.Infrastructure.Serialization;

/// <summary>
/// Writes compact positional ink samples while retaining support for the verbose object shape
/// used through HoomNote 0.7.x.
/// </summary>
public sealed class InkPointJsonConverter : JsonConverter<InkPoint>
{
    public override InkPoint Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.StartArray)
        {
            Span<double> values = stackalloc double[6];
            var count = 0;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                if (count < values.Length && reader.TokenType == JsonTokenType.Number)
                    values[count++] = reader.GetDouble();
                else
                    reader.Skip();
            }
            if (count < 2) throw new JsonException("An ink point requires X and Y coordinates.");
            return new InkPoint(values[0], values[1],
                count > 2 ? (float)values[2] : 0.5f,
                count > 3 ? (float)values[3] : 0,
                count > 4 ? (float)values[4] : 0,
                count > 5 ? checked((long)values[5]) : 0).Normalize();
        }

        if (reader.TokenType != JsonTokenType.StartObject) throw new JsonException("Invalid ink point.");
        double x = 0, y = 0;
        float pressure = 0.5f, tiltX = 0, tiltY = 0;
        long timestamp = 0;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName) continue;
            var property = reader.GetString();
            if (!reader.Read()) throw new JsonException();
            switch (property?.ToLowerInvariant())
            {
                case "x": x = reader.GetDouble(); break;
                case "y": y = reader.GetDouble(); break;
                case "pressure": pressure = reader.GetSingle(); break;
                case "tiltx": tiltX = reader.GetSingle(); break;
                case "tilty": tiltY = reader.GetSingle(); break;
                case "timestampmicroseconds": timestamp = reader.GetInt64(); break;
                default: reader.Skip(); break;
            }
        }
        return new InkPoint(x, y, pressure, tiltX, tiltY, timestamp).Normalize();
    }

    public override void Write(Utf8JsonWriter writer, InkPoint value, JsonSerializerOptions options)
    {
        value = value.Normalize();
        writer.WriteStartArray();
        writer.WriteNumberValue(value.X);
        writer.WriteNumberValue(value.Y);
        var last = value.TimestampMicroseconds != 0 ? 5 :
            value.TiltY != 0 ? 4 : value.TiltX != 0 ? 3 : value.Pressure != 0.5f ? 2 : 1;
        if (last >= 2) writer.WriteNumberValue(value.Pressure);
        if (last >= 3) writer.WriteNumberValue(value.TiltX);
        if (last >= 4) writer.WriteNumberValue(value.TiltY);
        if (last >= 5) writer.WriteNumberValue(value.TimestampMicroseconds);
        writer.WriteEndArray();
    }
}
