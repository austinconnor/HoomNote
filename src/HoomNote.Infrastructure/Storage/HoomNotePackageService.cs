using System.IO.Compression;
using System.Text.Json;
using HoomNote.Core.Documents;
using HoomNote.Core.Services;
using HoomNote.Infrastructure.Serialization;

namespace HoomNote.Infrastructure.Storage;

public sealed class HoomNotePackageService(IAssetStore assetStore) : IPackageService
{
    public async Task ExportAsync(HoomNoteDocument document, string destinationPath, CancellationToken cancellationToken = default)
    {
        var fullDestination = Path.GetFullPath(destinationPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullDestination)!);
        var temporary = fullDestination + $".{Guid.NewGuid():N}.tmp";
        await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
                         128 * 1024, FileOptions.Asynchronous))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false))
        {
            var manifest = archive.CreateEntry("manifest.json", CompressionLevel.Fastest);
            await using (var output = manifest.Open())
                await JsonSerializer.SerializeAsync(output, document, HoomNoteJson.Options, cancellationToken);

            foreach (var asset in ReferencedAssets(document).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var sourcePath = assetStore.GetPath(asset);
                if (!File.Exists(sourcePath)) continue;
                var entry = archive.CreateEntry($"assets/{asset}", CompressionLevel.Fastest);
                await using var output = entry.Open();
                await using var input = File.OpenRead(sourcePath);
                await input.CopyToAsync(output, cancellationToken);
            }
        }

        File.Move(temporary, fullDestination, overwrite: true);
    }

    public async Task<HoomNoteDocument> ImportAsync(string packagePath, CancellationToken cancellationToken = default)
    {
        using var archive = ZipFile.OpenRead(packagePath);
        var manifest = archive.GetEntry("manifest.json")
            ?? throw new InvalidDataException("The package does not contain a manifest.");
        await using var manifestStream = manifest.Open();
        var document = await JsonSerializer.DeserializeAsync<HoomNoteDocument>(manifestStream, HoomNoteJson.Options, cancellationToken)
            ?? throw new InvalidDataException("The package manifest is invalid.");
        if (document.SchemaVersion > HoomNoteDocument.CurrentSchemaVersion)
            throw new InvalidDataException("This package was created by a newer HoomNote version.");

        foreach (var entry in archive.Entries.Where(entry => entry.FullName.StartsWith("assets/", StringComparison.Ordinal)))
        {
            if (entry.Name.Length == 0) continue;
            await using var source = entry.Open();
            await assetStore.AddAsync(source, Path.GetExtension(entry.Name), cancellationToken);
        }

        return document with { Id = Guid.NewGuid(), Title = document.Title + " (Imported)" };
    }

    private static IEnumerable<string> ReferencedAssets(HoomNoteDocument document)
    {
        foreach (var page in document.Pages)
        {
            if (page.ImportedLayer is not null) yield return page.ImportedLayer.AssetHash;
            foreach (var image in page.Objects.OfType<ImageObject>()) yield return image.AssetHash;
        }
    }
}

