using HoomNote.Core.Documents;

namespace HoomNote.Infrastructure.Import;

public static class PdfSemanticTextExtractor
{
    private const int CacheLimit = 2;
    private static readonly object Gate = new();
    private static readonly Dictionary<string, UglyToad.PdfPig.PdfDocument> Documents =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly LinkedList<string> Lru = [];

    public static IReadOnlyList<RecognizedTextRegion> ExtractPage(
        string path,
        int pageIndex,
        SizeD destinationSize,
        Transform2D transform,
        CancellationToken cancellationToken = default) => WithDocument(path, document =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (pageIndex < 0 || pageIndex >= document.NumberOfPages) return [];
        return ExtractRegions(document.GetPage(pageIndex + 1), destinationSize, transform, cancellationToken);
    });

    public static IReadOnlyList<IReadOnlyList<RecognizedTextRegion>> ExtractDocument(
        string path,
        IReadOnlyList<SizeD> destinationSizes,
        CancellationToken cancellationToken = default) => WithDocument(path, document =>
    {
        var count = Math.Min(destinationSizes.Count, document.NumberOfPages);
        var results = new List<IReadOnlyList<RecognizedTextRegion>>(count);
        for (var index = 0; index < count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(ExtractRegions(document.GetPage(index + 1), destinationSizes[index],
                Transform2D.Identity, cancellationToken));
        }
        return results;
    });

    private static IReadOnlyList<RecognizedTextRegion> ExtractRegions(
        UglyToad.PdfPig.Content.Page page,
        SizeD destinationSize,
        Transform2D transform,
        CancellationToken cancellationToken)
    {
        var sourceWidth = Convert.ToDouble(page.Width);
        var sourceHeight = Convert.ToDouble(page.Height);
        if (sourceWidth <= 0 || sourceHeight <= 0) return [];
        var regions = new List<RecognizedTextRegion>();
        foreach (var word in page.GetWords())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var text = word.Text;
            if (string.IsNullOrWhiteSpace(text)) continue;
            var box = word.BoundingBox;
            var sourceBounds = new RectD(
                Convert.ToDouble(box.Left) / sourceWidth * destinationSize.Width,
                (sourceHeight - Convert.ToDouble(box.Top)) / sourceHeight * destinationSize.Height,
                (Convert.ToDouble(box.Right) - Convert.ToDouble(box.Left)) / sourceWidth * destinationSize.Width,
                (Convert.ToDouble(box.Top) - Convert.ToDouble(box.Bottom)) / sourceHeight * destinationSize.Height);
            if (!sourceBounds.IsFinite || sourceBounds.Width <= 0 || sourceBounds.Height <= 0) continue;
            regions.Add(new RecognizedTextRegion
            {
                Text = text,
                Bounds = RectD.FromPoints(sourceBounds.Corners().Select(transform.Apply)),
                Source = "Pdf"
            });
        }
        return regions;
    }

    private static T WithDocument<T>(string path, Func<UglyToad.PdfPig.PdfDocument, T> use)
    {
        lock (Gate)
        {
            if (!Documents.TryGetValue(path, out var document))
            {
                document = UglyToad.PdfPig.PdfDocument.Open(path);
                Documents[path] = document;
            }
            Lru.Remove(path);
            Lru.AddFirst(path);
            while (Documents.Count > CacheLimit && Lru.Last is { } oldest)
            {
                Lru.RemoveLast();
                if (Documents.Remove(oldest.Value, out var evicted)) evicted.Dispose();
            }
            return use(document);
        }
    }
}
