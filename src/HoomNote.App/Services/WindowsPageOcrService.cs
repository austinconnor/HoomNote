using HoomNote.Core.Documents;
using HoomNote.Core.Services;
using Microsoft.Graphics.Canvas;
using System.Numerics;
using Windows.Data.Pdf;
using Windows.Foundation;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage;
using Windows.Storage.Streams;

namespace HoomNote_App.Services;

public sealed record PageOcrResult(string Text, IReadOnlyList<RecognizedTextRegion> Regions);

public sealed class WindowsPageOcrService(IAssetStore assetStore)
{
    private sealed record RawOcrRegion(string Text, Rect Bounds);
    private sealed record OcrSnapshot(string Text, uint PixelWidth, uint PixelHeight,
        IReadOnlyList<RawOcrRegion> Regions);

    public async Task<PageOcrResult> RecognizePageAsync(NotePage page, IReadOnlyList<ImageObject> images,
        IReadOnlyList<InkStrokeObject> inkStrokes, string languageTag,
        CancellationToken cancellationToken = default)
    {
        var engine = CreateEngine(languageTag);
        if (engine is null) return new PageOcrResult(string.Empty, []);
        var results = new List<string>();
        var regions = new List<RecognizedTextRegion>();

        if (page.ImportedLayer is { } imported)
        {
            var snapshot = await RecognizePdfPageAsync(engine, assetStore.GetPath(imported.AssetHash),
                imported.SourcePageIndex, cancellationToken);
            AddSnapshot(snapshot, new RectD(0, 0, page.Size.Width, page.Size.Height),
                imported.Transform, results, regions);
        }

        foreach (var group in images.Where(image => !image.IsHidden && !string.IsNullOrWhiteSpace(image.AssetHash))
                     .GroupBy(image => image.AssetHash, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = await RecognizeImageAsync(engine, assetStore.GetPath(group.Key), cancellationToken);
            if (!string.IsNullOrWhiteSpace(snapshot.Text)) results.Add(snapshot.Text);
            foreach (var image in group)
                AddSnapshot(snapshot, image.Bounds, image.Transform, null, regions);
        }

        if (inkStrokes.Count > 0)
        {
            var snapshot = await RecognizeVectorInkRasterAsync(engine, page, inkStrokes, cancellationToken);
            AddSnapshot(snapshot, new RectD(0, 0, page.Size.Width, page.Size.Height),
                Transform2D.Identity, results, regions);
        }

        return new PageOcrResult(
            string.Join(Environment.NewLine, results.Distinct(StringComparer.OrdinalIgnoreCase)), regions);
    }

    private static void AddSnapshot(OcrSnapshot snapshot, RectD target, Transform2D transform,
        ICollection<string>? text, ICollection<RecognizedTextRegion> regions)
    {
        if (text is not null && !string.IsNullOrWhiteSpace(snapshot.Text)) text.Add(snapshot.Text);
        if (snapshot.PixelWidth == 0 || snapshot.PixelHeight == 0) return;
        var xScale = target.Width / snapshot.PixelWidth;
        var yScale = target.Height / snapshot.PixelHeight;
        foreach (var region in snapshot.Regions)
        {
            regions.Add(new RecognizedTextRegion
            {
                Text = region.Text,
                Bounds = TransformBounds(new RectD(target.X + region.Bounds.X * xScale,
                    target.Y + region.Bounds.Y * yScale,
                    region.Bounds.Width * xScale, region.Bounds.Height * yScale), transform)
            });
        }
    }

    private static RectD TransformBounds(RectD bounds, Transform2D transform) => RectD.FromPoints([
        transform.Apply(new PointD(bounds.Left, bounds.Top)),
        transform.Apply(new PointD(bounds.Right, bounds.Top)),
        transform.Apply(new PointD(bounds.Right, bounds.Bottom)),
        transform.Apply(new PointD(bounds.Left, bounds.Bottom))
    ]);

    private static OcrEngine? CreateEngine(string languageTag)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(languageTag))
            {
                var requested = OcrEngine.TryCreateFromLanguage(new Language(languageTag));
                if (requested is not null) return requested;
            }
        }
        catch { }
        return OcrEngine.TryCreateFromUserProfileLanguages();
    }

    private static async Task<OcrSnapshot> RecognizeImageAsync(OcrEngine engine, string path,
        CancellationToken cancellationToken)
    {
        var file = await StorageFile.GetFileFromPathAsync(path);
        using var stream = await file.OpenAsync(FileAccessMode.Read);
        return await RecognizeStreamAsync(engine, stream, cancellationToken);
    }

    private static async Task<OcrSnapshot> RecognizePdfPageAsync(OcrEngine engine, string path, int pageIndex,
        CancellationToken cancellationToken)
    {
        var file = await StorageFile.GetFileFromPathAsync(path);
        var document = await PdfDocument.LoadFromFileAsync(file);
        if (pageIndex < 0 || (uint)pageIndex >= document.PageCount)
            return new OcrSnapshot(string.Empty, 0, 0, []);
        using var page = document.GetPage((uint)pageIndex);
        using var stream = new InMemoryRandomAccessStream();
        var scale = Math.Min(2d, OcrEngine.MaxImageDimension / Math.Max(page.Size.Width, page.Size.Height));
        await page.RenderToStreamAsync(stream, new PdfPageRenderOptions
        {
            DestinationWidth = (uint)Math.Max(1, page.Size.Width * scale),
            DestinationHeight = (uint)Math.Max(1, page.Size.Height * scale)
        });
        stream.Seek(0);
        return await RecognizeStreamAsync(engine, stream, cancellationToken);
    }

    private static async Task<OcrSnapshot> RecognizeVectorInkRasterAsync(OcrEngine engine, NotePage page,
        IReadOnlyList<InkStrokeObject> strokes, CancellationToken cancellationToken)
    {
        var scale = Math.Min(2d, OcrEngine.MaxImageDimension / Math.Max(page.Size.Width, page.Size.Height));
        // OCR rendering uses Win2D's software device so background indexing cannot contend
        // with the interactive canvas for GPU time while the user is writing or panning.
        using var target = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(forceSoftwareRenderer: true),
            (float)Math.Max(1, page.Size.Width * scale), (float)Math.Max(1, page.Size.Height * scale), 96);
        using (var session = target.CreateDrawingSession())
        {
            session.Clear(Windows.UI.Color.FromArgb(255, 255, 255, 255));
            session.Transform = Matrix3x2.CreateScale((float)scale);
            var inkColor = Windows.UI.Color.FromArgb(255, 10, 10, 10);
            foreach (var stroke in strokes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (stroke.Points.Count == 0) continue;
                var transform = stroke.Transform.ToMatrix();
                var width = Math.Clamp(stroke.Style.Normalize().Width, 1.4f, 9f);
                var previous = Vector2.Transform(stroke.Points[0].Position.ToVector2(), transform);
                if (stroke.Points.Count == 1)
                {
                    session.FillCircle(previous, width / 2f, inkColor);
                    continue;
                }
                for (var index = 1; index < stroke.Points.Count; index++)
                {
                    var current = Vector2.Transform(stroke.Points[index].Position.ToVector2(), transform);
                    session.DrawLine(previous, current, inkColor, width);
                    previous = current;
                }
            }
        }
        using var stream = new InMemoryRandomAccessStream();
        await target.SaveAsync(stream, CanvasBitmapFileFormat.Png);
        stream.Seek(0);
        return await RecognizeStreamAsync(engine, stream, cancellationToken);
    }

    private static async Task<OcrSnapshot> RecognizeStreamAsync(OcrEngine engine, IRandomAccessStream stream,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var decoder = await BitmapDecoder.CreateAsync(stream);
        var scale = Math.Min(1d, OcrEngine.MaxImageDimension /
                                 (double)Math.Max(decoder.PixelWidth, decoder.PixelHeight));
        var transform = new BitmapTransform
        {
            ScaledWidth = (uint)Math.Max(1, decoder.PixelWidth * scale),
            ScaledHeight = (uint)Math.Max(1, decoder.PixelHeight * scale)
        };
        using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied, transform, ExifOrientationMode.RespectExifOrientation,
            ColorManagementMode.ColorManageToSRgb);
        cancellationToken.ThrowIfCancellationRequested();
        var result = await engine.RecognizeAsync(bitmap);
        var regions = result.Lines.SelectMany(line => line.Words)
            .Where(word => !string.IsNullOrWhiteSpace(word.Text))
            .Select(word => new RawOcrRegion(word.Text.Trim(), word.BoundingRect))
            .ToArray();
        return new OcrSnapshot(result.Text?.Trim() ?? string.Empty,
            transform.ScaledWidth, transform.ScaledHeight, regions);
    }
}
