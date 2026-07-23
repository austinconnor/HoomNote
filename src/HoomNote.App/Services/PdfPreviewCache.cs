using Microsoft.Graphics.Canvas;
using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;

namespace HoomNote_App.Services;

public sealed class PdfPreviewCache : IDisposable
{
    private sealed record CacheEntry(CanvasBitmap Bitmap, long ByteSize);

    private const long CacheBudget = 24L * 1024 * 1024;
    private const int CacheEntryLimit = 2;
    private const double PreviewLongEdge = 1_400d;
    private readonly object _gate = new();
    private readonly Dictionary<(string Path, int Page), CacheEntry> _cache = [];
    private readonly LinkedList<(string Path, int Page)> _lru = [];
    private readonly HashSet<(string Path, int Page)> _loading = [];
    private long _cachedBytes;
    private bool _disposed;

    public event EventHandler? PreviewAvailable;

    public CanvasBitmap? TryGet(string path, int pageIndex)
    {
        lock (_gate)
        {
            var key = (path, pageIndex);
            if (!_cache.TryGetValue(key, out var entry)) return null;
            Touch(key);
            return entry.Bitmap;
        }
    }

    public async Task EnsureLoadedAsync(string path, int pageIndex, CancellationToken cancellationToken = default)
    {
        var key = (path, pageIndex);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_cache.ContainsKey(key) || !_loading.Add(key)) return;
        }
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            var document = await PdfDocument.LoadFromFileAsync(file);
            if ((uint)pageIndex >= document.PageCount) return;
            using var page = document.GetPage((uint)pageIndex);
            using var stream = new InMemoryRandomAccessStream();
            var scale = Math.Min(2d, PreviewLongEdge / Math.Max(page.Size.Width, page.Size.Height));
            await page.RenderToStreamAsync(stream, new PdfPageRenderOptions
            {
                DestinationWidth = (uint)Math.Max(1, page.Size.Width * scale),
                DestinationHeight = (uint)Math.Max(1, page.Size.Height * scale)
            });
            stream.Seek(0);
            cancellationToken.ThrowIfCancellationRequested();
            var bitmap = await CanvasBitmap.LoadAsync(CanvasDevice.GetSharedDevice(), stream);
            lock (_gate)
            {
                if (_disposed)
                {
                    bitmap.Dispose();
                    return;
                }
                var byteSize = Math.Max(1L,
                    (long)bitmap.SizeInPixels.Width * bitmap.SizeInPixels.Height * 4L);
                if (_cache.Remove(key, out var previous))
                {
                    _cachedBytes -= previous.ByteSize;
                    previous.Bitmap.Dispose();
                    _lru.Remove(key);
                }
                _cache[key] = new CacheEntry(bitmap, byteSize);
                _cachedBytes += byteSize;
                _lru.AddFirst(key);
                EvictToBudget(CacheBudget, CacheEntryLimit);
            }
            PreviewAvailable?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            lock (_gate) _loading.Remove(key);
        }
    }

    public void Trim(long targetBytes = 16L * 1024 * 1024)
    {
        lock (_gate)
        {
            if (_disposed) return;
            EvictToBudget(Math.Max(0, targetBytes), 1);
        }
    }

    private void Touch((string Path, int Page) key)
    {
        _lru.Remove(key);
        _lru.AddFirst(key);
    }

    private void EvictToBudget(long targetBytes, int entryLimit)
    {
        while ((_cachedBytes > targetBytes || _cache.Count > entryLimit) && _lru.Count > 0)
        {
            var key = _lru.Last!.Value;
            _lru.RemoveLast();
            if (!_cache.Remove(key, out var entry)) continue;
            _cachedBytes -= entry.ByteSize;
            entry.Bitmap.Dispose();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var entry in _cache.Values) entry.Bitmap.Dispose();
            _cache.Clear();
            _lru.Clear();
            _cachedBytes = 0;
            _loading.Clear();
        }
    }
}
