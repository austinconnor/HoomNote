using HoomNote.Canvas.Rendering;

namespace HoomNote.Core.Tests;

public sealed class ImageDecodePolicyTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(16)]
    [InlineData(100)]
    [InlineData(10000)]
    public void FullPageOfSquareImagesFitsBudget(int count)
    {
        var edge = ImageDecodePolicy.MaximumLongEdge(count);
        Assert.InRange(edge, 1, 1600);
        Assert.True((long)edge * edge * 4 * count <= ImageDecodePolicy.PageBudgetBytes);
    }
}
