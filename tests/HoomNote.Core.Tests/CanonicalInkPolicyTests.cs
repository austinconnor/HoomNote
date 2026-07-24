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
}
