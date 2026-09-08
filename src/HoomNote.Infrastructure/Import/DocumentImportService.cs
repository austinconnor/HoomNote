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
        var succeeded = false;
        try
        {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = workerPath,
            ArgumentList = { "convert", sourcePath, outputDirectory },
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        }) ?? throw new InvalidOperationException("The slide conversion worker could not be started.");
        var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
        using var registration = cancellationToken.Register(() =>
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException) { }
        });
        await process.WaitForExitAsync(cancellationToken);
        var outputText = await standardOutput;
        var errorText = await standardError;
        if (process.ExitCode != 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(errorText) ? outputText.Trim() : errorText.Trim());
        var output = Directory.EnumerateFiles(outputDirectory, "*.pdf").SingleOrDefault();
        succeeded = output is not null;
        return output ?? throw new InvalidOperationException("The slide converter did not produce a PDF.");
        }
        finally
        {
            if (!succeeded)
            {
                try { Directory.Delete(outputDirectory, recursive: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
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
        string? convertedOutputDirectory = null;
        if (!File.Exists(sourcePath)) throw new FileNotFoundException("Import source was not found.", sourcePath);
        var extension = Path.GetExtension(sourcePath).ToLowerInvariant();
        if (extension is ".ppt" or ".pptx")
        {
            if (slideConverter is null)
                throw new InvalidOperationException("The HoomNote Slide Import Pack is not installed.");
            sourcePath = await slideConverter.ConvertToPdfAsync(sourcePath, cancellationToken);
            convertedOutputDirectory = Path.GetDirectoryName(sourcePath);
            extension = ".pdf";
        }

        if (extension == ".sdocx")
        {
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
                    Transform = image.Transform,
                    AltText = Path.GetFileNameWithoutExtension(image.FileName),
                    ZIndex = image.ZIndex,
                    IsLocked = false,
                    PreserveAspectRatio = true
                });
            }
            foreach (var page in samsungPages)
                CanvasObjectOrdering.SortStable(page.Objects);
            var selectedSamsungPages = request.PageIndexes?
                .Where(index => index >= 0 && index < samsungPages.Length)
                .Distinct()
                .Select((index, ordinal) => samsungPages[index] with { Title = $"Page {ordinal + 1}" })
                .ToArray() ?? samsungPages;
            return new ImportResult(
                string.Empty,
                Path.GetFileName(request.SourcePath),
                selectedSamsungPages,
                warnings);
        }

        if (extension != ".pdf") throw new NotSupportedException("HoomNote currently imports PDF, PPT, PPTX, and Samsung Notes SDOCX documents.");
        try
        {
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
        finally
        {
            if (convertedOutputDirectory is not null)
            {
                try { Directory.Delete(convertedOutputDirectory, recursive: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
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
            catch (OperationCanceledException)
            {
                throw;
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
            catch (OperationCanceledException)
            {
                throw;
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
            var extracted = PdfSemanticTextExtractor.ExtractDocument(path,
                pages.Select(page => new SizeD(page.Width, page.Height)).ToArray(), cancellationToken);
            for (var index = 0; index < extracted.Count; index++)
            {
                var destination = pages[index];
                var regions = extracted[index];
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
