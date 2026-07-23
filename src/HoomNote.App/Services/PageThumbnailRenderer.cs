using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using HoomNote.Canvas.Geometry;
using HoomNote.Core.Documents;
using HoomNote.Core.Services;
using Windows.Data.Pdf;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.UI;

namespace HoomNote_App.Services;

/// <summary>
/// Produces small, self-contained page previews on Win2D's software device so the
/// interactive canvas never pays the cost of rendering thumbnail geometry.
/// </summary>
public sealed class PageThumbnailRenderer(IAssetStore assetStore)
{
    private const int AssetLongEdge = 512;
    private readonly SemaphoreSlim _renderGate = new(1, 1);

    public async Task<byte[]> RenderAsync(NotePage page, int pixelWidth, int pixelHeight,
        CancellationToken cancellationToken = default)
    {
        var snapshot = page with { Objects = page.Objects.ToList() };
        await _renderGate.WaitAsync(cancellationToken);
        try
        {
            return await Task.Run(() => RenderCoreAsync(snapshot, pixelWidth, pixelHeight, cancellationToken),
                cancellationToken);
        }
        finally
        {
            _renderGate.Release();
        }
    }

    private async Task<byte[]> RenderCoreAsync(NotePage page, int pixelWidth, int pixelHeight,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var device = CanvasDevice.GetSharedDevice(forceSoftwareRenderer: true);
        CanvasBitmap? importedPage = null;
        var imageBitmaps = new Dictionary<string, CanvasBitmap>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (page.ImportedLayer is { IsVisible: true } imported && !string.IsNullOrWhiteSpace(imported.AssetHash))
                importedPage = await TryLoadPdfPageAsync(device, assetStore.GetPath(imported.AssetHash),
                    imported.SourcePageIndex, cancellationToken);

            foreach (var assetHash in page.Objects.OfType<ImageObject>()
                         .Where(image => !image.IsHidden && !string.IsNullOrWhiteSpace(image.AssetHash))
                         .Select(image => image.AssetHash).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var bitmap = await TryLoadImageAsync(device, assetStore.GetPath(assetHash), cancellationToken);
                if (bitmap is not null) imageBitmaps[assetHash] = bitmap;
            }

            using var target = new CanvasRenderTarget(device, Math.Max(1, pixelWidth), Math.Max(1, pixelHeight), 96);
            using (var session = target.CreateDrawingSession())
            using (var roundStyle = new CanvasStrokeStyle
                   {
                       StartCap = CanvasCapStyle.Round,
                       EndCap = CanvasCapStyle.Round,
                       LineJoin = CanvasLineJoin.Round
                   })
            {
                session.Clear(Color.FromArgb(255, 5, 5, 6));
                var scale = (float)Math.Min(pixelWidth / Math.Max(1d, page.Size.Width),
                    pixelHeight / Math.Max(1d, page.Size.Height));
                var offsetX = (pixelWidth - page.Size.Width * scale) / 2f;
                var offsetY = (pixelHeight - page.Size.Height * scale) / 2f;
                session.Transform = Matrix3x2.CreateScale(scale) *
                                    Matrix3x2.CreateTranslation((float)offsetX, (float)offsetY);

                DrawBackground(session, page);
                DrawImportedPage(session, page, importedPage);
                // Page.Objects is kept in z-order by document commands and persistence. Avoid
                // sorting dense pages again for every thumbnail refresh.
                foreach (var canvasObject in page.Objects)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (canvasObject.IsHidden) continue;
                    DrawObject(session, canvasObject, imageBitmaps, roundStyle);
                }
            }

