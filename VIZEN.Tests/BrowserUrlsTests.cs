using VIZEN.App.Models;

namespace VIZEN.Tests;

public class BrowserUrlsTests
{
    [Theory]
    [InlineData("youtube.com/watch?v=dQw4w9WgXcQ", "https://youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("  https://www.twitch.tv/somechannel ", "https://www.twitch.tv/somechannel")]
    [InlineData("http://example.org", "http://example.org/")]
    public void NormalizesAddresses(string input, string expected) => Assert.Equal(expected, BrowserUrls.Normalize(input));

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///C:/secret.txt")]
    [InlineData("not a url")]
    [InlineData("localhost")]
    [InlineData("")]
    public void RejectsUnsafeOrInvalid(string input) => Assert.Null(BrowserUrls.Normalize(input));

    [Theory]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ&t=10s", "dQw4w9WgXcQ")]
    [InlineData("https://youtu.be/dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/live/dQw4w9WgXcQ?si=abc", "dQw4w9WgXcQ")]
    [InlineData("https://youtube.com/shorts/dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/@channel", null)]
    [InlineData("https://example.com/watch?v=dQw4w9WgXcQ", null)]
    public void FindsYouTubeVideoIds(string url, string? expected) => Assert.Equal(expected, BrowserUrls.YouTubeVideoId(url));

    [Fact]
    public void BuildsChatUrls()
    {
        Assert.Equal("https://www.youtube.com/live_chat?is_popout=1&v=dQw4w9WgXcQ", BrowserUrls.YouTubeChatUrl("dQw4w9WgXcQ"));
        Assert.Equal("somechannel", BrowserUrls.TwitchChannel("https://www.twitch.tv/SomeChannel/videos"));
        Assert.Null(BrowserUrls.TwitchChannel("https://www.twitch.tv/directory"));
        Assert.Equal("https://www.twitch.tv/popout/somechannel/chat?popout=", BrowserUrls.TwitchChatUrl("somechannel"));
    }
}
