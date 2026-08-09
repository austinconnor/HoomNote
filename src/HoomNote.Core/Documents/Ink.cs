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
public readonly record struct InkPoint
{
    private readonly float _x;
    private readonly float _y;
    private readonly uint _timestampMicroseconds;

    public double X
    {
        get => _x;
        init => _x = double.IsFinite(value) ? (float)value : 0;
    }

    public double Y
    {
        get => _y;
        init => _y = double.IsFinite(value) ? (float)value : 0;
    }

    public float Pressure { get; init; }
    public float TiltX { get; init; }
    public float TiltY { get; init; }
    public long TimestampMicroseconds
    {
        get => _timestampMicroseconds;
        init => _timestampMicroseconds = (uint)Math.Clamp(value, 0, uint.MaxValue);
    }

    public InkPoint(double X, double Y, float Pressure = 0.5f, float TiltX = 0, float TiltY = 0,
        long TimestampMicroseconds = 0)
    {
        _x = double.IsFinite(X) ? (float)X : 0;
        _y = double.IsFinite(Y) ? (float)Y : 0;
        this.Pressure = Pressure;
        this.TiltX = TiltX;
        this.TiltY = TiltY;
        _timestampMicroseconds = (uint)Math.Clamp(TimestampMicroseconds, 0, uint.MaxValue);
    }

    public PointD Position => new(X, Y);

    public InkPoint Normalize() => this with
    {
        X = double.IsFinite(X) ? X : 0,
        Y = double.IsFinite(Y) ? Y : 0,
        Pressure = Math.Clamp(float.IsFinite(Pressure) ? Pressure : 0.5f, 0.01f, 1f),
        TiltX = Math.Clamp(float.IsFinite(TiltX) ? TiltX : 0, -90, 90),
        TiltY = Math.Clamp(float.IsFinite(TiltY) ? TiltY : 0, -90, 90)
    };
}

public sealed record InkStyle
{
    public const float DefaultHighlighterOpacity = 0.60f;
    public const float HighlighterCompositingStrength = 0.76f;

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
        Width = Math.Clamp(float.IsFinite(Width) ? Width : 2.4f, 0.1f, 96f),
        // Pen and pencil are opaque pigments. Alpha accumulation changes their selected color
        // wherever strokes overlap; only the highlighter intentionally uses translucent ink.
        Opacity = Tool == InkToolKind.Highlighter
            ? Math.Clamp(float.IsFinite(Opacity) ? Opacity : DefaultHighlighterOpacity, 0.02f, 1f)
            : 1f,
        PressureSensitivity = Math.Clamp(float.IsFinite(PressureSensitivity) ? PressureSensitivity : 0.85f, 0f, 1f),
        Smoothing = Math.Clamp(float.IsFinite(Smoothing) ? Smoothing : 0.72f, 0f, 1f)
    };
}
