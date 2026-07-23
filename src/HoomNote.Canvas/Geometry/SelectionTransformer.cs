using System.Numerics;
using HoomNote.Core.Documents;

namespace HoomNote.Canvas.Geometry;

public enum TransformHandle
{
    None,
    Move,
    TopLeft,
    Top,
    TopRight,
    Right,
    BottomRight,
    Bottom,
    BottomLeft,
    Left,
    Rotate
}

public static class SelectionTransformer
{
    public static IReadOnlyDictionary<TransformHandle, PointD> GetHandles(RectD bounds, double rotationOffset = 32) =>
        new Dictionary<TransformHandle, PointD>
        {
            [TransformHandle.TopLeft] = new(bounds.Left, bounds.Top),
            [TransformHandle.Top] = new(bounds.Center.X, bounds.Top),
            [TransformHandle.TopRight] = new(bounds.Right, bounds.Top),
            [TransformHandle.Right] = new(bounds.Right, bounds.Center.Y),
            [TransformHandle.BottomRight] = new(bounds.Right, bounds.Bottom),
            [TransformHandle.Bottom] = new(bounds.Center.X, bounds.Bottom),
            [TransformHandle.BottomLeft] = new(bounds.Left, bounds.Bottom),
            [TransformHandle.Left] = new(bounds.Left, bounds.Center.Y),
            [TransformHandle.Rotate] = new(bounds.Center.X, bounds.Top - rotationOffset)
        };

    public static TransformHandle HitHandle(RectD bounds, PointD point, double radius = 12)
    {
        foreach (var handle in GetHandles(bounds))
        {
            if (Vector2.Distance(handle.Value.ToVector2(), point.ToVector2()) <= radius)
                return handle.Key;
        }

        return bounds.Contains(point) ? TransformHandle.Move : TransformHandle.None;
    }

    public static Transform2D CreateTransform(
        TransformHandle handle,
        RectD originalBounds,
        PointD start,
        PointD current,
        bool preserveAspect = false)
    {
        if (handle == TransformHandle.Move)
            return Transform2D.Translation(current.X - start.X, current.Y - start.Y);

        if (handle == TransformHandle.Rotate)
        {
            var center = originalBounds.Center;
            var startAngle = Math.Atan2(start.Y - center.Y, start.X - center.X);
            var currentAngle = Math.Atan2(current.Y - center.Y, current.X - center.X);
            return Transform2D.Rotation(currentAngle - startAngle, center);
        }

        var anchor = OppositeAnchor(handle, originalBounds);
        var originalX = Math.Abs(start.X - anchor.X) < 0.001 ? 1 : start.X - anchor.X;
        var originalY = Math.Abs(start.Y - anchor.Y) < 0.001 ? 1 : start.Y - anchor.Y;
        var scaleX = UsesHorizontal(handle) ? (current.X - anchor.X) / originalX : 1;
        var scaleY = UsesVertical(handle) ? (current.Y - anchor.Y) / originalY : 1;
        scaleX = ClampScale(scaleX);
        scaleY = ClampScale(scaleY);

        if (preserveAspect && UsesHorizontal(handle) && UsesVertical(handle))
        {
            var magnitude = Math.Max(Math.Abs(scaleX), Math.Abs(scaleY));
            scaleX = Math.CopySign(magnitude, scaleX);
            scaleY = Math.CopySign(magnitude, scaleY);
        }

        return Transform2D.Scale(scaleX, scaleY, anchor);
    }

    private static PointD OppositeAnchor(TransformHandle handle, RectD bounds) => handle switch
    {
        TransformHandle.TopLeft => new(bounds.Right, bounds.Bottom),
        TransformHandle.Top => new(bounds.Center.X, bounds.Bottom),
        TransformHandle.TopRight => new(bounds.Left, bounds.Bottom),
        TransformHandle.Right => new(bounds.Left, bounds.Center.Y),
        TransformHandle.BottomRight => new(bounds.Left, bounds.Top),
        TransformHandle.Bottom => new(bounds.Center.X, bounds.Top),
        TransformHandle.BottomLeft => new(bounds.Right, bounds.Top),
        TransformHandle.Left => new(bounds.Right, bounds.Center.Y),
        _ => bounds.Center
    };

    private static bool UsesHorizontal(TransformHandle handle) => handle is
        TransformHandle.TopLeft or TransformHandle.TopRight or TransformHandle.Right or
        TransformHandle.BottomRight or TransformHandle.BottomLeft or TransformHandle.Left;

    private static bool UsesVertical(TransformHandle handle) => handle is
        TransformHandle.TopLeft or TransformHandle.Top or TransformHandle.TopRight or
        TransformHandle.BottomRight or TransformHandle.Bottom or TransformHandle.BottomLeft;

    private static double ClampScale(double value) =>
        Math.CopySign(Math.Clamp(Math.Abs(double.IsFinite(value) ? value : 1), 0.02, 100), value);
}

