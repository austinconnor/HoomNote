using System.Security.Cryptography;
using System.Text.Json;
using HoomNote.Core.Documents;
using HoomNote.Core.Services;
using HoomNote.Infrastructure.Import;
using HoomNote.Infrastructure.Storage;

if (args.Length != 2) throw new ArgumentException("Usage: HoomNote.Import.Audit <source.sdocx> <output-directory>");
var source = Path.GetFullPath(args[0]);
var root = Path.GetFullPath(args[1]);
Directory.CreateDirectory(root);
var store = new ContentAddressedAssetStore(Path.Combine(root, "assets"));
var result = await new DocumentImportService(store).ImportAsync(new ImportRequest(source));
var document = HoomNoteDocument.Create(Path.GetFileNameWithoutExtension(source));
document.Pages.AddRange(result.Pages);
document.Sections[0].PageIds.AddRange(result.Pages.Select(page => page.Id));
var packagePath = Path.Combine(root, "Corrected Samsung notebook.hoomnote");
var packageService = new HoomNotePackageService(store);
await packageService.ExportAsync(document, packagePath);
var restored = await packageService.ImportAsync(packagePath);
var expected = document.Pages.SelectMany(page => page.Objects).OfType<ImageObject>().Select(image => image.AssetHash).ToArray();
var actual = restored.Pages.SelectMany(page => page.Objects).OfType<ImageObject>().Select(image => image.AssetHash).ToArray();
if (!expected.SequenceEqual(actual)) throw new InvalidDataException("Package roundtrip changed image identities.");
await using var repository = new SqliteDocumentRepository(Path.Combine(root, "audit.db"));
await repository.InitializeAsync();
await repository.SaveAsync(document);
var loaded = await repository.LoadAsync(document.Id) ?? throw new InvalidDataException("Saved notebook was not found.");
if (!expected.SequenceEqual(loaded.Pages.SelectMany(page => page.Objects).OfType<ImageObject>().Select(image => image.AssetHash)))
    throw new InvalidDataException("SQLite roundtrip changed image identities.");
var report = new
{
    SourceSha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(source))),
    Pages = document.Pages.Count,
    Images = expected.Length,
    UniqueImages = expected.Distinct().Count(),
    InkStrokes = document.Pages.SelectMany(page => page.Objects).OfType<InkStrokeObject>().Count(),
    InkPoints = document.Pages.SelectMany(page => page.Objects).OfType<InkStrokeObject>().Sum(stroke => (long)stroke.Points.Count),
    PackageRoundtrip = "passed", SqliteRoundtrip = "passed", result.Warnings,
    Placements = document.Pages.SelectMany((page, index) => page.Objects.OfType<ImageObject>().Select(image => new
    {
        Page = index + 1, image.ZIndex, image.AltText, image.AssetHash, image.Bounds, image.Transform
    }))
};
var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
await File.WriteAllTextAsync(Path.Combine(root, "image-audit.json"), json);
Console.WriteLine(JsonSerializer.Serialize(new { report.Pages, report.Images, report.UniqueImages, report.InkStrokes, report.InkPoints, result.Warnings }));
