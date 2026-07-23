using System.Buffers.Binary;
using System.IO.Compression;
using Microsoft.Data.Sqlite;
using HoomNote.Core.Documents;
using HoomNote.Core.Services;
using HoomNote.Infrastructure.Export;
using HoomNote.Infrastructure.Import;
using HoomNote.Infrastructure.Storage;
using PdfSharp.Pdf;

namespace HoomNote.Infrastructure.Tests;

public sealed class PersistenceTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "HoomNote.Tests", Guid.NewGuid().ToString("N"));

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Repository_RoundTripsPolymorphicVectorObjectsAndSearchText()
    {
        await using var repository = new SqliteDocumentRepository(Path.Combine(_root, "library.db"));
        await repository.InitializeAsync();
        var document = HoomNoteDocument.Create("Physics 301");
        document.Settings = document.Settings with
        {
            DefaultPageTemplateKind = PageTemplateKind.Graph,
            DefaultPaperColor = "#202124"
        };
        var page = AddPage(document);
        document.Tags.Add("lecture");
        page.RecognizedText = "angular momentum";
        page.Objects.Add(new InkStrokeObject
        {
            Points = [new InkPoint(10, 20, 0.4f), new InkPoint(20, 30, 0.8f)]
        });
        page.Objects.Add(new RichTextObject
        {
            Content = RichTextDocument.FromPlainText("Conservation law")
        });

        await repository.SaveAsync(document);
        var loaded = await repository.LoadAsync(document.Id);
        var search = await repository.SearchAsync("angular momentum");

        Assert.NotNull(loaded);
        Assert.Equal(document.Title, loaded.Title);
        Assert.Equal(PageTemplateKind.Graph, loaded.Settings.DefaultPageTemplateKind);
        Assert.Equal("#202124", loaded.Settings.DefaultPaperColor);
        Assert.IsType<InkStrokeObject>(loaded.Pages[0].Objects[0]);
        Assert.IsType<RichTextObject>(loaded.Pages[0].Objects[1]);
        Assert.Contains(search, result => result.DocumentId == document.Id);
    }

    [Fact]
    public async Task Repository_DeleteRemovesNotebookAndSearchRows()
    {
        await using var repository = new SqliteDocumentRepository(Path.Combine(_root, "delete-library.db"));
        await repository.InitializeAsync();
        var document = HoomNoteDocument.Create("Delete me");
        AddPage(document).Objects.Add(new RichTextObject
        {
            Content = RichTextDocument.FromPlainText("unique deletion marker")
        });
        await repository.SaveAsync(document);

        await repository.DeleteAsync(document.Id);

        Assert.Null(await repository.LoadAsync(document.Id));
        Assert.DoesNotContain(await repository.ListAsync(), item => item.Id == document.Id);
        Assert.DoesNotContain(await repository.SearchAsync("unique deletion marker"), item => item.DocumentId == document.Id);
    }

    [Fact]
    public async Task Repository_SearchFindsZeroPageNotebookTitles()
    {
        await using var repository = new SqliteDocumentRepository(Path.Combine(_root, "title-search.db"));
        await repository.InitializeAsync();
        var document = HoomNoteDocument.Create("Cardiovascular review");
        await repository.SaveAsync(document);

        var result = Assert.Single(await repository.SearchAsync("Cardiovascular"));

        Assert.Equal(document.Id, result.DocumentId);
        Assert.Null(result.PageId);
        Assert.Equal("Notebook", result.PageTitle);
    }

    [Fact]
    public async Task Repository_RecognitionUpdateIsSearchableWithoutFullPageSerialization()
    {
        await using var repository = new SqliteDocumentRepository(Path.Combine(_root, "recognition-search.db"));
        await repository.InitializeAsync();
        var document = HoomNoteDocument.Create("Anatomy");
        var page = AddPage(document);
        page.Objects.Add(new InkStrokeObject
        {
            Points = [new InkPoint(1, 1), new InkPoint(10, 10)]
        });
        await repository.SaveAsync(document);

        var regions = new[]
        {
            new RecognizedTextRegion { Text = "posterior", Bounds = new RectD(20, 30, 90, 24) },
            new RecognizedTextRegion { Text = "abdominal", Bounds = new RectD(116, 30, 105, 24) },
            new RecognizedTextRegion { Text = "wall", Bounds = new RectD(228, 30, 42, 24) }
        };
        await repository.SaveRecognizedTextAsync(document, page, "posterior abdominal wall", regions);

        var loaded = await repository.LoadAsync(document.Id);
        Assert.Equal("posterior abdominal wall", loaded!.Pages[0].RecognizedText);
        Assert.Equal(regions, loaded.Pages[0].RecognizedRegions);
        var result = Assert.Single(await repository.SearchAsync("abdominal"));
        Assert.Equal(page.Id, result.PageId);
        Assert.Equal("typed + handwriting", result.Source);
    }

    [Fact]
    public async Task Repository_MigratesLegacyPagesToPersistRecognizedWordRegions()
    {
        await using (var bootstrap = new SqliteDocumentRepository(Path.Combine(_root, "provider-bootstrap.db")))
            await bootstrap.InitializeAsync();

        var path = Path.Combine(_root, "legacy-regions.db");
        await using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE pages (
                    id TEXT PRIMARY KEY,
                    document_id TEXT NOT NULL,
                    ordinal INTEGER NOT NULL,
                    title TEXT NOT NULL,
                    page_json TEXT NOT NULL,
                    recognized_text TEXT NOT NULL,
                    updated_utc TEXT NOT NULL
                );
                """;
            await command.ExecuteNonQueryAsync();
        }

        await using var repository = new SqliteDocumentRepository(path);
        await repository.InitializeAsync();
        var document = HoomNoteDocument.Create("Migrated notebook");
        var page = AddPage(document);
        await repository.SaveAsync(document);
        var region = new RecognizedTextRegion { Text = "pancreas", Bounds = new RectD(42, 80, 74, 20) };
        await repository.SaveRecognizedTextAsync(document, page, "pancreas", [region]);

        var loaded = await repository.LoadAsync(document.Id);

        Assert.Equal(region, Assert.Single(loaded!.Pages[0].RecognizedRegions));
    }

    [Fact]
    public async Task Repository_SearchSupportsPrefixTermsForTypedAndRecognizedText()
    {
        await using var repository = new SqliteDocumentRepository(Path.Combine(_root, "prefix-search.db"));
        await repository.InitializeAsync();
        var document = HoomNoteDocument.Create("Anatomy");
        var page = AddPage(document);
        page.Objects.Add(new RichTextObject { Content = RichTextDocument.FromPlainText("cardiovascular pathway") });
        await repository.SaveAsync(document);
        await repository.SaveRecognizedTextAsync(document, page, "posterior abdominal wall");

        Assert.Contains(await repository.SearchAsync("cardiovasc"), item => item.PageId == page.Id);
        Assert.Contains(await repository.SearchAsync("poster abdom"), item => item.PageId == page.Id);
    }

    [Fact]
    public async Task Repository_InkAppendJournalRoundTripsAndCompactsOnFullSave()
    {
        await using var repository = new SqliteDocumentRepository(Path.Combine(_root, "append-library.db"));
        await repository.InitializeAsync();
        var document = HoomNoteDocument.Create("Fast ink autosave");
        var page = AddPage(document);
        await repository.SaveAsync(document);

        var stroke = new InkStrokeObject
        {
            ZIndex = 1,
            Points = [new InkPoint(4, 5, 0.4f), new InkPoint(20, 24, 0.9f)]
        };
        page.Objects.Add(stroke);
        page.UpdatedAt = DateTimeOffset.UtcNow.AddMilliseconds(10);

        Assert.True(await repository.SaveInkAppendsAsync(document, [(page.Id, stroke)]));
        var journalLoaded = await repository.LoadAsync(document.Id);
        Assert.NotNull(journalLoaded);
        Assert.Contains(journalLoaded.Pages[0].Objects, item => item.Id == stroke.Id);

        await repository.SaveAsync(document);
        var compacted = await repository.LoadAsync(document.Id);
        Assert.NotNull(compacted);
        Assert.Single(compacted.Pages[0].Objects, item => item.Id == stroke.Id);
    }

    [Fact]
    public async Task Repository_IncrementalSavePreservesUnchangedPagesAndDeletesRemovedPages()
    {
        await using var repository = new SqliteDocumentRepository(Path.Combine(_root, "incremental-library.db"));
        await repository.InitializeAsync();
        var document = HoomNoteDocument.Create("Incremental pages");
        var removed = AddPage(document);
        removed.Objects.Add(new RichTextObject { Content = RichTextDocument.FromPlainText("remove me") });
        var retained = AddPage(document);
        retained.Objects.Add(new RichTextObject { Content = RichTextDocument.FromPlainText("before") });
        await repository.SaveAsync(document);

        document.Pages.Remove(removed);
        document.Sections[0].PageIds.Remove(removed.Id);
        retained.Objects.Add(new RichTextObject { Content = RichTextDocument.FromPlainText("after") });
        retained.UpdatedAt = DateTimeOffset.UtcNow.AddMilliseconds(10);
        await repository.SaveAsync(document);

        var loaded = await repository.LoadAsync(document.Id);
        Assert.NotNull(loaded);
        var page = Assert.Single(loaded.Pages);
        Assert.Equal(retained.Id, page.Id);
        Assert.Contains(page.Objects.OfType<RichTextObject>(), item => item.Content.PlainText == "after");
        Assert.DoesNotContain(await repository.SearchAsync("remove me"), item => item.DocumentId == document.Id);
    }

    [Fact]
    public async Task AssetStore_DeduplicatesIdenticalContent()
    {
        var store = new ContentAddressedAssetStore(Path.Combine(_root, "assets"));
        var first = await store.AddAsync(new MemoryStream("same content"u8.ToArray()), ".pdf");
        var second = await store.AddAsync(new MemoryStream("same content"u8.ToArray()), "pdf");

        Assert.Equal(first, second);
        Assert.True(File.Exists(store.GetPath(first)));
        Assert.Single(Directory.EnumerateFiles(Path.Combine(_root, "assets")));
    }

    [Fact]
    public async Task Package_RoundTripsManifestAndReferencedAsset()
    {
        var store = new ContentAddressedAssetStore(Path.Combine(_root, "assets"));
        var hash = await store.AddAsync(new MemoryStream("%PDF placeholder"u8.ToArray()), ".pdf");
        var service = new HoomNotePackageService(store);
        var document = HoomNoteDocument.Create("Portable notes");
        AddPage(document).ImportedLayer = new ImportedDocumentLayer { AssetHash = hash, SourceName = "slides.pdf" };
        var path = Path.Combine(_root, "portable.hoomnote");

        await service.ExportAsync(document, path);
        var imported = await service.ImportAsync(path);

        Assert.True(File.Exists(path));
        Assert.NotEqual(document.Id, imported.Id);
        Assert.Equal("Portable notes (Imported)", imported.Title);
        Assert.Equal(hash, imported.Pages[0].ImportedLayer?.AssetHash);
    }

    [Fact]
    public async Task VectorExporter_WritesPdfAndSvgWithoutFlatteningInk()
    {
        var store = new ContentAddressedAssetStore(Path.Combine(_root, "assets"));
        var exporter = new VectorExportService(store);
        var document = HoomNoteDocument.Create("Vector export");
        var page = AddPage(document);
        page.Objects.Add(new InkStrokeObject
        {
            Points = [new InkPoint(10, 10, 0.5f), new InkPoint(100, 100, 0.9f)],
            Style = new InkStyle { Color = "#112233", Width = 4 }
        });
        page.Objects.Add(new InkStrokeObject
        {
            Points = [new InkPoint(10, 30, 0.3f), new InkPoint(100, 31, 0.8f)],
            Style = new InkStyle { Color = "#44AAEE", Width = 0.647f }
        });
        page.Objects.Add(new InkStrokeObject
        {
            Points = [new InkPoint(10, 50), new InkPoint(100, 51)],
            Style = new InkStyle
            {
                Color = "#CC55AA", Width = 0.647f, Smoothing = 0, PreserveSourceGeometry = true
            }
        });
        var imageBytes = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
        await using (var imageStream = new MemoryStream(imageBytes, writable: false))
        {
            var imageHash = await store.AddAsync(imageStream, ".png");
            page.Objects.Add(new ImageObject
            {
                AssetHash = imageHash,
                Bounds = new RectD(120, 20, 80, 80),
                AltText = "pixel"
            });
        }
        var pdf = Path.Combine(_root, "notes.pdf");
        var svg = Path.Combine(_root, "notes.svg");

        await exporter.ExportAsync(document, pdf, VectorExportFormat.Pdf);
        await exporter.ExportAsync(document, svg, VectorExportFormat.Svg);

        Assert.True(new FileInfo(pdf).Length > 100);
        var svgText = await File.ReadAllTextAsync(svg);
        Assert.Contains("<line", svgText);
        Assert.Contains("#112233", svgText);
        Assert.Contains("fill-rule=\"nonzero\"", svgText);
        Assert.Contains("stroke=\"#44AAEE\"", svgText);
        Assert.Contains("stroke-width=\"0.647\"", svgText);
        Assert.Contains("stroke=\"#CC55AA\"", svgText);
        Assert.Contains("stroke=\"#CC55AA\" stroke-opacity=\"1\" stroke-width=\"0.647\"", svgText);
        Assert.Contains("stroke-linecap=\"round\"", svgText);
        Assert.Contains("<image", svgText);
        Assert.Contains("data:image/png;base64,", svgText);
    }

    [Fact]
    public async Task PdfImport_ImportsEveryPageWhenRangeIsAll()
    {
        var source = Path.Combine(_root, "three-pages.pdf");
        using (var pdf = new PdfDocument())
        {
            pdf.AddPage();
            pdf.AddPage();
            pdf.AddPage();
            pdf.Save(source);
        }
        var store = new ContentAddressedAssetStore(Path.Combine(_root, "assets"));
        var importer = new DocumentImportService(store);

        var result = await importer.ImportAsync(new ImportRequest(source));

        Assert.Equal(3, result.Pages.Count);
        Assert.Equal(new[] { 0, 1, 2 }, result.Pages.Select(page => page.ImportedLayer!.SourcePageIndex));
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public async Task SamsungNotesImport_ProducesEditableVectorInk()
    {
        var source = Path.Combine(_root, "handwritten.sdocx");
        WriteSamsungNotesFixture(source);
        var store = new ContentAddressedAssetStore(Path.Combine(_root, "assets"));
        var importer = new DocumentImportService(store);

        var result = await importer.ImportAsync(new ImportRequest(source));

        var page = Assert.Single(result.Pages);
        var stroke = Assert.IsType<InkStrokeObject>(Assert.Single(page.Objects));
        Assert.True(stroke.Points.Count >= 3);
        Assert.All(stroke.Points, point =>
        {
            Assert.True(double.IsFinite(point.X));
            Assert.True(double.IsFinite(point.Y));
        });
        Assert.True(stroke.Style.PressureEnabled);
        Assert.True(stroke.Style.PreserveSourceGeometry);
        Assert.Equal("#112233", stroke.Style.Color);
        Assert.Equal(1.75f, stroke.Style.Width, 3);
        Assert.Equal("#F5DDEE", page.Template.PaperColor);
        Assert.InRange(stroke.Points.Max(point => point.X), 119.9, 120.1);
        Assert.Contains(result.Warnings, warning => warning.Contains("editable vector ink", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SamsungNotesImport_RealSamplesPreserveFidelityWhenAvailable()
    {
        var cases = new[]
        {
            (File: "samsung_note.sdocx", Pages: 5, MinimumStrokes: 5_500, Paper: "#FCFCFC"),
            (File: "samsung_note2.sdocx", Pages: 2, MinimumStrokes: 1_800, Paper: "#010101")
        };
        foreach (var item in cases)
        {
            var source = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
                "..", "..", "..", "..", "..", item.File));
            if (!File.Exists(source)) continue;
            var store = new ContentAddressedAssetStore(Path.Combine(_root, $"assets-{item.File}"));
            var importer = new DocumentImportService(store);

            var result = await importer.ImportAsync(new ImportRequest(source));

            Assert.Equal(item.Pages, result.Pages.Count);
            var strokes = result.Pages.SelectMany(page => page.Objects).OfType<InkStrokeObject>().ToArray();
            Assert.True(strokes.Length > item.MinimumStrokes);
            Assert.True(strokes.Select(stroke => stroke.Style.Color).Distinct().Count() > 10);
            Assert.True(strokes.Count(stroke => stroke.Style.Tool != InkToolKind.Highlighter && stroke.Style.Width <= 1.35f) > 1_000);
            Assert.True(strokes.Count(stroke => stroke.Style.PressureEnabled) > 100);
            Assert.All(strokes, stroke => Assert.Equal(0, stroke.Style.Smoothing));
            Assert.All(strokes, stroke => Assert.True(stroke.Style.PreserveSourceGeometry));
            Assert.Equal(item.Paper, result.Pages[0].Template.PaperColor);
            if (item.File == "samsung_note.sdocx")
            {
                var images = result.Pages.SelectMany(page => page.Objects).OfType<ImageObject>().ToArray();
                Assert.Equal(13, images.Length);
                Assert.All(images, image =>
                {
                    Assert.False(image.IsLocked);
                    Assert.True(image.PreserveAspectRatio);
                    Assert.True(File.Exists(store.GetPath(image.AssetHash)));
                    Assert.True(image.Bounds.Width > 0);
                    Assert.True(image.Bounds.Height > 0);
                });
            }
            Assert.All(strokes.SelectMany(stroke => stroke.Points), point =>
            {
                Assert.True(double.IsFinite(point.X));
                Assert.True(double.IsFinite(point.Y));
                Assert.InRange(point.X, -1, 1_632);
                Assert.InRange(point.Y, -1, 3_000);
            });
        }
    }

    [Fact]
    public async Task UserSettings_PersistPaletteAcrossStoreInstances()
    {
        var path = Path.Combine(_root, "settings.json");
        var folder = new NotebookFolderPreference { Name = "School", Color = "#7C6CFF" };
        var documentId = Guid.NewGuid();
        var preferences = new UserPreferences
        {
            SavedInkColors = ["#111111", "#C23B22", "#2E86DE"],
            PenColor = "#2E86DE",
            HighlighterColor = "#FFCE56",
            HighlighterStraightLine = true,
            ToolbarPresets = [new ToolbarPresetPreference
            {
                Tool = "Highlighter", Color = "#FFCE56", Width = 8,
                PressureSensitivity = 40, Opacity = 0.34, Smoothing = 0.8, StraightLine = true
            }],
            NotebookFolders = [folder],
            DocumentColors = new Dictionary<string, string>
            {
                [documentId.ToString("D")] = "#4BAEFF"
            },
            NotebookOrder = [documentId.ToString("D")],
            DefaultPageTemplate = "Graph",
            DefaultPageColor = "#202124",
            DocumentFolders = new Dictionary<string, string>
            {
                [documentId.ToString("D")] = folder.Id.ToString("D")
            }
        };

        await new LocalUserSettingsStore(path).SaveAsync(preferences);
        var loaded = await new LocalUserSettingsStore(path).LoadAsync();

        Assert.Equal(preferences.SavedInkColors, loaded.SavedInkColors);
        Assert.Equal("#2E86DE", loaded.PenColor);
        Assert.Equal("#FFCE56", loaded.HighlighterColor);
        Assert.True(loaded.HighlighterStraightLine);
        Assert.Equal(UserPreferences.CurrentVersion, loaded.Version);
        var preset = Assert.Single(loaded.ToolbarPresets);
        Assert.Equal("Highlighter", preset.Tool);
        Assert.Equal(40, preset.PressureSensitivity);
        Assert.Equal(0.34, preset.Opacity, 3);
        Assert.Equal(0.8, preset.Smoothing, 3);
        Assert.True(preset.StraightLine);
        Assert.Equal("School", Assert.Single(loaded.NotebookFolders).Name);
        Assert.Equal("#7C6CFF", Assert.Single(loaded.NotebookFolders).Color);
        Assert.Equal(folder.Id.ToString("D"), loaded.DocumentFolders[documentId.ToString("D")]);
        Assert.Equal("#4BAEFF", loaded.DocumentColors[documentId.ToString("D")]);
        Assert.Equal(documentId.ToString("D"), Assert.Single(loaded.NotebookOrder));
        Assert.Equal("Graph", loaded.DefaultPageTemplate);
        Assert.Equal("#202124", loaded.DefaultPageColor);
    }

    private static NotePage AddPage(HoomNoteDocument document)
    {
        var page = new NotePage();
        document.Pages.Add(page);
        document.Sections[0].PageIds.Add(page.Id);
        return page;
    }

    private static void WriteSamsungNotesFixture(string path)
    {
        var objectBytes = new byte[232];
        WriteUInt32(objectBytes, 0, (uint)objectBytes.Length);
        WriteUInt32(objectBytes, 6, 80);
        objectBytes[10] = 2;
        WriteDouble(objectBytes, 32, 100);
        WriteDouble(objectBytes, 40, 80);
        WriteDouble(objectBytes, 48, 120);
        WriteDouble(objectBytes, 56, 82);
        WriteUInt32(objectBytes, 130, 5);
        // The high magnitude bits are significant. Each 0x0140 word is a +10px move;
        // decoding only the low byte would incorrectly shrink it to +2px.
        objectBytes[156] = 64;
        objectBytes[157] = 0x01;
        objectBytes[160] = 64;
        objectBytes[161] = 0x01;
        // Four pressure deltas begin after the coordinate stream and its four-byte gap.
        for (var offset = 176; offset < 184; offset += 2) objectBytes[offset] = 100;
        // Last style marker: BGRA color followed by the true Samsung pen width.
        objectBytes[212] = 0x03;
        objectBytes[213] = 0x00;
        objectBytes[214] = 0x01;
        objectBytes[215] = 0x00;
        objectBytes[216] = 0x00;
        objectBytes[217] = 0x00;
        objectBytes[218] = 0x33;
        objectBytes[219] = 0x22;
        objectBytes[220] = 0x11;
        objectBytes[221] = 0xFF;
        BinaryPrimitives.WriteSingleLittleEndian(objectBytes.AsSpan(222), 1.75f);

        var pageBytes = new byte[359];
        WriteUInt32(pageBytes, 0, 64);
        WriteUInt32(pageBytes, 0x16, 816);
        WriteUInt32(pageBytes, 0x1A, 1056);
        WriteUInt16(pageBytes, 64, 1);
        WriteUInt32(pageBytes, 84, 1);
        pageBytes[88] = 1;
        WriteUInt32(pageBytes, 91, (uint)objectBytes.Length);
        objectBytes.CopyTo(pageBytes, 95);

        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        var entry = archive.CreateEntry("fixture.page");
        using var stream = entry.Open();
        stream.Write(pageBytes);
        stream.Dispose();
        var metadata = archive.CreateEntry("note.note");
        using var metadataStream = metadata.Open();
        metadataStream.Write([0x18, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0xF5, 0xDD, 0xEE, 0xFF]);
    }

    private static void WriteUInt16(Span<byte> bytes, int offset, ushort value) =>
        BinaryPrimitives.WriteUInt16LittleEndian(bytes[offset..], value);

    private static void WriteUInt32(Span<byte> bytes, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[offset..], value);

    private static void WriteDouble(Span<byte> bytes, int offset, double value) =>
        BinaryPrimitives.WriteInt64LittleEndian(bytes[offset..], BitConverter.DoubleToInt64Bits(value));
}
