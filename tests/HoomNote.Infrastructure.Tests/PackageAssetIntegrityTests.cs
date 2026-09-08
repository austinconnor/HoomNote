using System.IO.Compression;
using System.Text.Json;
using HoomNote.Core.Documents;
using HoomNote.Infrastructure.Serialization;
using HoomNote.Infrastructure.Storage;

namespace HoomNote.Infrastructure.Tests;

public sealed class PackageAssetIntegrityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "HoomNote.Tests", Guid.NewGuid().ToString("N"));

    public PackageAssetIntegrityTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Theory]
    [InlineData("missing")]
    [InlineData("wrong-content")]
    [InlineData("duplicate")]
    public async Task ImportRejectsBrokenAssetsEvenWhenLocalStoreHasTheExpectedFile(string defect)
    {
        var store = new ContentAddressedAssetStore(Path.Combine(_root, "assets"));
        var hash = await store.AddAsync(new MemoryStream([1, 2, 3]), ".png");
        var document = HoomNoteDocument.Create("Images");
        document.Pages.Add(new NotePage { Objects = [new ImageObject { AssetHash = hash }] });
        var path = Path.Combine(_root, "broken.hoomnote");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            await using (var manifest = zip.CreateEntry("manifest.json").Open())
                await JsonSerializer.SerializeAsync(manifest, document, HoomNoteJson.Options);
            if (defect != "missing")
            {
                using var entry = zip.CreateEntry($"assets/{hash}").Open();
                entry.Write(defect == "wrong-content" ? [4, 5, 6] : [1, 2, 3]);
            }
            if (defect == "duplicate")
            {
                using var duplicate = zip.CreateEntry($"assets/{hash}").Open();
                duplicate.Write([1, 2, 3]);
            }
        }

        await Assert.ThrowsAsync<InvalidDataException>(() => new HoomNotePackageService(store).ImportAsync(path));
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(store.GetPath(hash)));
    }

    [Fact]
    public async Task ExportMissingAssetPreservesExistingDestination()
    {
        var store = new ContentAddressedAssetStore(Path.Combine(_root, "assets"));
        var document = HoomNoteDocument.Create("Missing image");
        document.Pages.Add(new NotePage { Objects = [new ImageObject { AssetHash = "missing.png" }] });
        var path = Path.Combine(_root, "existing.hoomnote");
        await File.WriteAllTextAsync(path, "previous export");

        await Assert.ThrowsAsync<InvalidDataException>(() => new HoomNotePackageService(store).ExportAsync(document, path));

        Assert.Equal("previous export", await File.ReadAllTextAsync(path));
        Assert.Empty(Directory.EnumerateFiles(_root, "*.tmp"));
    }
}
