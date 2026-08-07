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

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    public void NavigationRefinementYieldsToImmediateInput(
        bool interactionActive,
        bool immediateInputRequested,
        bool expected)
    {
        Assert.Equal(expected,
            NavigationRefinementPolicy.CanBuildTile(interactionActive, immediateInputRequested));
    }

    [Fact]
    public void NavigationRefinementWaitsForInputToBecomeIdle()
    {
        Assert.False(NavigationRefinementPolicy.CanBuildTile(
            interactionActive: false,
            immediateInputRequested: false,
            inputIdle: false));
        Assert.True(NavigationRefinementPolicy.CanBuildTile(
            interactionActive: false,
            immediateInputRequested: false,
            inputIdle: true));
    }

    [Fact]
    public void RefinementIsPresentedOnlyWhenTheVisibleSetIsComplete()
    {
        Assert.False(NavigationRefinementPolicy.ShouldPresentTiles(6, 0));
        Assert.False(NavigationRefinementPolicy.ShouldPresentTiles(6, 5));
        Assert.True(NavigationRefinementPolicy.ShouldPresentTiles(6, 6));
    }

    [Fact]
    public void DenseRasterSamplesAreReducedWithoutLosingEndpoints()
    {
        var points = Enumerable.Range(0, 2_001)
            .Select(index => new InkPoint(index / 100d, 12, 0.5f, 0, 0, index))
            .ToArray();

        var sampled = StrokeRenderSampler.ForRaster(points, pixelsPerDocumentUnit: 2);

        Assert.True(sampled.Count < points.Length / 10);
        Assert.Equal(points[0], sampled[0]);
        Assert.Equal(points[^1], sampled[^1]);
    }

    [Fact]
    public void RasterSamplingKeepsShortCornersAndPressureChanges()
    {
        var points = new[]
        {
            new InkPoint(0, 0, 0.2f, 0, 0, 0),
            new InkPoint(0.05, 0, 0.2f, 0, 0, 1),
            new InkPoint(0.05, 0.4, 0.2f, 0, 0, 2),
            new InkPoint(0.5, 0.4, 0.8f, 0, 0, 3),
            new InkPoint(0.6, 0.4, 0.8f, 0, 0, 4)
        };

        var sampled = StrokeRenderSampler.ForRaster(points, pixelsPerDocumentUnit: 1);

        Assert.Contains(points[2], sampled);
        Assert.Contains(points[3], sampled);
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
