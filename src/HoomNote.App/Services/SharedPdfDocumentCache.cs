using Windows.Data.Pdf;
using Windows.Storage;

namespace HoomNote_App.Services;

/// <summary>
/// Small serialized cache for Windows PDF documents. Parsing a source file is substantially more
/// expensive than opening a page; keeping two recent documents covers navigation plus background OCR.
/// </summary>
public sealed class SharedPdfDocumentCache
{
    private const int EntryLimit = 2;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, PdfDocument> _documents = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _lru = [];

    public async Task<T?> UsePageAsync<T>(string path, int pageIndex, Func<PdfPage, Task<T>> use,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!_documents.TryGetValue(path, out var document))
            {
                var file = await StorageFile.GetFileFromPathAsync(path);
                document = await PdfDocument.LoadFromFileAsync(file);
                _documents[path] = document;
            }
            Touch(path);
            while (_documents.Count > EntryLimit && _lru.Last is { } oldest)
            {
                _lru.RemoveLast();
                _documents.Remove(oldest.Value);
            }
            if (pageIndex < 0 || (uint)pageIndex >= document.PageCount) return default;
            using var page = document.GetPage((uint)pageIndex);
            return await use(page);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void Touch(string path)
    {
        _lru.Remove(path);
        _lru.AddFirst(path);
    }
}
