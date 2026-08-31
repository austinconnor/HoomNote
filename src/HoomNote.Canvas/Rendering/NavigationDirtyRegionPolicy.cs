using HoomNote.Core.Documents;

namespace HoomNote.Canvas.Rendering;

public static class NavigationDirtyRegionPolicy
{
    public static IReadOnlyList<RectD> Normalize(
        IEnumerable<RectD> regions,
        SizeD pageSize,
        double padding = 0)
    {
        if (!double.IsFinite(pageSize.Width) || !double.IsFinite(pageSize.Height) ||
            pageSize.Width <= 0 || pageSize.Height <= 0)
            return [];

        var result = new List<RectD>();
        foreach (var source in regions)
        {
            if (!source.IsFinite || source.Width < 0 || source.Height < 0) continue;
            var region = Clip(source.Inflate(Math.Max(0, padding)), pageSize);
            if (region.Width <= 0 || region.Height <= 0) continue;
            var merged = true;
            while (merged)
            {
                merged = false;
                for (var index = result.Count - 1; index >= 0; index--)
                {
                    if (!result[index].Intersects(region)) continue;
                    region = result[index].Union(region);
                    result.RemoveAt(index);
                    merged = true;
                }
            }
            result.Add(region);
        }
        return result;
    }

    public static HashSet<(int X, int Y)> TileKeys(
        IEnumerable<RectD> regions,
        SizeD pageSize,
        double scale,
        int tilePixels)
    {
        var result = new HashSet<(int X, int Y)>();
        if (!double.IsFinite(scale) || scale <= 0 || tilePixels <= 0 ||
            pageSize.Width <= 0 || pageSize.Height <= 0)
            return result;

        var fullPixelWidth = Math.Max(1, (int)Math.Ceiling(pageSize.Width * scale));
        var fullPixelHeight = Math.Max(1, (int)Math.Ceiling(pageSize.Height * scale));
        var maximumTileX = Math.Max(0, (fullPixelWidth - 1) / tilePixels);
        var maximumTileY = Math.Max(0, (fullPixelHeight - 1) / tilePixels);
        foreach (var region in Normalize(regions, pageSize))
        {
            var minimumX = Math.Clamp((int)Math.Floor(region.Left * scale / tilePixels),
                0, maximumTileX);
            var maximumX = Math.Clamp((int)Math.Floor(
                    Math.Max(region.Left, region.Right - 0.0001) * scale / tilePixels),
                minimumX, maximumTileX);
            var minimumY = Math.Clamp((int)Math.Floor(region.Top * scale / tilePixels),
                0, maximumTileY);
            var maximumY = Math.Clamp((int)Math.Floor(
                    Math.Max(region.Top, region.Bottom - 0.0001) * scale / tilePixels),
                minimumY, maximumTileY);
            for (var y = minimumY; y <= maximumY; y++)
            for (var x = minimumX; x <= maximumX; x++)
                result.Add((x, y));
        }
        return result;
    }

    private static RectD Clip(RectD region, SizeD pageSize)
    {
        var left = Math.Clamp(region.Left, 0, pageSize.Width);
        var top = Math.Clamp(region.Top, 0, pageSize.Height);
        var right = Math.Clamp(region.Right, left, pageSize.Width);
        var bottom = Math.Clamp(region.Bottom, top, pageSize.Height);
        return new RectD(left, top, right - left, bottom - top);
    }
}