            using var stream = new InMemoryRandomAccessStream();
            await target.SaveAsync(stream, CanvasBitmapFileFormat.Png);
            cancellationToken.ThrowIfCancellationRequested();
            stream.Seek(0);
            using var reader = new DataReader(stream.GetInputStreamAt(0));
            await reader.LoadAsync((uint)stream.Size);
            var bytes = new byte[checked((int)stream.Size)];
            reader.ReadBytes(bytes);
            return bytes;
        }
        finally
        {
            importedPage?.Dispose();
            foreach (var bitmap in imageBitmaps.Values) bitmap.Dispose();
        }
    }

    private static void DrawBackground(CanvasDrawingSession session, NotePage page)
    {
        var paper = ParseColor(page.Template.PaperColor);
        var line = ParseColor(page.Template.LineColor);
        session.FillRectangle(0, 0, (float)page.Size.Width, (float)page.Size.Height, paper);
        var spacing = Math.Max(4, page.Template.Spacing);
        switch (page.Template.Kind)
        {
            case PageTemplateKind.Lined:
                for (var y = page.Template.Margin; y < page.Size.Height; y += spacing)
                    session.DrawLine((float)page.Template.Margin, (float)y,
                        (float)(page.Size.Width - page.Template.Margin), (float)y, line,
                        (float)page.Template.LineWidth);
                break;
            case PageTemplateKind.Dotted:
                for (var x = page.Template.Margin; x < page.Size.Width - page.Template.Margin; x += spacing)
                for (var y = page.Template.Margin; y < page.Size.Height - page.Template.Margin; y += spacing)
                    session.FillCircle((float)x, (float)y, 1.1f, line);
                break;
            case PageTemplateKind.SquareGrid:
            case PageTemplateKind.Graph:
                var graph = page.Template.Kind == PageTemplateKind.Graph;
                var count = 0;
                for (var x = 0d; x <= page.Size.Width; x += spacing, count++)
                    session.DrawLine((float)x, 0, (float)x, (float)page.Size.Height, line,
                        graph && count % 5 == 0 ? 1.4f : (float)page.Template.LineWidth);
                count = 0;
                for (var y = 0d; y <= page.Size.Height; y += spacing, count++)
                    session.DrawLine(0, (float)y, (float)page.Size.Width, (float)y, line,
                        graph && count % 5 == 0 ? 1.4f : (float)page.Template.LineWidth);
                break;
        }
    }

    private static void DrawImportedPage(CanvasDrawingSession session, NotePage page, CanvasBitmap? bitmap)
    {
        if (bitmap is null || page.ImportedLayer is not { IsVisible: true } layer) return;
        var previous = session.Transform;
        session.Transform = layer.Transform.ToMatrix() * previous;
        session.DrawImage(bitmap, new Rect(0, 0, page.Size.Width, page.Size.Height));
        session.Transform = previous;
    }

    private static void DrawObject(CanvasDrawingSession session, CanvasObject canvasObject,
        IReadOnlyDictionary<string, CanvasBitmap> images, CanvasStrokeStyle roundStyle)
    {
        var previous = session.Transform;
        session.Transform = canvasObject.Transform.ToMatrix() * previous;
        switch (canvasObject)
        {
            case InkStrokeObject ink:
                DrawInk(session, ink, roundStyle);
                break;
            case RichTextObject text:
                DrawText(session, text);
                break;
            case ShapeObject shape:
                DrawShape(session, shape, roundStyle);
                break;
            case ImageObject image when images.TryGetValue(image.AssetHash, out var bitmap):
                session.DrawImage(bitmap, new Rect(image.Bounds.X, image.Bounds.Y,
                    image.Bounds.Width, image.Bounds.Height));
                break;
        }
        session.Transform = previous;
    }

    private static void DrawInk(CanvasDrawingSession session, InkStrokeObject stroke, CanvasStrokeStyle roundStyle)
    {
        if (stroke.Points.Count == 0) return;
        var style = stroke.Style.Normalize();
        var color = ParseColor(style.Color, style.Opacity);
        if (StrokeOutlineBuilder.UsesCenterlineStroke(stroke))
        {
            var points = StrokeOutlineBuilder.FitCenterline(stroke);
            var width = StrokeOutlineBuilder.VectorCenterlineWidth(style);
            if (points.Count == 1)
            {
                session.FillCircle(points[0].Position.ToVector2(), width / 2f, color);
                return;
            }
            for (var index = 1; index < points.Count; index++)
                session.DrawLine(points[index - 1].Position.ToVector2(), points[index].Position.ToVector2(),
                    color, width, roundStyle);
            return;
        }

        var outline = StrokeOutlineBuilder.Build(stroke.Points, style);
        if (outline.Contour.Count < 3)
        {
            var point = stroke.Points[0];
            session.FillCircle(point.Position.ToVector2(),
                StrokeOutlineBuilder.EffectiveWidth(point, style) / 2f, color);
            return;
        }
        using var geometry = CreateWindingGeometry(session, outline.Contour);
        session.FillGeometry(geometry, color);
    }

    private static CanvasGeometry CreateWindingGeometry(ICanvasResourceCreator creator, IReadOnlyList<PointD> points)
    {
        using var builder = new CanvasPathBuilder(creator);
        builder.SetFilledRegionDetermination(CanvasFilledRegionDetermination.Winding);
        builder.BeginFigure(points[0].ToVector2());
        for (var index = 1; index < points.Count; index++) builder.AddLine(points[index].ToVector2());
        builder.EndFigure(CanvasFigureLoop.Closed);
        return CanvasGeometry.CreatePath(builder);
    }

    private static void DrawText(CanvasDrawingSession session, RichTextObject text)
    {
        var paragraph = text.Content.Paragraphs.FirstOrDefault();
        var run = paragraph?.Runs.FirstOrDefault();
        var textSize = paragraph?.Kind switch
        {
            ParagraphKind.Heading1 => text.Content.FontSize * 1.65f,
            ParagraphKind.Heading2 => text.Content.FontSize * 1.3f,
            _ => text.Content.FontSize
        };
        using var format = new CanvasTextFormat
        {
            FontFamily = text.Content.FontFamily,
            FontSize = textSize,
            FontWeight = run?.Bold == true ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.Normal,
            FontStyle = run?.Italic == true ? Windows.UI.Text.FontStyle.Italic : Windows.UI.Text.FontStyle.Normal,
            WordWrapping = CanvasWordWrapping.Wrap
        };
        session.DrawText(text.Content.PlainText,
            new Rect(text.Bounds.X, text.Bounds.Y, text.Bounds.Width, text.Bounds.Height),
            ParseColor(run?.Color ?? "#F4F7FB"), format);
    }

    private static void DrawShape(CanvasDrawingSession session, ShapeObject shape, CanvasStrokeStyle roundStyle)
    {
        var color = ParseColor(shape.StrokeColor);
        var bounds = shape.Bounds;
        if (shape.FillColor is { Length: > 0 } fill)
        {
            var fillColor = ParseColor(fill);
            if (shape.Shape == ShapeKind.Ellipse)
                session.FillEllipse((float)bounds.Center.X, (float)bounds.Center.Y,
                    (float)(bounds.Width / 2), (float)(bounds.Height / 2), fillColor);
            else if (shape.Shape is ShapeKind.Rectangle or ShapeKind.RoundedRectangle)
                session.FillRectangle((float)bounds.X, (float)bounds.Y, (float)bounds.Width,
                    (float)bounds.Height, fillColor);
        }
        switch (shape.Shape)
        {
            case ShapeKind.Ellipse:
                session.DrawEllipse((float)bounds.Center.X, (float)bounds.Center.Y,
                    (float)(bounds.Width / 2), (float)(bounds.Height / 2), color, shape.StrokeWidth);
                break;
            case ShapeKind.RoundedRectangle:
                session.DrawRoundedRectangle((float)bounds.X, (float)bounds.Y, (float)bounds.Width,
                    (float)bounds.Height, 12, 12, color, shape.StrokeWidth);
                break;
            case ShapeKind.Line:
                var lineStart = shape.StartPoint ?? new PointD(bounds.Left, bounds.Top);
                var lineEnd = shape.EndPoint ?? new PointD(bounds.Right, bounds.Bottom);
                session.DrawLine(lineStart.ToVector2(), lineEnd.ToVector2(), color, shape.StrokeWidth, roundStyle);
                break;
            case ShapeKind.Arrow:
                var arrowStart = shape.StartPoint ?? new PointD(bounds.Left, bounds.Top);
                var arrowEnd = shape.EndPoint ?? new PointD(bounds.Right, bounds.Bottom);
                session.DrawLine(arrowStart.ToVector2(), arrowEnd.ToVector2(), color, shape.StrokeWidth, roundStyle);
                var angle = Math.Atan2(arrowEnd.Y - arrowStart.Y, arrowEnd.X - arrowStart.X);
                var length = Vector2.Distance(arrowStart.ToVector2(), arrowEnd.ToVector2());
                var head = Math.Clamp(length * 0.2, 9, 24);
                session.DrawLine((float)arrowEnd.X, (float)arrowEnd.Y,
                    (float)(arrowEnd.X - Math.Cos(angle - 0.55) * head),
                    (float)(arrowEnd.Y - Math.Sin(angle - 0.55) * head), color, shape.StrokeWidth);
                session.DrawLine((float)arrowEnd.X, (float)arrowEnd.Y,
                    (float)(arrowEnd.X - Math.Cos(angle + 0.55) * head),
                    (float)(arrowEnd.Y - Math.Sin(angle + 0.55) * head), color, shape.StrokeWidth);
                break;
            case ShapeKind.Triangle:
                session.DrawLine((float)bounds.Center.X, (float)bounds.Top, (float)bounds.Right,
                    (float)bounds.Bottom, color, shape.StrokeWidth);
                session.DrawLine((float)bounds.Right, (float)bounds.Bottom, (float)bounds.Left,
                    (float)bounds.Bottom, color, shape.StrokeWidth);
                session.DrawLine((float)bounds.Left, (float)bounds.Bottom, (float)bounds.Center.X,
                    (float)bounds.Top, color, shape.StrokeWidth);
                break;
            case ShapeKind.Diamond:
                var vertices = new[]
                {
                    bounds.Center with { Y = bounds.Top }, bounds.Center with { X = bounds.Right },
                    bounds.Center with { Y = bounds.Bottom }, bounds.Center with { X = bounds.Left }
                };
                for (var index = 0; index < vertices.Length; index++)
                {
                    var next = vertices[(index + 1) % vertices.Length];
                    session.DrawLine(vertices[index].ToVector2(), next.ToVector2(), color, shape.StrokeWidth);
                }
                break;
            default:
                session.DrawRectangle((float)bounds.X, (float)bounds.Y, (float)bounds.Width,
                    (float)bounds.Height, color, shape.StrokeWidth);
                break;
        }
    }

    private static async Task<CanvasBitmap?> TryLoadPdfPageAsync(CanvasDevice device, string path, int pageIndex,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = await StorageFile.GetFileFromPathAsync(path);
            var document = await PdfDocument.LoadFromFileAsync(file);
            if (pageIndex < 0 || (uint)pageIndex >= document.PageCount) return null;
            using var page = document.GetPage((uint)pageIndex);
            using var stream = new InMemoryRandomAccessStream();
            var scale = Math.Min(1d, AssetLongEdge / Math.Max(page.Size.Width, page.Size.Height));
            await page.RenderToStreamAsync(stream, new PdfPageRenderOptions
            {
                DestinationWidth = (uint)Math.Max(1, page.Size.Width * scale),
                DestinationHeight = (uint)Math.Max(1, page.Size.Height * scale)
            });
            cancellationToken.ThrowIfCancellationRequested();
            stream.Seek(0);
            return await CanvasBitmap.LoadAsync(device, stream);
        }
        catch when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    private static async Task<CanvasBitmap?> TryLoadImageAsync(CanvasDevice device, string path,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = await StorageFile.GetFileFromPathAsync(path);
            using var stream = await file.OpenAsync(FileAccessMode.Read);
            var decoder = await BitmapDecoder.CreateAsync(stream);
            var scale = Math.Min(1d, AssetLongEdge / (double)Math.Max(decoder.PixelWidth, decoder.PixelHeight));
            var transform = new BitmapTransform
            {
                ScaledWidth = (uint)Math.Max(1, decoder.PixelWidth * scale),
                ScaledHeight = (uint)Math.Max(1, decoder.PixelHeight * scale)
            };
            using var softwareBitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied, transform, ExifOrientationMode.RespectExifOrientation,
                ColorManagementMode.ColorManageToSRgb);
            cancellationToken.ThrowIfCancellationRequested();
            return CanvasBitmap.CreateFromSoftwareBitmap(device, softwareBitmap);
        }
        catch when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    private static Color ParseColor(string value, float opacity = 1)
    {
        var hex = value.Trim().TrimStart('#');
        if (hex.Length == 8)
        {
            var alpha = Convert.ToByte(hex[..2], 16);
            return Color.FromArgb((byte)(alpha * Math.Clamp(opacity, 0, 1)),
                Convert.ToByte(hex.Substring(2, 2), 16), Convert.ToByte(hex.Substring(4, 2), 16),
                Convert.ToByte(hex.Substring(6, 2), 16));
        }
        if (hex.Length != 6) return Color.FromArgb((byte)(255 * Math.Clamp(opacity, 0, 1)), 32, 36, 45);
        return Color.FromArgb((byte)(255 * Math.Clamp(opacity, 0, 1)), Convert.ToByte(hex[..2], 16),
            Convert.ToByte(hex.Substring(2, 2), 16), Convert.ToByte(hex.Substring(4, 2), 16));
    }
}
