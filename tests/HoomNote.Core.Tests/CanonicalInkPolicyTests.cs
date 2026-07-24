using HoomNote.Canvas.Rendering;
using HoomNote.Core.Documents;

namespace HoomNote.Core.Tests;

public sealed class CanonicalInkPolicyTests
{
    [Fact]
    public void CapturePolicyHasNoViewportDependentInput()
    {
        var previous = new InkPoint(10, 20, 0.5f, 0, 0, 1);
        var close = new PointD(10.01, 20.01);
        var visible = new PointD(10.1, 20.1);

        Assert.False(CanonicalInkPolicy.ShouldAccept(previous, close));
        Assert.True(CanonicalInkPolicy.ShouldAccept(previous, visible));
        Assert.True(CanonicalInkPolicy.ShouldAccept(previous, close, force: true));
    }

    [Theory]
    [InlineData(true, 0)]
    [InlineData(false, 1)]
    public void NavigationRefinementIsBoundedPerFrame(bool interactionActive, int expected)
    {
        Assert.Equal(expected, NavigationRefinementPolicy.TileBuildBudget(interactionActive));
    }

    [Fact]
    public void RefinementIsPresentedOnlyWhenTheVisibleSetIsComplete()
    {
        Assert.False(NavigationRefinementPolicy.ShouldPresentTiles(6, 0));
        Assert.False(NavigationRefinementPolicy.ShouldPresentTiles(6, 5));
        Assert.True(NavigationRefinementPolicy.ShouldPresentTiles(6, 6));
    }

    [Fact]
    public void AdjacentTilesHaveContiguousCoresAndOverlappingRenderGutters()
    {
        var left = NavigationTileMetrics.Create(0, 0, 512, 1_200, 900, 2);
        var right = NavigationTileMetrics.Create(1, 0, 512, 1_200, 900, 2);

        Assert.Equal(left.CorePixelLeft + left.CorePixelWidth, right.CorePixelLeft);
        Assert.True(left.RenderPixelLeft + left.RenderPixelWidth > right.RenderPixelLeft);
        Assert.True(right.RenderPixelLeft < right.CorePixelLeft);
    }
}
