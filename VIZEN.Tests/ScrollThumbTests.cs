using VIZEN.App.Converters;

namespace VIZEN.Tests;

public class ScrollThumbTests
{
    private static double Thumb(double viewport, double range, double track) => track * viewport / (range + viewport);

    [Fact]
    public void LongContentGetsAThumbOfAtLeastTheMinimum()
    {
        // A note 200 px tall showing 6 of 60 lines: WPF would draw a 20 px thumb, at the limit ~8 px for longer text.
        var viewport = MinThumbViewportConverter.MinThumbViewport(10, 0, 2000, 200, 36);
        Assert.Equal(36, Thumb(viewport, 2000, 200), 6);
    }

    [Fact]
    public void ShortContentKeepsItsNaturalThumb()
    {
        Assert.Equal(100, MinThumbViewportConverter.MinThumbViewport(100, 0, 100, 200, 36)); // thumb 100 px: unchanged
    }

    [Theory]
    [InlineData(double.NaN, 0, 100, 200)] // not inside a ScrollViewer
    [InlineData(50, 0, 0, 200)]           // nothing to scroll
    [InlineData(10, 0, 2000, 60)]         // track too short for a minimum
    public void UnusualInputsAreLeftAlone(double viewport, double minimum, double maximum, double track)
    {
        var result = MinThumbViewportConverter.MinThumbViewport(viewport, minimum, maximum, track, 36);
        Assert.True(result.Equals(viewport));
    }
}
