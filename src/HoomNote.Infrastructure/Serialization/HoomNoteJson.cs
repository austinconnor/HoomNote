using System.Text.Json;
using System.Text.Json.Serialization;

namespace HoomNote.Infrastructure.Serialization;

public static class HoomNoteJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };
}

