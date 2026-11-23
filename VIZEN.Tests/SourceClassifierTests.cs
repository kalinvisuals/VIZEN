using VIZEN.App.Models;

namespace VIZEN.Tests;

public class SourceClassifierTests
{
    [Theory]
    // Call apps have no category of their own.
    [InlineData("Discord", "General - Discord", SourceCategory.Other)]
    [InlineData("Zoom", "Zoom Meeting", SourceCategory.Other)]
    [InlineData("vlc", "movie.mp4 - VLC", SourceCategory.Media)]
    [InlineData("WindowsCamera", "Camera", SourceCategory.Cameras)]
    [InlineData("notepad", "notes.txt - Notepad", SourceCategory.Other)]
    public void ClassifiesByProcess(string process, string title, SourceCategory expected) =>
        Assert.Equal(expected, SourceClassifier.Classify(process, title));

    [Theory]
    // Real titles seen during testing (Facebook Messenger call in Chrome, Romanian UI).
    [InlineData("Apel Messenger – Google Chrome", SourceCategory.Browsers)]
    [InlineData("Facebook – Google Chrome", SourceCategory.Browsers)]
    [InlineData("Meet - abc-defg-hij - Google Chrome", SourceCategory.Browsers)]
    [InlineData("Video call - Microsoft Edge", SourceCategory.Browsers)]
    [InlineData("Lofi radio - YouTube - Google Chrome", SourceCategory.Media)]
    [InlineData("Docs - Google Chrome", SourceCategory.Browsers)]
    public void ClassifiesBrowserByActiveTabTitle(string title, SourceCategory expected) =>
        Assert.Equal(expected, SourceClassifier.Classify("chrome", title));
}
