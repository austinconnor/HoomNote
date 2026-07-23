namespace HoomNote.Core.Documents;

public enum InkToolKind
{
    Pen,
    Pencil,
    Highlighter
}

// Samples are immutable values, not identity-bearing objects. Keeping them inline in List<T>
// removes one managed object plus one reference per sample (hundreds of thousands on imported
// pages), substantially lowering GC pressure and improving sequential geometry traversal.
public readonly record struct InkPoint(
    double X,
    double Y,
    float Pressure = 0.5f,
    float TiltX = 0,
    float TiltY = 0,
    long TimestampMicroseconds = 0)
{
    public PointD Position => new(X, Y);

    public InkPoint Normalize() => this with
    {
        Pressure = Math.Clamp(float.IsFinite(Pressure) ? Pressure : 0.5f, 0.01f, 1f),
        TiltX = Math.Clamp(float.IsFinite(TiltX) ? TiltX : 0, -90, 90),
        TiltY = Math.Clamp(float.IsFinite(TiltY) ? TiltY : 0, -90, 90)
    };
}

public sealed record InkStyle
{
    public InkToolKind Tool { get; init; } = InkToolKind.Pen;
    public string Color { get; init; } = "#111111";
    public float Width { get; init; } = 2.4f;
    public float Opacity { get; init; } = 1f;
    public bool PressureEnabled { get; init; } = true;
    public float PressureSensitivity { get; init; } = 0.85f;
    public float Smoothing { get; init; } = 0.72f;
    public bool PreserveSourceGeometry { get; init; }

    public InkStyle Normalize() => this with
    {
        Width = Math.Clamp(float.IsFinite(Width) ? Width : 2.4f, 0.25f, 96f),
        // Pen and pencil are opaque pigments. Alpha accumulation changes their selected color
        // wherever strokes overlap; only the highlighter intentionally uses translucent ink.
        Opacity = Tool == InkToolKind.Highlighter
            ? Math.Clamp(float.IsFinite(Opacity) ? Opacity : 0.34f, 0.02f, 1f)
            : 1f,
        PressureSensitivity = Math.Clamp(float.IsFinite(PressureSensitivity) ? PressureSensitivity : 0.85f, 0f, 1f),
        Smoothing = Math.Clamp(float.IsFinite(Smoothing) ? Smoothing : 0.72f, 0f, 1f)
    };
}
