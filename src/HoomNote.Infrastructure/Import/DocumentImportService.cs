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
        var assetHash = await assetStore.AddAsync(stream, extension, cancellationToken);
        var pageCount = await CountPdfPagesAsync(sourcePath, cancellationToken);
        var selected = request.PageIndexes?.Where(index => index >= 0 && index < pageCount).Distinct().ToArray()
                       ?? Enumerable.Range(0, pageCount).ToArray();
        var margin = Math.Clamp(request.Margin, 0, 200);
        var fitTransform = margin <= 0
            ? Transform2D.Identity
            : Transform2D.Scale((816 - margin * 2) / 816d, (1056 - margin * 2) / 1056d, new PointD(0, 0))
                .Then(Transform2D.Translation(margin, margin));
        var pages = selected.Select((sourceIndex, ordinal) => new NotePage
        {
            Title = $"Page {ordinal + 1}",
            Template = PageTemplate.For(PageTemplateKind.Blank),
            ImportedLayer = new ImportedDocumentLayer
            {
                AssetHash = assetHash,
                SourceName = Path.GetFileName(request.SourcePath),
                SourcePageIndex = sourceIndex,
                Transform = request.RotationDegrees == 0
                    ? fitTransform
                    : fitTransform.Then(Transform2D.Rotation(request.RotationDegrees * Math.PI / 180d, new PointD(408, 528)))
            }
        }).ToArray();

        return new ImportResult(assetHash, Path.GetFileName(request.SourcePath), pages, []);
    }

    private static Task<int> CountPdfPagesAsync(string path, CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var document = PdfReader.Open(path, PdfDocumentOpenMode.Import);
                return document.PageCount;
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
