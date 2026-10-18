using VIZEN.App.ViewModels;

namespace VIZEN.Tests;

public class ToastTests
{
    [Fact]
    public void ShortTextStaysAtLeastFiveSeconds() =>
        Assert.Equal(TimeSpan.FromSeconds(5), ToastHostViewModel.LifetimeFor("Saved", "Done."));

    [Fact]
    public void LongerTextStaysLonger()
    {
        var text = new string('x', 150);
        Assert.Equal(TimeSpan.FromSeconds(12), ToastHostViewModel.LifetimeFor("", text)); // 2 s + 150 / 15
    }

    [Fact]
    public void VeryLongTextIsCappedAtFifteenSeconds() =>
        Assert.Equal(TimeSpan.FromSeconds(15), ToastHostViewModel.LifetimeFor("Title", new string('x', 1000)));
}
