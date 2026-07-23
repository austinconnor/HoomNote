using HoomNote.Core.Documents;

namespace HoomNote.Core.Services;

public sealed record DocumentSummary(
    Guid Id,
    string Title,
    DocumentKind Kind,
    int PageCount,
    DateTimeOffset UpdatedAt)
{
    public string Color { get; init; } = "#4BAEFF";
}

public sealed record SearchResult(
    Guid DocumentId,
    Guid? PageId,
    string DocumentTitle,
    string PageTitle,
    string Snippet,
    string Source);

public interface IDocumentRepository : IAsyncDisposable
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DocumentSummary>> ListAsync(CancellationToken cancellationToken = default);
    Task<HoomNoteDocument?> LoadAsync(Guid documentId, CancellationToken cancellationToken = default);
    Task SaveAsync(HoomNoteDocument document, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid documentId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SearchResult>> SearchAsync(string query, CancellationToken cancellationToken = default);
}

public interface IAssetStore
{
    Task<string> AddAsync(Stream source, string extension, CancellationToken cancellationToken = default);
    Task<Stream> OpenReadAsync(string assetHash, CancellationToken cancellationToken = default);
    string GetPath(string assetHash);
}

public interface IPackageService
{
    Task ExportAsync(HoomNoteDocument document, string destinationPath, CancellationToken cancellationToken = default);
    Task<HoomNoteDocument> ImportAsync(string packagePath, CancellationToken cancellationToken = default);
}

public interface IHandwritingRecognitionService
{
    Task<HandwritingRecognitionResult> RecognizeAsync(IReadOnlyList<InkStrokeObject> strokes, string languageTag,
        CancellationToken cancellationToken = default);
}

public sealed record HandwritingRecognitionResult(
    string Text,
    IReadOnlyList<RecognizedTextRegion> Regions)
{
    public static HandwritingRecognitionResult Empty { get; } = new(string.Empty, []);
}

public sealed record ImportRequest(
    string SourcePath,
    IReadOnlyList<int>? PageIndexes = null,
    bool ReplaceCurrentPages = false,
    double Margin = 0,
    int RotationDegrees = 0);

public sealed record ImportResult(
    string AssetHash,
    string DisplayName,
    IReadOnlyList<NotePage> Pages,
    IReadOnlyList<string> Warnings);

public interface IDocumentImportService
{
    Task<ImportResult> ImportAsync(ImportRequest request, CancellationToken cancellationToken = default);
}

public enum VectorExportFormat
{
    Pdf,
    Svg
}

public sealed record ExportResult(string DestinationPath, IReadOnlyList<string> Warnings);

public interface IVectorExportService
{
    Task<ExportResult> ExportAsync(HoomNoteDocument document, string destinationPath,
        VectorExportFormat format, CancellationToken cancellationToken = default);
}
