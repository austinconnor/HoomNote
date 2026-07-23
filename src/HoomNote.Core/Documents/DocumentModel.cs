namespace HoomNote.Core.Documents;

public enum DocumentKind
{
    PagedNotebook,
    InfiniteCanvas
}

public enum PageTemplateKind
{
    Blank,
    Lined,
    Dotted,
    SquareGrid,
    Graph,
    DarkPaper
}

public sealed record PageTemplate
{
    public PageTemplateKind Kind { get; init; } = PageTemplateKind.Lined;
    public string PaperColor { get; init; } = "#FFFDF8";
    public string LineColor { get; init; } = "#D5DAE2";
    public double Spacing { get; init; } = 28;
    public double Margin { get; init; } = 52;
    public double LineWidth { get; init; } = 1;

    public static PageTemplate For(PageTemplateKind kind) => kind switch
    {
        PageTemplateKind.Blank => new() { Kind = kind },
        PageTemplateKind.Dotted => new() { Kind = kind, Spacing = 24 },
        PageTemplateKind.SquareGrid => new() { Kind = kind, Spacing = 24 },
        PageTemplateKind.Graph => new() { Kind = kind, Spacing = 20, LineColor = "#C7D8EA" },
        PageTemplateKind.DarkPaper => new()
        {
            Kind = kind,
            PaperColor = "#191C22",
            LineColor = "#343A46"
        },
        _ => new() { Kind = PageTemplateKind.Lined }
    };
}

public sealed record ImportedDocumentLayer
{
    public string AssetHash { get; init; } = string.Empty;
    public string SourceName { get; init; } = string.Empty;
    public int SourcePageIndex { get; init; }
    public bool IsVisible { get; init; } = true;
    public bool IsLocked { get; init; } = true;
    public RectD Crop { get; init; }
    public Transform2D Transform { get; init; } = Transform2D.Identity;
}

public sealed record RecognizedTextRegion
{
    public string Text { get; init; } = string.Empty;
    public RectD Bounds { get; init; }
}

public sealed record NotePage
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Title { get; set; } = "Page 1";
    public SizeD Size { get; init; } = new(816, 1056);
    public PageTemplate Template { get; set; } = PageTemplate.For(PageTemplateKind.Lined);
    public ImportedDocumentLayer? ImportedLayer { get; set; }
    public List<CanvasObject> Objects { get; init; } = [];
    public string RecognizedText { get; set; } = string.Empty;
    public List<RecognizedTextRegion> RecognizedRegions { get; set; } = [];
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed record NotebookSection
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Title { get; set; } = "Notes";
    public List<Guid> PageIds { get; init; } = [];
}

public sealed record DocumentSettings
{
    public bool DarkPaperByDefault { get; init; }
    public double DefaultZoom { get; init; } = 1;
    public string RecognitionLanguage { get; init; } = "en-US";
    public PageTemplateKind DefaultPageTemplateKind { get; init; } = PageTemplateKind.Lined;
    public string DefaultPaperColor { get; init; } = "#FFFDF8";
}

public sealed record HoomNoteDocument
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Title { get; set; } = "Untitled notebook";
    public DocumentKind Kind { get; init; } = DocumentKind.PagedNotebook;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<string> Tags { get; init; } = [];
    public List<NotebookSection> Sections { get; init; } = [];
    public List<NotePage> Pages { get; init; } = [];
    public DocumentSettings Settings { get; set; } = new();

    public static HoomNoteDocument Create(string title, DocumentKind kind = DocumentKind.PagedNotebook)
    {
        return new HoomNoteDocument
        {
            Title = title,
            Kind = kind,
            Pages = [],
            Sections = [new NotebookSection()]
        };
    }
}
