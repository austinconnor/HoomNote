using HoomNote.Core.Documents;
using HoomNote.Core.Services;
using HoomNote.Infrastructure.Export;
using HoomNote.Infrastructure.Storage;

namespace HoomNote.Infrastructure.Tests;

public sealed class ImageExportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "HoomNote.Tests", Guid.NewGuid().ToString("N"));
    public ImageExportTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Theory]
    [InlineData(false, true, 75, 37.5)]
    [InlineData(true, true, 37.5, 75)]
    [InlineData(false, false, 75, 75)]
    public async Task PdfImageKeepsAspectAndRotation(bool rotate, bool preserve, double width, double height)
    {
        var store = new ContentAddressedAssetStore(Path.Combine(_root, "assets"));
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAIAAAABCAIAAAB7QOjdAAAADUlEQVR4nGP4zwAE/wEHAAH/4iOeWQAAAABJRU5ErkJggg==");
        var hash = await store.AddAsync(new MemoryStream(png), ".png");
        var document = HoomNoteDocument.Create("Image export");
        document.Pages.Add(new NotePage { Objects = [new ImageObject
        {
            AssetHash = hash, Bounds = new RectD(100, 100, 100, 100), PreserveAspectRatio = preserve,
            Transform = rotate ? Transform2D.Rotation(Math.PI / 2, new PointD(150, 150)) : Transform2D.Identity
        }] });
        var path = Path.Combine(_root, "images.pdf");
        var result = await new VectorExportService(store).ExportAsync(document, path, VectorExportFormat.Pdf);
        Assert.Empty(result.Warnings);

        using var pdf = UglyToad.PdfPig.PdfDocument.Open(path);
        var rectangle = Assert.Single(pdf.GetPage(1).GetImages()).BoundingBox;
        var corners = new[] { rectangle.BottomLeft, rectangle.BottomRight, rectangle.TopLeft, rectangle.TopRight };
        Assert.Equal(width, corners.Max(point => point.X) - corners.Min(point => point.X), 2);
        Assert.Equal(height, corners.Max(point => point.Y) - corners.Min(point => point.Y), 2);
        Assert.Equal(112.5, (corners.Max(point => point.X) + corners.Min(point => point.X)) / 2, 2);
    }
}
