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
        var unloadedPageCount = document.Pages.Count(page => !page.IsContentLoaded);
        if (unloadedPageCount > 0)
            throw new InvalidOperationException(
                $"Cannot export a partially loaded notebook. Load all pages first ({unloadedPageCount} page(s) are not loaded)." );

        var fullDestination = Path.GetFullPath(destinationPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullDestination)!);
        var temporary = fullDestination + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
                             128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false))
            {
                // Ink manifests are repetitive JSON and shrink substantially at the maximum
                // Deflate level. Packages are exported infrequently, so portability and transfer
                // size matter more here than minimizing a few seconds of export CPU time.
                var manifest = archive.CreateEntry("manifest.json", CompressionLevel.SmallestSize);
                await using (var output = manifest.Open())
                    await JsonSerializer.SerializeAsync(output,
                        document with { SchemaVersion = HoomNoteDocument.CurrentSchemaVersion },
                        HoomNoteJson.Options, cancellationToken);

                foreach (var asset in ReferencedAssets(document).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    var sourcePath = assetStore.GetPath(asset);
                    if (!File.Exists(sourcePath)) continue;
                    var entry = archive.CreateEntry($"assets/{asset}", CompressionLevel.SmallestSize);
                    await using var output = entry.Open();
                    await using var input = new FileStream(
                        sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                        128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    await input.CopyToAsync(output, cancellationToken);
                }
            }

            File.Move(temporary, fullDestination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
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

        var referencedAssets = ReferencedAssets(document).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries.Where(entry => entry.FullName.StartsWith("assets/", StringComparison.Ordinal)))
        {
            if (entry.Name.Length == 0 || !referencedAssets.Contains(entry.Name)) continue;
            await using var source = entry.Open();
            await assetStore.AddAsync(source, Path.GetExtension(entry.Name), cancellationToken);
        }

        return RemapIdentity(document);
    }

    private static HoomNoteDocument RemapIdentity(HoomNoteDocument document)
    {
        var pageIds = document.Pages.ToDictionary(page => page.Id, _ => Guid.NewGuid());
        var objectIds = document.Pages
            .SelectMany(page => page.Objects)
            .Select(canvasObject => canvasObject.Id)
            .Distinct()
            .ToDictionary(id => id, _ => Guid.NewGuid());

        CanvasObject RemapObject(CanvasObject canvasObject)
        {
            var id = objectIds[canvasObject.Id];
            return canvasObject switch
            {
                InkStrokeObject stroke => stroke with
                {
                    Id = id,
                    ParentStrokeId = stroke.ParentStrokeId is { } parentId && objectIds.TryGetValue(parentId, out var remappedParent)
                        ? remappedParent
                        : null,
                    Points = [.. stroke.Points]
                },
                GroupObject group => group with
                {
                    Id = id,
                    ChildIds = group.ChildIds
                        .Where(objectIds.ContainsKey)
                        .Select(childId => objectIds[childId])
                        .ToList()
                },
                _ => canvasObject with { Id = id }
            };
        }

        var pages = document.Pages.Select(page => page with
        {
            Id = pageIds[page.Id],
            Objects = page.Objects.Select(RemapObject).ToList(),
            RecognizedRegions = [.. page.RecognizedRegions],
            IsContentLoaded = true
        }).ToList();
        var sections = document.Sections.Select(section => section with
        {
            Id = Guid.NewGuid(),
            PageIds = section.PageIds
                .Where(pageIds.ContainsKey)
                .Select(pageId => pageIds[pageId])
                .ToList()
        }).ToList();

        return document with
        {
            Id = Guid.NewGuid(),
            Title = LibraryNamePolicy.Normalize(document.Title + " (Imported)") ?? "Imported notebook",
            Tags = [.. document.Tags],
            Sections = sections,
            Pages = pages,
            Settings = document.Settings with { }
        };
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
