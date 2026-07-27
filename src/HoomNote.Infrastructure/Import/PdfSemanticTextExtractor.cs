using HoomNote.Core.Documents;

namespace HoomNote.Infrastructure.Import;

public static class PdfSemanticTextExtractor
{
    public static IReadOnlyList<RecognizedTextRegion> ExtractPage(
        string path,
        int pageIndex,
        SizeD destinationSize,
        Transform2D transform,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var document = UglyToad.PdfPig.PdfDocument.Open(path);
        if (pageIndex < 0 || pageIndex >= document.NumberOfPages) return [];
        var page = document.GetPage(pageIndex + 1);
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
            var left = Convert.ToDouble(box.Left);
            var right = Convert.ToDouble(box.Right);
            var top = Convert.ToDouble(box.Top);
            var bottom = Convert.ToDouble(box.Bottom);
            var sourceBounds = new RectD(
                left / sourceWidth * destinationSize.Width,
                (sourceHeight - top) / sourceHeight * destinationSize.Height,
                (right - left) / sourceWidth * destinationSize.Width,
                (top - bottom) / sourceHeight * destinationSize.Height);
            if (!double.IsFinite(sourceBounds.X) || !double.IsFinite(sourceBounds.Y) ||
                !double.IsFinite(sourceBounds.Width) || !double.IsFinite(sourceBounds.Height) ||
                sourceBounds.Width <= 0 || sourceBounds.Height <= 0) continue;
            regions.Add(new RecognizedTextRegion
            {
                Text = text,
                Bounds = TransformBounds(sourceBounds, transform),
                Source = "Pdf"
            });
        }
        return regions;
    }

    private static RectD TransformBounds(RectD bounds, Transform2D transform) =>
        RectD.FromPoints([
            transform.Apply(new PointD(bounds.Left, bounds.Top)),
            transform.Apply(new PointD(bounds.Right, bounds.Top)),
            transform.Apply(new PointD(bounds.Right, bounds.Bottom)),
            transform.Apply(new PointD(bounds.Left, bounds.Bottom))
        ]);
}
