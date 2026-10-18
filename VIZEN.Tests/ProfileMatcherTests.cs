using VIZEN.App.Models;
using VIZEN.App.Services;

namespace VIZEN.Tests;

public class ProfileMatcherTests
{
    private static WindowSource Window(int handle, string exe, string title, int z = 0) => new()
    {
        Handle = handle,
        ProcessName = Path.GetFileNameWithoutExtension(exe),
        ProcessId = (uint)handle,
        ExecutablePath = exe,
        ClassName = "Chrome_WidgetWin_1",
        Title = title,
        ZOrder = z,
    };

    private static OverlayLayout Layout(string exe, string title, string? region = null) => new()
    {
        ProcessName = Path.GetFileNameWithoutExtension(exe),
        ExecutablePath = exe,
        WindowTitle = title,
        ClassName = "Chrome_WidgetWin_1",
        RegionName = region,
    };

    [Fact]
    public void ExactTitleWinsOverSimilar()
    {
        var windows = new[] { Window(1, @"C:\chrome.exe", "YouTube - Chrome"), Window(2, @"C:\chrome.exe", "Twitch - Chrome") };
        var match = ProfileMatcher.Match([Layout(@"C:\chrome.exe", "Twitch - Chrome")], windows).Single();
        Assert.Equal(MatchQuality.Exact, match.Quality);
        Assert.Equal((IntPtr)2, match.Window!.Handle);
    }

    [Fact]
    public void RegionsOfOneWindowShareTheSameMatch()
    {
        var windows = new[] { Window(5, @"C:\zoom.exe", "Zoom Meeting") };
        var layouts = new[]
        {
            Layout(@"C:\zoom.exe", "Zoom Meeting", "Participant 1"),
            Layout(@"C:\zoom.exe", "Zoom Meeting", "Participant 2"),
        };
        var matches = ProfileMatcher.Match(layouts, windows);
        Assert.All(matches, m => Assert.Equal((IntPtr)5, m.Window!.Handle));
    }

    [Fact]
    public void DifferentAppIsNeverMatched()
    {
        var match = ProfileMatcher.Match([Layout(@"C:\discord.exe", "Discord")], [Window(1, @"C:\chrome.exe", "Discord")]).Single();
        Assert.Equal(MatchQuality.NotFound, match.Quality);
        Assert.Null(match.Window);
    }

    [Fact]
    public void ExactMatchIsNotStolenByVagueOne()
    {
        // Two saved windows of the same app; each must get its own window.
        var windows = new[] { Window(1, @"C:\chrome.exe", "Chat - YouTube"), Window(2, @"C:\chrome.exe", "Stream - YouTube") };
        var layouts = new[] { Layout(@"C:\chrome.exe", "Something else"), Layout(@"C:\chrome.exe", "Chat - YouTube") };
        var matches = ProfileMatcher.Match(layouts, windows);
        Assert.Equal((IntPtr)2, matches[0].Window!.Handle);
        Assert.Equal(MatchQuality.AppOnly, matches[0].Quality);
        Assert.Equal((IntPtr)1, matches[1].Window!.Handle);
        Assert.Equal(MatchQuality.Exact, matches[1].Quality);
    }
}
