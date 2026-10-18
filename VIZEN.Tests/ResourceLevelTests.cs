using VIZEN.App.Services;
using VIZEN.App.ViewModels;

namespace VIZEN.Tests;

public class ResourceLevelTests
{
    [Theory]
    [InlineData(0.3, ResourceLevel.Low)]
    [InlineData(4.9, ResourceLevel.Low)]
    [InlineData(5.0, ResourceLevel.Medium)]
    [InlineData(14.9, ResourceLevel.Medium)]
    [InlineData(15.0, ResourceLevel.High)]
    public void VizenCpuLevels(double percent, ResourceLevel expected) =>
        Assert.Equal(expected, new ResourceTexts(new ResourceSnapshot(percent, 0, 0, 0, null, null)).VizenCpuLevel);

    [Fact]
    public void MemoryLevelIsTheShareOfThePcMemory()
    {
        // 250 MB of 16 GB = 1.5 % -> low; 3 GB of 16 GB = 18.75 % -> high.
        Assert.Equal(ResourceLevel.Low, new ResourceTexts(new ResourceSnapshot(0, 0, 250, 200, null, null), totalMemoryGb: 16).MemoryLevel);
        Assert.Equal(ResourceLevel.High, new ResourceTexts(new ResourceSnapshot(0, 0, 3072, 3000, null, null), totalMemoryGb: 16).MemoryLevel);
    }

    [Fact]
    public void NoLevelWithoutAValue()
    {
        var texts = new ResourceTexts(new ResourceSnapshot(0, 0, 250, 200, null, null));
        Assert.Equal(ResourceLevel.None, texts.GpuLevel);    // GPU not measured yet
        Assert.Equal(ResourceLevel.None, texts.MemoryLevel); // PC memory not known yet
    }
}
