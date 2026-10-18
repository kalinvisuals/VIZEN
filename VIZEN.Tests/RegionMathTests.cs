using System.Windows;
using VIZEN.App.Models;

namespace VIZEN.Tests;

public class RegionMathTests
{
    [Fact]
    public void FromPoints_OrdersAndClipsToUnitSquare()
    {
        var r = RegionMath.FromPoints(new Point(1.2, 0.8), new Point(-0.3, 0.2));
        Assert.Equal(new Rect(0, 0.2, 1, 0.6000000000000001), r);
    }

    [Fact]
    public void ClampInside_KeepsSizeAndMovesInside()
    {
        var r = RegionMath.ClampInside(new Rect(0.9, -0.1, 0.3, 0.4));
        Assert.Equal(0.7, r.X, 6);
        Assert.Equal(0, r.Y, 6);
        Assert.Equal(0.3, r.Width, 6);
        Assert.Equal(0.4, r.Height, 6);
    }

    [Fact]
    public void WithAspect_ProducesRequestedRatioInsideSquare()
    {
        var r = RegionMath.WithAspect(new Rect(0.1, 0.1, 0.5, 0.2), 2.0);
        Assert.Equal(2.0, r.Width / r.Height, 6);
        Assert.True(r.Left >= 0 && r.Top >= 0 && r.Right <= 1 && r.Bottom <= 1);
    }

    [Fact]
    public void NormalizedAspect_AccountsForSourceShape()
    {
        // 16:9 pixels on a 1600x900 source = a normalized square.
        Assert.Equal(1.0, RegionMath.NormalizedAspect(16.0 / 9.0, 1600, 900), 6);
    }

    [Theory]
    [InlineData(0, 0, 1, 1, true)]
    [InlineData(0, 0, 0.5, 1, false)]
    public void IsFull(double x, double y, double w, double h, bool expected) =>
        Assert.Equal(expected, RegionMath.IsFull(new Rect(x, y, w, h)));
}
