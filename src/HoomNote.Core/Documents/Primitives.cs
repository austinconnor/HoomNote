using System.Numerics;

namespace HoomNote.Core.Documents;

public readonly record struct PointD(double X, double Y)
{
    public Vector2 ToVector2() => new((float)X, (float)Y);
}

public readonly record struct SizeD(double Width, double Height);

public readonly record struct RectD(double X, double Y, double Width, double Height)
{
    public double Left => X;
    public double Top => Y;
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public PointD Center => new(X + Width / 2d, Y + Height / 2d);

    public bool Contains(PointD point) =>
        point.X >= Left && point.X <= Right && point.Y >= Top && point.Y <= Bottom;

    public bool Intersects(RectD other) =>
        Left <= other.Right && Right >= other.Left && Top <= other.Bottom && Bottom >= other.Top;

    public RectD Inflate(double amount) =>
        new(X - amount, Y - amount, Width + amount * 2d, Height + amount * 2d);

    public static RectD FromPoints(IEnumerable<PointD> points)
    {
        var materialized = points as IReadOnlyCollection<PointD> ?? points.ToArray();
        if (materialized.Count == 0)
        {
            return default;
        }

        var minX = materialized.Min(point => point.X);
        var minY = materialized.Min(point => point.Y);
        var maxX = materialized.Max(point => point.X);
        var maxY = materialized.Max(point => point.Y);
        return new RectD(minX, minY, maxX - minX, maxY - minY);
    }
}

public readonly record struct Transform2D(
    double M11,
    double M12,
    double M21,
    double M22,
    double M31,
    double M32)
{
    public static Transform2D Identity => new(1, 0, 0, 1, 0, 0);

    public bool IsFinite =>
        double.IsFinite(M11) && double.IsFinite(M12) && double.IsFinite(M21) &&
        double.IsFinite(M22) && double.IsFinite(M31) && double.IsFinite(M32);

    public Matrix3x2 ToMatrix() => new(
        (float)M11, (float)M12, (float)M21, (float)M22, (float)M31, (float)M32);

    public PointD Apply(PointD point)
    {
        var transformed = Vector2.Transform(point.ToVector2(), ToMatrix());
        return new PointD(transformed.X, transformed.Y);
    }

    public static Transform2D FromMatrix(Matrix3x2 matrix) => new(
        matrix.M11, matrix.M12, matrix.M21, matrix.M22, matrix.M31, matrix.M32);

    public static Transform2D Translation(double x, double y) =>
        FromMatrix(Matrix3x2.CreateTranslation((float)x, (float)y));

    public static Transform2D Scale(double x, double y, PointD center) =>
        FromMatrix(Matrix3x2.CreateScale((float)x, (float)y, center.ToVector2()));

    public static Transform2D Rotation(double radians, PointD center) =>
        FromMatrix(Matrix3x2.CreateRotation((float)radians, center.ToVector2()));

    public Transform2D Then(Transform2D next) => FromMatrix(ToMatrix() * next.ToMatrix());
}

