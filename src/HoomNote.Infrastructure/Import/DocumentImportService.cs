using System.Diagnostics;
using HoomNote.Core.Documents;
using HoomNote.Core.Services;
using PdfSharp.Pdf.IO;

namespace HoomNote.Infrastructure.Import;

public interface ISlideConverter
{
    Task<string> ConvertToPdfAsync(string sourcePath, CancellationToken cancellationToken = default);
}

public sealed class SlideWorkerConverter(string workerPath, string temporaryRoot) : ISlideConverter
{
    public async Task<string> ConvertToPdfAsync(string sourcePath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(workerPath))
            throw new FileNotFoundException("The HoomNote Slide Import Pack is not installed.", workerPath);
        var outputDirectory = Path.Combine(temporaryRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outputDirectory);
        var process = Process.Start(new ProcessStartInfo
        {
            FileName = workerPath,
            ArgumentList = { "convert", sourcePath, outputDirectory },
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        }) ?? throw new InvalidOperationException("The slide conversion worker could not be started.");
        using var registration = cancellationToken.Register(() => process.Kill(entireProcessTree: true));
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0)
            throw new InvalidOperationException((await process.StandardError.ReadToEndAsync(cancellationToken)).Trim());
        var output = Directory.EnumerateFiles(outputDirectory, "*.pdf").SingleOrDefault();
        return output ?? throw new InvalidOperationException("The slide converter did not produce a PDF.");
    }
}

public sealed class DocumentImportService(IAssetStore assetStore, ISlideConverter? slideConverter = null) : IDocumentImportService
{
    private const double DipsPerPdfPoint = 96d / 72d;
    private sealed record PdfPageInfo(
        double Width,
        double Height,
        string Text,
        IReadOnlyList<RecognizedTextRegion> TextRegions);

    public async Task<ImportResult> ImportAsync(ImportRequest request, CancellationToken cancellationToken = default)
    {
        var sourcePath = Path.GetFullPath(request.SourcePath);
        if (!File.Exists(sourcePath)) throw new FileNotFoundException("Import source was not found.", sourcePath);
        var extension = Path.GetExtension(sourcePath).ToLowerInvariant();
        if (extension is ".ppt" or ".pptx")
        {
            if (slideConverter is null)
                throw new InvalidOperationException("The HoomNote Slide Import Pack is not installed.");
            sourcePath = await slideConverter.ConvertToPdfAsync(sourcePath, cancellationToken);
            extension = ".pdf";
        }

        if (extension == ".sdocx")
        {
            await using var samsungStream = File.OpenRead(sourcePath);
            var samsungAssetHash = await assetStore.AddAsync(samsungStream, extension, cancellationToken);
            var samsung = await SamsungNotesImportParser.ParseAsync(sourcePath, cancellationToken);
            var samsungPages = samsung.Pages.ToArray();
            var warnings = samsung.Warnings.ToList();
            if (samsung.Pdf is { } embeddedPdf)
            {
                await using var pdfStream = new MemoryStream(embeddedPdf.Data, writable: false);
                var pdfAssetHash = await assetStore.AddAsync(pdfStream, ".pdf", cancellationToken);
                var pdfPageCount = await ReadEmbeddedPdfPageCountAsync(
                    embeddedPdf.Data, cancellationToken);
                var attachedPageCount = Math.Min(pdfPageCount, samsungPages.Length);
                for (var pageIndex = 0; pageIndex < attachedPageCount; pageIndex++)
                {
                    samsungPages[pageIndex].ImportedLayer = new ImportedDocumentLayer
                    {
                        AssetHash = pdfAssetHash,
                        SourceName = embeddedPdf.FileName,
                        SourcePageIndex = pageIndex,
                        Transform = Transform2D.Identity
                    };
                }
                warnings.Add($"The embedded PDF background was restored on {attachedPageCount} page(s).");
            }
            foreach (var image in samsung.Images)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await using var imageStream = new MemoryStream(image.Data, writable: false);
                var imageHash = await assetStore.AddAsync(imageStream, Path.GetExtension(image.FileName), cancellationToken);
                samsungPages[image.PageIndex].Objects.Add(new ImageObject
                {
                    AssetHash = imageHash,
                    Bounds = image.Bounds,
                    AltText = Path.GetFileNameWithoutExtension(image.FileName),
                    ZIndex = image.ZIndex,
                    IsLocked = false,
                    PreserveAspectRatio = true
                });
            }
            foreach (var page in samsungPages)
                page.Objects.Sort((left, right) => left.ZIndex.CompareTo(right.ZIndex));
            var selectedSamsungPages = request.PageIndexes?
                .Where(index => index >= 0 && index < samsungPages.Length)
                .Distinct()
                .Select((index, ordinal) => samsungPages[index] with { Title = $"Page {ordinal + 1}" })
                .ToArray() ?? samsungPages;
            return new ImportResult(
                samsungAssetHash,
                Path.GetFileName(request.SourcePath),
                selectedSamsungPages,
                warnings);
        }

