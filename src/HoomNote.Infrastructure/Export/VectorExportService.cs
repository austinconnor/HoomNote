using System.Globalization;
using System.Text;
using HoomNote.Core.Documents;
using HoomNote.Core.Services;
using HoomNote.Canvas.Geometry;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace HoomNote.Infrastructure.Export;

public sealed class VectorExportService(IAssetStore assetStore) : IVectorExportService
{
    private const double PointsPerDip = 72d / 96d;

    public Task<ExportResult> ExportAsync(HoomNoteDocument document, string destinationPath,
        VectorExportFormat format, CancellationToken cancellationToken = default) => format switch
    {
        VectorExportFormat.Pdf => ExportPdfAsync(document, destinationPath, cancellationToken),
        VectorExportFormat.Svg => ExportSvgAsync(document, destinationPath, cancellationToken),
        _ => throw new ArgumentOutOfRangeException(nameof(format))
    };

    private Task<ExportResult> ExportPdfAsync(HoomNoteDocument document, string destinationPath,
        CancellationToken cancellationToken) => Task.Run(() =>
    {
        var warnings = new List<string>();
        using var output = new PdfDocument { Info = { Title = document.Title, Creator = "HoomNote" } };
        foreach (var notePage in document.Pages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = output.AddPage();
            page.Width = XUnit.FromPoint(notePage.Size.Width * PointsPerDip);
            page.Height = XUnit.FromPoint(notePage.Size.Height * PointsPerDip);
            using var graphics = XGraphics.FromPdfPage(page);
            DrawPdfTemplate(graphics, notePage);
            DrawImportedPdfPage(graphics, notePage, warnings);
            foreach (var canvasObject in notePage.Objects.Where(item => !item.IsHidden).OrderBy(item => item.ZIndex))
                DrawPdfObject(graphics, canvasObject, warnings);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destinationPath))!);
        output.Save(destinationPath);
        return new ExportResult(destinationPath, warnings);
    }, cancellationToken);

    private async Task<ExportResult> ExportSvgAsync(HoomNoteDocument document, string destinationPath,
        CancellationToken cancellationToken)
    {
        var warnings = new List<string>();
        var root = Path.GetDirectoryName(Path.GetFullPath(destinationPath))!;
        Directory.CreateDirectory(root);
        var baseName = Path.GetFileNameWithoutExtension(destinationPath);
        for (var index = 0; index < document.Pages.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = document.Pages[index];
            var path = document.Pages.Count == 1
                ? destinationPath
                : Path.Combine(root, $"{baseName}-page-{index + 1}.svg");
            var svg = BuildSvg(page, warnings);
            await File.WriteAllTextAsync(path, svg, new UTF8Encoding(false), cancellationToken);
        }

        return new ExportResult(destinationPath, warnings);
    }

    private static void DrawPdfTemplate(XGraphics graphics, NotePage page)
    {
        graphics.DrawRectangle(new XSolidBrush(ParseXColor(page.Template.PaperColor)), 0, 0,
            Dip(page.Size.Width), Dip(page.Size.Height));
        var pen = new XPen(ParseXColor(page.Template.LineColor), Math.Max(0.25, Dip(page.Template.LineWidth)));
        var spacing = Math.Max(4, page.Template.Spacing);
        if (page.Template.Kind == PageTemplateKind.Lined)
        {
            for (var y = page.Template.Margin; y < page.Size.Height; y += spacing)
                graphics.DrawLine(pen, Dip(page.Template.Margin), Dip(y), Dip(page.Size.Width - page.Template.Margin), Dip(y));
        }
        else if (page.Template.Kind is PageTemplateKind.SquareGrid or PageTemplateKind.Graph)
        {
            for (var x = 0d; x < page.Size.Width; x += spacing)
                graphics.DrawLine(pen, Dip(x), 0, Dip(x), Dip(page.Size.Height));
            for (var y = 0d; y < page.Size.Height; y += spacing)
                graphics.DrawLine(pen, 0, Dip(y), Dip(page.Size.Width), Dip(y));
        }
        else if (page.Template.Kind == PageTemplateKind.Dotted)
        {
            var brush = new XSolidBrush(ParseXColor(page.Template.LineColor));
            for (var x = page.Template.Margin; x < page.Size.Width - page.Template.Margin; x += spacing)
            for (var y = page.Template.Margin; y < page.Size.Height - page.Template.Margin; y += spacing)
                graphics.DrawEllipse(brush, Dip(x) - 0.6, Dip(y) - 0.6, 1.2, 1.2);
        }
    }

    private void DrawImportedPdfPage(XGraphics graphics, NotePage page, List<string> warnings)
    {
        var layer = page.ImportedLayer;
        if (layer is null || !layer.IsVisible) return;
        try
        {
            using var form = XPdfForm.FromFile(assetStore.GetPath(layer.AssetHash));
            form.PageNumber = layer.SourcePageIndex + 1;
            graphics.DrawImage(form, 0, 0, Dip(page.Size.Width), Dip(page.Size.Height));
        }
        catch (Exception exception)
        {
            warnings.Add($"{layer.SourceName}, page {layer.SourcePageIndex + 1}, could not be embedded: {exception.Message}");
        }
    }

    private void DrawPdfObject(XGraphics graphics, CanvasObject canvasObject, List<string> warnings)
    {
        switch (canvasObject)
        {
            case InkStrokeObject stroke:
                var color = ParseXColor(stroke.Style.Color, stroke.Style.Normalize().Opacity);
                if (StrokeOutlineBuilder.UsesCenterlineStroke(stroke))
                {
                    var centerline = StrokeOutlineBuilder.FitCenterline(stroke);
                    if (centerline.Count == 0) break;
                    var points = centerline.Select(point => canvasObject.Transform.Apply(point.Position))
                        .Select(point => new XPoint(Dip(point.X), Dip(point.Y))).ToArray();
                    if (points.Length == 1)
                    {
                        var radius = Dip(StrokeOutlineBuilder.VectorCenterlineWidth(stroke.Style)) / 2d;
                        graphics.DrawEllipse(new XSolidBrush(color), points[0].X - radius, points[0].Y - radius,
                            radius * 2, radius * 2);
                    }
                    else
                    {
                        var pen = new XPen(color, Dip(StrokeOutlineBuilder.VectorCenterlineWidth(stroke.Style)))
                        {
                            LineCap = XLineCap.Round,
                            LineJoin = XLineJoin.Round
                        };
                        graphics.DrawLines(pen, points);
                    }
                }
                else
                {
                    var outline = StrokeOutlineBuilder.Build(stroke.Points, stroke.Style);
                    if (outline.Contour.Count >= 3)
                    {
                        var polygon = outline.Contour.Select(point => canvasObject.Transform.Apply(point))
                            .Select(point => new XPoint(Dip(point.X), Dip(point.Y))).ToArray();
                        graphics.DrawPolygon(new XSolidBrush(color), polygon, XFillMode.Winding);
                    }
                }
                break;
            case ShapeObject shape:
                var topLeft = canvasObject.Transform.Apply(new PointD(shape.Bounds.Left, shape.Bounds.Top));
                var bottomRight = canvasObject.Transform.Apply(new PointD(shape.Bounds.Right, shape.Bounds.Bottom));
                var rect = Normalize(topLeft, bottomRight);
                var shapePen = new XPen(ParseXColor(shape.StrokeColor), Dip(shape.StrokeWidth));
                switch (shape.Shape)
                {
                    case ShapeKind.Ellipse:
                        graphics.DrawEllipse(shapePen, Dip(rect.X), Dip(rect.Y), Dip(rect.Width), Dip(rect.Height));
                        break;
                    case ShapeKind.Line:
                    case ShapeKind.Arrow:
                        var directedStart = canvasObject.Transform.Apply(shape.StartPoint ??
                            new PointD(shape.Bounds.Left, shape.Bounds.Top));
                        var directedEnd = canvasObject.Transform.Apply(shape.EndPoint ??
                            new PointD(shape.Bounds.Right, shape.Bounds.Bottom));
                        graphics.DrawLine(shapePen, Dip(directedStart.X), Dip(directedStart.Y), Dip(directedEnd.X), Dip(directedEnd.Y));
                        if (shape.Shape == ShapeKind.Arrow)
                        {
                            var angle = Math.Atan2(directedEnd.Y - directedStart.Y, directedEnd.X - directedStart.X);
                            var arrowLength = Math.Sqrt(Math.Pow(directedEnd.X - directedStart.X, 2) +
                                                        Math.Pow(directedEnd.Y - directedStart.Y, 2));
                            var head = Math.Clamp(arrowLength * 0.2, 9, 24);
                            foreach (var offset in new[] { -0.55, 0.55 })
                                graphics.DrawLine(shapePen, Dip(directedEnd.X), Dip(directedEnd.Y),
                                    Dip(directedEnd.X - Math.Cos(angle + offset) * head),
                                    Dip(directedEnd.Y - Math.Sin(angle + offset) * head));
                        }
                        break;
                    case ShapeKind.Triangle:
                    case ShapeKind.Diamond:
                        var localPoints = shape.Shape == ShapeKind.Triangle
                            ? new[]
                            {
                                new PointD(shape.Bounds.Center.X, shape.Bounds.Top),
                                new PointD(shape.Bounds.Right, shape.Bounds.Bottom),
                                new PointD(shape.Bounds.Left, shape.Bounds.Bottom)
                            }
                            : new[]
                            {
                                new PointD(shape.Bounds.Center.X, shape.Bounds.Top),
                                new PointD(shape.Bounds.Right, shape.Bounds.Center.Y),
                                new PointD(shape.Bounds.Center.X, shape.Bounds.Bottom),
                                new PointD(shape.Bounds.Left, shape.Bounds.Center.Y)
                            };
                        graphics.DrawPolygon(shapePen, localPoints.Select(canvasObject.Transform.Apply)
                            .Select(point => new XPoint(Dip(point.X), Dip(point.Y))).ToArray());
                        break;
                    default:
                        graphics.DrawRectangle(shapePen, Dip(rect.X), Dip(rect.Y), Dip(rect.Width), Dip(rect.Height));
                        break;
                }
                break;
            case RichTextObject text:
                var origin = canvasObject.Transform.Apply(new PointD(text.Bounds.X, text.Bounds.Y));
                var font = new XFont("Segoe UI", text.Content.FontSize * PointsPerDip);
                graphics.DrawString(text.Content.PlainText, font, new XSolidBrush(ParseXColor("#20242D")),
                    new XRect(Dip(origin.X), Dip(origin.Y), Dip(text.Bounds.Width), Dip(text.Bounds.Height)),
                    XStringFormats.TopLeft);
                break;
            case ImageObject image:
                try
                {
                    using var xImage = XImage.FromFile(assetStore.GetPath(image.AssetHash));
                    var imageTopLeft = canvasObject.Transform.Apply(new PointD(image.Bounds.Left, image.Bounds.Top));
                    var imageBottomRight = canvasObject.Transform.Apply(new PointD(image.Bounds.Right, image.Bounds.Bottom));
                    var imageRect = Normalize(imageTopLeft, imageBottomRight);
                    graphics.DrawImage(xImage, Dip(imageRect.X), Dip(imageRect.Y),
                        Dip(imageRect.Width), Dip(imageRect.Height));
                }
                catch (Exception exception)
                {
                    warnings.Add($"Image {image.AltText ?? image.AssetHash} could not be embedded: {exception.Message}");
                }
                break;
        }
    }

    private string BuildSvg(NotePage page, List<string> warnings)
    {
        var builder = new StringBuilder();
        builder.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        builder.AppendLine(FormattableString.Invariant($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{page.Size.Width}\" height=\"{page.Size.Height}\" viewBox=\"0 0 {page.Size.Width} {page.Size.Height}\">"));
        builder.AppendLine($"<rect width=\"100%\" height=\"100%\" fill=\"{Escape(page.Template.PaperColor)}\"/>");
        AppendSvgTemplate(builder, page);
        if (page.ImportedLayer is not null)
            warnings.Add($"SVG export does not embed imported PDF page {page.ImportedLayer.SourcePageIndex + 1}; HoomNote vector overlays were preserved.");

        foreach (var canvasObject in page.Objects.Where(item => !item.IsHidden).OrderBy(item => item.ZIndex))
        {
            var matrix = canvasObject.Transform;
            var transform = FormattableString.Invariant($"matrix({matrix.M11} {matrix.M12} {matrix.M21} {matrix.M22} {matrix.M31} {matrix.M32})");
            switch (canvasObject)
            {
                case InkStrokeObject stroke:
                    var inkOpacity = stroke.Style.Normalize().Opacity;
                    if (StrokeOutlineBuilder.UsesCenterlineStroke(stroke))
                    {
                        var centerline = StrokeOutlineBuilder.FitCenterline(stroke);
                        if (centerline.Count == 0) break;
                        if (centerline.Count == 1)
                        {
                            var point = centerline[0];
                            builder.AppendLine(FormattableString.Invariant(
                                $"<circle cx=\"{point.X}\" cy=\"{point.Y}\" r=\"{StrokeOutlineBuilder.VectorCenterlineWidth(stroke.Style) / 2f}\" fill=\"{Escape(stroke.Style.Color)}\" fill-opacity=\"{inkOpacity}\" transform=\"{transform}\"/>"));
                        }
                        else
                        {
                            var pathData = string.Join(" ", centerline.Select((point, index) =>
                                FormattableString.Invariant($"{(index == 0 ? "M" : "L")} {point.X} {point.Y}")));
                            builder.AppendLine(FormattableString.Invariant(
                                $"<path d=\"{pathData}\" fill=\"none\" stroke=\"{Escape(stroke.Style.Color)}\" stroke-opacity=\"{inkOpacity}\" stroke-width=\"{StrokeOutlineBuilder.VectorCenterlineWidth(stroke.Style)}\" stroke-linecap=\"round\" stroke-linejoin=\"round\" transform=\"{transform}\"/>"));
                        }
                    }
                    else
                    {
                        var outline = StrokeOutlineBuilder.Build(stroke.Points, stroke.Style);
                        if (outline.Contour.Count >= 3)
                        {
                            var pathData = string.Join(" ", outline.Contour.Select((point, index) =>
                                FormattableString.Invariant($"{(index == 0 ? "M" : "L")} {point.X} {point.Y}")));
                            builder.AppendLine(FormattableString.Invariant(
                                $"<path d=\"{pathData} Z\" fill=\"{Escape(stroke.Style.Color)}\" fill-opacity=\"{inkOpacity}\" fill-rule=\"nonzero\" transform=\"{transform}\"/>"));
                        }
                    }
                    break;
                case ShapeObject shape:
                    var style = $"fill=\"none\" stroke=\"{Escape(shape.StrokeColor)}\" stroke-width=\"{shape.StrokeWidth.ToString(CultureInfo.InvariantCulture)}\" transform=\"{transform}\"";
                    var shapeElement = shape.Shape switch
                    {
                        ShapeKind.Ellipse => FormattableString.Invariant(
                            $"<ellipse cx=\"{shape.Bounds.Center.X}\" cy=\"{shape.Bounds.Center.Y}\" rx=\"{shape.Bounds.Width / 2}\" ry=\"{shape.Bounds.Height / 2}\" {style}/>") ,
                        ShapeKind.Line => FormattableString.Invariant(
                            $"<line x1=\"{(shape.StartPoint ?? new PointD(shape.Bounds.Left, shape.Bounds.Top)).X}\" y1=\"{(shape.StartPoint ?? new PointD(shape.Bounds.Left, shape.Bounds.Top)).Y}\" x2=\"{(shape.EndPoint ?? new PointD(shape.Bounds.Right, shape.Bounds.Bottom)).X}\" y2=\"{(shape.EndPoint ?? new PointD(shape.Bounds.Right, shape.Bounds.Bottom)).Y}\" {style}/>") ,
                        ShapeKind.Arrow => BuildSvgArrow(shape, style),
                        ShapeKind.Triangle => FormattableString.Invariant(
                            $"<polygon points=\"{shape.Bounds.Center.X},{shape.Bounds.Top} {shape.Bounds.Right},{shape.Bounds.Bottom} {shape.Bounds.Left},{shape.Bounds.Bottom}\" {style}/>") ,
                        ShapeKind.Diamond => FormattableString.Invariant(
                            $"<polygon points=\"{shape.Bounds.Center.X},{shape.Bounds.Top} {shape.Bounds.Right},{shape.Bounds.Center.Y} {shape.Bounds.Center.X},{shape.Bounds.Bottom} {shape.Bounds.Left},{shape.Bounds.Center.Y}\" {style}/>") ,
                        ShapeKind.RoundedRectangle => FormattableString.Invariant(
                            $"<rect x=\"{shape.Bounds.X}\" y=\"{shape.Bounds.Y}\" width=\"{shape.Bounds.Width}\" height=\"{shape.Bounds.Height}\" rx=\"12\" ry=\"12\" {style}/>") ,
                        _ => FormattableString.Invariant(
                            $"<rect x=\"{shape.Bounds.X}\" y=\"{shape.Bounds.Y}\" width=\"{shape.Bounds.Width}\" height=\"{shape.Bounds.Height}\" {style}/>")
                    };
                    builder.AppendLine(shapeElement);
                    break;
                case RichTextObject text:
                    builder.AppendLine(FormattableString.Invariant(
                        $"<foreignObject x=\"{text.Bounds.X}\" y=\"{text.Bounds.Y}\" width=\"{text.Bounds.Width}\" height=\"{text.Bounds.Height}\" transform=\"{transform}\"><div xmlns=\"http://www.w3.org/1999/xhtml\" style=\"font: {text.Content.FontSize}px Segoe UI; color: #f4f7fb; white-space: pre-wrap\">{Escape(text.Content.PlainText)}</div></foreignObject>"));
                    break;
                case ImageObject image:
                    try
                    {
                        var path = assetStore.GetPath(image.AssetHash);
                        var mime = Path.GetExtension(path).ToLowerInvariant() switch
                        {
                            ".jpg" or ".jpeg" => "image/jpeg",
                            ".webp" => "image/webp",
                            ".bmp" => "image/bmp",
                            _ => "image/png"
                        };
                        var data = Convert.ToBase64String(File.ReadAllBytes(path));
                        builder.AppendLine(FormattableString.Invariant(
                            $"<image x=\"{image.Bounds.X}\" y=\"{image.Bounds.Y}\" width=\"{image.Bounds.Width}\" height=\"{image.Bounds.Height}\" preserveAspectRatio=\"xMidYMid meet\" href=\"data:{mime};base64,{data}\" transform=\"{transform}\"/>"));
                    }
                    catch (Exception exception)
                    {
                        warnings.Add($"Image {image.AltText ?? image.AssetHash} could not be embedded: {exception.Message}");
                    }
                    break;
            }
        }
        builder.AppendLine("</svg>");
        return builder.ToString();
    }

    private static void AppendSvgTemplate(StringBuilder builder, NotePage page)
    {
        var spacing = Math.Max(4, page.Template.Spacing);
        if (page.Template.Kind == PageTemplateKind.Lined)
        {
            for (var y = page.Template.Margin; y < page.Size.Height; y += spacing)
                builder.AppendLine(FormattableString.Invariant($"<line x1=\"{page.Template.Margin}\" y1=\"{y}\" x2=\"{page.Size.Width - page.Template.Margin}\" y2=\"{y}\" stroke=\"{page.Template.LineColor}\" stroke-width=\"{page.Template.LineWidth}\"/>"));
        }
        else if (page.Template.Kind is PageTemplateKind.SquareGrid or PageTemplateKind.Graph)
        {
            for (var x = 0d; x < page.Size.Width; x += spacing)
                builder.AppendLine(FormattableString.Invariant($"<line x1=\"{x}\" y1=\"0\" x2=\"{x}\" y2=\"{page.Size.Height}\" stroke=\"{page.Template.LineColor}\"/>"));
            for (var y = 0d; y < page.Size.Height; y += spacing)
                builder.AppendLine(FormattableString.Invariant($"<line x1=\"0\" y1=\"{y}\" x2=\"{page.Size.Width}\" y2=\"{y}\" stroke=\"{page.Template.LineColor}\"/>"));
        }
    }

    private static string BuildSvgArrow(ShapeObject shape, string style)
    {
        var start = shape.StartPoint ?? new PointD(shape.Bounds.Left, shape.Bounds.Top);
        var end = shape.EndPoint ?? new PointD(shape.Bounds.Right, shape.Bounds.Bottom);
        var angle = Math.Atan2(end.Y - start.Y, end.X - start.X);
        var length = Math.Sqrt(Math.Pow(end.X - start.X, 2) + Math.Pow(end.Y - start.Y, 2));
        var head = Math.Clamp(length * 0.2, 9, 24);
        var first = new PointD(end.X - Math.Cos(angle - 0.55) * head,
            end.Y - Math.Sin(angle - 0.55) * head);
        var second = new PointD(end.X - Math.Cos(angle + 0.55) * head,
            end.Y - Math.Sin(angle + 0.55) * head);
        return FormattableString.Invariant(
            $"<path d=\"M {start.X} {start.Y} L {end.X} {end.Y} M {end.X} {end.Y} L {first.X} {first.Y} M {end.X} {end.Y} L {second.X} {second.Y}\" {style}/>");
    }

    private static RectD Normalize(PointD left, PointD right) => new(
        Math.Min(left.X, right.X), Math.Min(left.Y, right.Y), Math.Abs(right.X - left.X), Math.Abs(right.Y - left.Y));
    private static double Dip(double value) => value * PointsPerDip;

    private static XColor ParseXColor(string value, float opacity = 1)
    {
        var hex = value.TrimStart('#');
        if (hex.Length != 6) return XColor.FromArgb((int)(255 * opacity), 32, 36, 45);
        return XColor.FromArgb((int)(255 * Math.Clamp(opacity, 0, 1)),
            Convert.ToInt32(hex[..2], 16), Convert.ToInt32(hex.Substring(2, 2), 16), Convert.ToInt32(hex.Substring(4, 2), 16));
    }

    private static string Escape(string value) => System.Security.SecurityElement.Escape(value) ?? string.Empty;
}
