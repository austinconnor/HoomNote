using System.Text.Json;
using System.Text.Json.Serialization;

namespace HoomNote.Infrastructure.Serialization;

public static class HoomNoteJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            AllowOutOfOrderMetadataProperties = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false
        };
        options.Converters.Add(new InkPointJsonConverter());
        options.TypeInfoResolverChain.Insert(0, new HoomNoteJsonContext(options));
        return options;
    }
}
