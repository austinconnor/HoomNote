namespace HoomNote.Canvas.Rendering;

public static class ImageDecodePolicy
{
    public const long PageBudgetBytes = 24L * 1024 * 1024;

    public static int MaximumLongEdge(int distinctImageCount) =>
        Math.Clamp((int)Math.Sqrt(PageBudgetBytes / (4d * Math.Max(1, distinctImageCount))), 1, 1600);
}
