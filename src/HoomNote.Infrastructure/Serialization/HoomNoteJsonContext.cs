using System.Text.Json.Serialization;
using HoomNote.Core.Documents;
using HoomNote.Infrastructure.Storage;

namespace HoomNote.Infrastructure.Serialization;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(HoomNoteDocument))]
[JsonSerializable(typeof(NotePage))]
[JsonSerializable(typeof(CanvasObject))]
[JsonSerializable(typeof(IReadOnlyList<CanvasObject>))]
[JsonSerializable(typeof(List<CanvasObject>))]
[JsonSerializable(typeof(InkStrokeObject))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(List<NotebookSection>))]
[JsonSerializable(typeof(List<RecognizedTextRegion>))]
[JsonSerializable(typeof(DocumentSettings))]
[JsonSerializable(typeof(UserPreferences))]
internal sealed partial class HoomNoteJsonContext : JsonSerializerContext;