        if (extension != ".pdf") throw new NotSupportedException("HoomNote currently imports PDF, PPT, PPTX, and Samsung Notes SDOCX documents.");
        await using var stream = File.OpenRead(sourcePath);
        var assetTask = assetStore.AddAsync(stream, extension, cancellationToken);
        var pageInfoTask = ReadPdfPagesAsync(sourcePath, cancellationToken);
        await Task.WhenAll(assetTask, pageInfoTask);
        var assetHash = await assetTask;
        var pageInfo = await pageInfoTask;
        var selected = request.PageIndexes?
                           .Where(index => index >= 0 && index < pageInfo.Count)
                           .Distinct()
                           .ToArray()
                       ?? Enumerable.Range(0, pageInfo.Count).ToArray();
        var pages = selected.Select((sourceIndex, ordinal) =>
        {
            var sourcePage = pageInfo[sourceIndex];
            var pageSize = new SizeD(sourcePage.Width, sourcePage.Height);
            var fitTransform = CreatePageTransform(pageSize, request.Margin, request.RotationDegrees);
            var textRegions = sourcePage.TextRegions
                .Select(region => region with { Bounds = TransformBounds(region.Bounds, fitTransform) })
                .ToList();
            return new NotePage
            {
                Title = $"Page {ordinal + 1}",
                Size = pageSize,
                Template = PageTemplate.For(PageTemplateKind.Blank),
                RecognizedText = sourcePage.Text,
                RecognizedRegions = textRegions,
                ImportedLayer = new ImportedDocumentLayer
                {
                    AssetHash = assetHash,
                    SourceName = Path.GetFileName(request.SourcePath),
                    SourcePageIndex = sourceIndex,
                    Transform = fitTransform
                }
            };
        }).ToArray();

