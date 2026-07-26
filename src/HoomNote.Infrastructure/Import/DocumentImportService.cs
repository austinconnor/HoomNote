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
    private sealed record PdfPageInfo(double Width, double Height);

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
            foreach (var image in samsung.Images)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await using var imageStream = new MemoryStream(image.Data, writable: false);
                var imageHash = await assetStore.AddAsync(imageStream, Path.GetExtension(image.FileName), cancellationToken);
                samsung.Pages[image.PageIndex].Objects.Add(new ImageObject
                {
                    AssetHash = imageHash,
                    Bounds = image.Bounds,
                    AltText = Path.GetFileNameWithoutExtension(image.FileName),
                    ZIndex = image.ZIndex,
                    IsLocked = false,
                    PreserveAspectRatio = true
                });
            }
            foreach (var page in samsung.Pages)
                page.Objects.Sort((left, right) => left.ZIndex.CompareTo(right.ZIndex));
            var selectedSamsungPages = request.PageIndexes?
                .Where(index => index >= 0 && index < samsung.Pages.Count)
                .Distinct()
                .Select((index, ordinal) => samsung.Pages[index] with { Title = $"Page {ordinal + 1}" })
                .ToArray() ?? samsung.Pages;
            return new ImportResult(samsungAssetHash, Path.GetFileName(request.SourcePath), selectedSamsungPages, samsung.Warnings);
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
            return new NotePage
            {
                Title = $"Page {ordinal + 1}",
                Size = pageSize,
                Template = PageTemplate.For(PageTemplateKind.Blank),
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
                    pages.Add(new PdfPageInfo(width, height));
                }
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
}
