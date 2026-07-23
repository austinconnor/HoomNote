using System.Text.Json.Serialization;

namespace HoomNote.Core.Documents;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(InkStrokeObject), "ink")]
[JsonDerivedType(typeof(RichTextObject), "text")]
[JsonDerivedType(typeof(ShapeObject), "shape")]
[JsonDerivedType(typeof(ImageObject), "image")]
[JsonDerivedType(typeof(GroupObject), "group")]
public abstract record CanvasObject
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public int ZIndex { get; init; }
    public bool IsLocked { get; init; }
    public bool IsHidden { get; init; }
    public Transform2D Transform { get; init; } = Transform2D.Identity;
    public abstract RectD LocalBounds { get; }
}

public sealed record InkStrokeObject : CanvasObject
{
    public List<InkPoint> Points { get; init; } = [];
    public InkStyle Style { get; init; } = new();
    public Guid? ParentStrokeId { get; init; }

    [JsonIgnore]
    public override RectD LocalBounds =>
        RectD.FromPoints(Points.Select(point => point.Position)).Inflate(Style.Normalize().Width / 2d);
}

public enum ParagraphKind
{
    Body,
    Heading1,
    Heading2,
    Bullet,
    Numbered,
    Checkbox
}

public enum TextAlignmentKind
{
    Left,
    Center,
    Right,
    Justify
}

public sealed record TextRun
{
    public string Text { get; init; } = string.Empty;
    public bool Bold { get; init; }
    public bool Italic { get; init; }
    public bool Underline { get; init; }
    public string Color { get; init; } = "#20242D";
    public string? Link { get; init; }
}

public sealed record RichParagraph
{
    public ParagraphKind Kind { get; init; }
    public TextAlignmentKind Alignment { get; init; }
    public bool IsChecked { get; init; }
    public List<TextRun> Runs { get; init; } = [];
}

public sealed record RichTextDocument
{
    public string FontFamily { get; init; } = "Segoe UI Variable Text";
    public float FontSize { get; init; } = 18;
    public List<RichParagraph> Paragraphs { get; init; } = [];

    [JsonIgnore]
    public string PlainText => string.Join(Environment.NewLine,
        Paragraphs.Select(paragraph => string.Concat(paragraph.Runs.Select(run => run.Text))));

    public static RichTextDocument FromPlainText(string text) => new()
    {
        Paragraphs = text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n')
            .Select(line => new RichParagraph { Runs = [new TextRun { Text = line }] })
            .ToList()
    };
}

public sealed record RichTextObject : CanvasObject
{
    public RectD Bounds { get; init; } = new(80, 80, 320, 160);
    public RichTextDocument Content { get; init; } = RichTextDocument.FromPlainText("Type your note…");
    public override RectD LocalBounds => Bounds;
}

public enum ShapeKind
{
    Line,
    Rectangle,
    RoundedRectangle,
    Ellipse,
    Arrow,
    Triangle,
    Diamond
}

public sealed record ShapeObject : CanvasObject
{
    public ShapeKind Shape { get; init; } = ShapeKind.Rectangle;
    public RectD Bounds { get; init; } = new(0, 0, 100, 100);
    // Directed shapes retain the actual drag direction. Older documents fall back to Bounds.
    public PointD? StartPoint { get; init; }
    public PointD? EndPoint { get; init; }
    public string StrokeColor { get; init; } = "#F4F7FB";
    public string? FillColor { get; init; }
    public float StrokeWidth { get; init; } = 2;
    public override RectD LocalBounds => Bounds.Inflate(StrokeWidth / 2d);
}

public sealed record ImageObject : CanvasObject
{
    public string AssetHash { get; init; } = string.Empty;
    public RectD Bounds { get; init; } = new(0, 0, 320, 240);
    public string? AltText { get; init; }
    public bool PreserveAspectRatio { get; init; } = true;
    public override RectD LocalBounds => Bounds;
}

public sealed record GroupObject : CanvasObject
{
    public List<Guid> ChildIds { get; init; } = [];
    public RectD Bounds { get; init; }
    public override RectD LocalBounds => Bounds;
}
