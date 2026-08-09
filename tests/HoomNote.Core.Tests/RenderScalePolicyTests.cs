using HoomNote.Canvas.Rendering;

namespace HoomNote.Core.Tests;

public sealed class RenderScalePolicyTests
{
    [Fact]
    public void SnapshotScaleStaysWithinBudgetAndConfiguredBounds()
    {
        const long budget = 24L * 1024 * 1024;
        var scale = RenderScalePolicy.ComputeSnapshotScale(816, 1056, budget);

        Assert.InRange(scale, 1d / 16d, 3);
        Assert.True(RenderScalePolicy.EstimateSnapshotBytes(816, 1056, scale) <=
                    budget + 4 * 2048);
    }

    [Theory]
    [InlineData(2.0, 1.0, 192.0, true)]
    [InlineData(2.0, 1.25, 192.0, false)]
    [InlineData(1.5, 1.5, 96.0, true)]
    [InlineData(1.5, 2.0, 96.0, false)]
    public void NativeResolutionDecisionIncludesMonitorDpi(
        double snapshotScale,
        double zoom,
        double dpi,
        bool expected)
    {
        Assert.Equal(expected,
            RenderScalePolicy.HasNativeDisplayResolution(snapshotScale, zoom, dpi));
    }

    [Fact]
    public void InvalidInputsFailSafe()
    {
        Assert.Equal(1d / 16d, RenderScalePolicy.ComputeSnapshotScale(double.NaN, 100, 1024));
        Assert.False(RenderScalePolicy.HasNativeDisplayResolution(2, 1, 0));
        Assert.Equal(0, RenderScalePolicy.EstimateSnapshotBytes(-1, 100, 2));
        Assert.Equal(1, RenderScalePolicy.ComputeNativeTileScale(double.NaN, 96));
    }

    [Fact]
    public void OversizedPageCanScaleBelowOneToHonorSnapshotBudget()
    {
        const long budget = 24L * 1024 * 1024;
        var scale = RenderScalePolicy.ComputeSnapshotScale(3000, 3000, budget);

        Assert.True(scale < 1);
        Assert.True(RenderScalePolicy.EstimateSnapshotBytes(3000, 3000, scale) <= budget + 24_000);
    }

    [Theory]
    [InlineData(0.826, 288)]
    [InlineData(1.0, 96)]
    [InlineData(2.4, 192)]
    public void NativeTileScaleNeverUndersamplesAndStaysInOneBucketStep(
        double zoom,
        double dpi)
    {
        const double step = 1.25;
        var required = zoom * dpi / RenderScalePolicy.BaselineDpi;
        var scale = RenderScalePolicy.ComputeNativeTileScale(zoom, dpi, step);

        Assert.True(scale + 1e-9 >= required);
        Assert.True(scale <= required * step + 1e-9);
    }
}
