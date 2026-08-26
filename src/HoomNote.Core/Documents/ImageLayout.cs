namespace HoomNote.Core.Documents;

public static class ImageLayout
{
    public static RectD Destination(
        RectD bounds,
        double sourceWidth,
        double sourceHeight,
        bool preserveAspectRatio)
    {
        if (!preserveAspectRatio ||
            !bounds.IsFinite || bounds.Width <= 0 || bounds.Height <= 0 ||
            !double.IsFinite(sourceWidth) || sourceWidth <= 0 ||
            !double.IsFinite(sourceHeight) || sourceHeight <= 0)
            return bounds;

        var scale = Math.Min(bounds.Width / sourceWidth, bounds.Height / sourceHeight);
        var width = sourceWidth * scale;
        var height = sourceHeight * scale;
        return new RectD(
            bounds.X + (bounds.Width - width) / 2d,
            bounds.Y + (bounds.Height - height) / 2d,
            width,
            height);
    }
}
