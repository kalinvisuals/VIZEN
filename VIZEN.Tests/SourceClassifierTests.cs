using VIZEN.App.Models;

namespace VIZEN.Tests;

public class SourceClassifierTests
{
    [Theory]
    [InlineData("Discord", "General - Discord", SourceCategory.Calls)]
    [InlineData("Zoom", "Zoom Meeting", SourceCategory.Calls)]
    [InlineData("vlc", "movie.mp4 - VLC", SourceCategory.Media)]
    [InlineData("WindowsCamera", "Camera", SourceCategory.Cameras)]
    [InlineData("notepad", "notes.txt - Notepad", SourceCategory.Other)]
    public void ClassifiesByProcess(string process, string title, SourceCategory expected) =>
        Assert.Equal(expected, SourceClassifier.Classify(process, title));

    [Theory]
    // Real titles seen during testing (Facebook Messenger call in Chrome, Romanian UI).
    [InlineData("Apel Messenger – Google Chrome", SourceCategory.Calls)]
    [InlineData("Facebook – Google Chrome", SourceCategory.Browsers)]
    [InlineData("Meet - abc-defg-hij - Google Chrome", SourceCategory.Calls)]
    [InlineData("Video call - Microsoft Edge", SourceCategory.Calls)]
    [InlineData("Lofi radio - YouTube - Google Chrome", SourceCategory.Media)]
    [InlineData("Docs - Google Chrome", SourceCategory.Browsers)]
    public void ClassifiesBrowserByActiveTabTitle(string title, SourceCategory expected) =>
        Assert.Equal(expected, SourceClassifier.Classify("chrome", title));
}