        return new ImportResult(assetHash, Path.GetFileName(request.SourcePath), pages, []);
    }

    private static RectD TransformBounds(RectD bounds, Transform2D transform)
    {
        var corners = new[]
        {
            transform.Apply(new PointD(bounds.Left, bounds.Top)),
            transform.Apply(new PointD(bounds.Right, bounds.Top)),
            transform.Apply(new PointD(bounds.Right, bounds.Bottom)),
            transform.Apply(new PointD(bounds.Left, bounds.Bottom))
        };
        return RectD.FromPoints(corners);
    }

    private static Transform2D CreatePageTransform(SizeD pageSize, double requestedMargin, int rotationDegrees)
    {
        var maximumMargin = Math.Max(0, Math.Min(pageSize.Width, pageSize.Height) / 2d - 0.5);
        var margin = Math.Clamp(requestedMargin, 0, Math.Min(200, maximumMargin));
        var transform = Transform2D.Identity;
        if (margin > 0)
        {
            var scale = Math.Min(
                (pageSize.Width - margin * 2) / pageSize.Width,
                (pageSize.Height - margin * 2) / pageSize.Height);
            var horizontalInset = (pageSize.Width - pageSize.Width * scale) / 2d;
            var verticalInset = (pageSize.Height - pageSize.Height * scale) / 2d;
            transform = Transform2D.Scale(scale, scale, new PointD(0, 0))
                .Then(Transform2D.Translation(horizontalInset, verticalInset));
        }
        var normalizedRotation = ((rotationDegrees % 360) + 360) % 360;
        return normalizedRotation == 0
            ? transform
            : transform.Then(Transform2D.Rotation(
                normalizedRotation * Math.PI / 180d,
                new PointD(pageSize.Width / 2d, pageSize.Height / 2d)));
    }

    private static Task<IReadOnlyList<PdfPageInfo>> ReadPdfPagesAsync(
        string path,
        CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var document = PdfReader.Open(path, PdfDocumentOpenMode.Import);
                var pages = new List<PdfPageInfo>(document.PageCount);
                foreach (var page in document.Pages)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var width = page.Width.Point * DipsPerPdfPoint;
                    var height = page.Height.Point * DipsPerPdfPoint;
                    var rotation = ((page.Rotate % 360) + 360) % 360;
                    if (rotation is 90 or 270) (width, height) = (height, width);
                    if (!double.IsFinite(width) || !double.IsFinite(height) ||
                        width <= 0 || height <= 0)
                        throw new InvalidDataException("The PDF contains a page with invalid dimensions.");
                    pages.Add(new PdfPageInfo(width, height, string.Empty, []));
                }

                // PDF rendering remains on Windows.Data.Pdf/PDFsharp. PdfPig is used only
                // to retain the source document's Unicode words and their page geometry so
                // selection and clipboard copy do not flatten text into pixels.
                TryAttachSemanticText(path, pages, cancellationToken);
                return (IReadOnlyList<PdfPageInfo>)pages;
            }
            catch (Exception exception)
            {
                throw new InvalidDataException(
                    "HoomNote could not read the PDF page tree. The file may be encrypted, damaged, or unsupported.",
                    exception);
            }
        }, cancellationToken);
    }

    private static Task<int> ReadEmbeddedPdfPageCountAsync(
        byte[] data,
        CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var stream = new MemoryStream(data, writable: false);
                using var document = PdfReader.Open(stream, PdfDocumentOpenMode.Import);
                return document.PageCount;
            }
            catch (Exception exception)
            {
                throw new InvalidDataException(
                    "HoomNote could not read the PDF embedded in this Samsung Notes file.",
                    exception);
            }
        }, cancellationToken);
    }

    private static void TryAttachSemanticText(
        string path,
        IList<PdfPageInfo> pages,
        CancellationToken cancellationToken)
    {
        try
        {
            using var textDocument = UglyToad.PdfPig.PdfDocument.Open(path);
            var pageCount = Math.Min(pages.Count, textDocument.NumberOfPages);
            for (var index = 0; index < pageCount; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var textPage = textDocument.GetPage(index + 1);
                var sourceWidth = Convert.ToDouble(textPage.Width);
                var sourceHeight = Convert.ToDouble(textPage.Height);
                if (sourceWidth <= 0 || sourceHeight <= 0) continue;

                var destination = pages[index];
                var regions = new List<RecognizedTextRegion>();
                foreach (var word in textPage.GetWords())
                {
                    var text = word.Text;
                    if (string.IsNullOrWhiteSpace(text)) continue;
                    var box = word.BoundingBox;
                    var left = Convert.ToDouble(box.Left);
                    var right = Convert.ToDouble(box.Right);
                    var top = Convert.ToDouble(box.Top);
                    var bottom = Convert.ToDouble(box.Bottom);
                    var bounds = new RectD(
                        left / sourceWidth * destination.Width,
                        (sourceHeight - top) / sourceHeight * destination.Height,
                        (right - left) / sourceWidth * destination.Width,
                        (top - bottom) / sourceHeight * destination.Height);
                    if (!double.IsFinite(bounds.X) || !double.IsFinite(bounds.Y) ||
                        !double.IsFinite(bounds.Width) || !double.IsFinite(bounds.Height) ||
                        bounds.Width <= 0 || bounds.Height <= 0) continue;
                    regions.Add(new RecognizedTextRegion
                    {
                        Text = text,
                        Bounds = bounds,
                        Source = "Pdf"
                    });
                }

                pages[index] = destination with
                {
                    Text = string.Join(' ', regions.Select(region => region.Text)),
                    TextRegions = regions
                };
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Image-only, encrypted, or malformed PDFs still import and remain OCR-searchable.
            // Semantic selection is simply unavailable when the source has no readable text.
        }
    }
}
