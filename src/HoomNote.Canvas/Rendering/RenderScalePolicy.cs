namespace HoomNote.Canvas.Rendering;

/// <summary>
/// Chooses a bounded page-snapshot resolution and decides when that snapshot can be
/// displayed without upscaling on the current monitor. Document data remains vector;
/// this policy only controls the transient navigation representation.
/// </summary>
public static class RenderScalePolicy
{
    public const double BaselineDpi = 96d;
    public const int BytesPerPixel = 4;

    public static double ComputeSnapshotScale(
        double pageWidth,
        double pageHeight,
        long byteBudget,
        double minimumScale = 1d,
        double maximumScale = 3d)
    {
        if (!double.IsFinite(pageWidth) || !double.IsFinite(pageHeight) ||
            pageWidth <= 0 || pageHeight <= 0 || byteBudget <= 0)
            return minimumScale;

        var scale = Math.Sqrt(byteBudget / (pageWidth * pageHeight * BytesPerPixel));
        return Math.Clamp(scale, minimumScale, maximumScale);
    }

    public static long EstimateSnapshotBytes(double pageWidth, double pageHeight, double scale)
    {
        if (!double.IsFinite(pageWidth) || !double.IsFinite(pageHeight) ||
            !double.IsFinite(scale) || pageWidth <= 0 || pageHeight <= 0 || scale <= 0)
            return 0;

        var bytes = Math.Ceiling(pageWidth * scale) *
                    Math.Ceiling(pageHeight * scale) *
                    BytesPerPixel;
        return bytes >= long.MaxValue ? long.MaxValue : (long)bytes;
    }

    public static bool HasNativeDisplayResolution(
        double snapshotScale,
        double zoom,
        double displayDpi,
        double tolerance = 1.02d)
    {
        if (!double.IsFinite(snapshotScale) || !double.IsFinite(zoom) ||
            !double.IsFinite(displayDpi) || snapshotScale <= 0 || zoom <= 0 || displayDpi <= 0)
            return false;

        var requiredScale = zoom * displayDpi / BaselineDpi;
        return snapshotScale * Math.Max(1d, tolerance) >= requiredScale;
    }

    /// <summary>
    /// Rounds the required physical-pixel scale upward to a stable bucket. Tiles remain
    /// native-resolution throughout the bucket while small zoom changes do not discard the
    /// entire working set.
    /// </summary>
    public static double ComputeNativeTileScale(
        double zoom,
        double displayDpi,
        double scaleStep = 1.25d)
    {
        if (!double.IsFinite(zoom) || !double.IsFinite(displayDpi) ||
            !double.IsFinite(scaleStep) || zoom <= 0 || displayDpi <= 0 || scaleStep <= 1)
            return 1;

        var requiredScale = zoom * displayDpi / BaselineDpi;
        var exponent = Math.Ceiling(Math.Log(requiredScale) / Math.Log(scaleStep) - 1e-10);
        return Math.Max(1d / 16d, Math.Pow(scaleStep, exponent));
    }
}
