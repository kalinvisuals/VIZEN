using System.Text.RegularExpressions;

namespace VIZEN.App.Models;

/// <summary>
/// A web page that VIZEN shows in its own window ("VIZEN Browser · name"), so it can be captured, cropped and
/// sent to overlays, OBS outputs and compositions like any other window. Persisted in browsers.json.
/// </summary>
public sealed class BrowserSource
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;

    /// <summary>Window client size in physical pixels.</summary>
    public int PixelWidth { get; set; } = 480;
    public int PixelHeight { get; set; } = 720;

    public double Zoom { get; set; } = 1.0;
    public bool Muted { get; set; }

    /// <summary>Show a normal title bar so the window can be moved; off = clean borderless window for capture.</summary>
    public bool Framed { get; set; }

    public bool AutoOpen { get; set; }

    public double Left { get; set; }
    public double Top { get; set; }
}

/// <summary>URL helpers: normalizing typed addresses and finding the pop-out chat of a YouTube video or Twitch channel.</summary>
public static partial class BrowserUrls
{
    /// <summary>Adds https:// when no scheme was typed; only http and https pages are allowed.</summary>
    public static string? Normalize(string? input)
    {
        var text = input?.Trim();
        if (string.IsNullOrEmpty(text) || text.Contains(' '))
        {
            return null;
        }

        if (!text.Contains("://", StringComparison.Ordinal))
        {
            text = "https://" + text;
        }

        return Uri.TryCreate(text, UriKind.Absolute, out var uri)
               && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
               && uri.Host.Contains('.')
            ? uri.AbsoluteUri
            : null;
    }

    /// <summary>The 11-character video id of a YouTube watch/live/shorts/embed/youtu.be link, or null.</summary>
    public static string? YouTubeVideoId(string? url)
    {
        if (Normalize(url) is not { } normalized)
        {
            return null;
        }

        var uri = new Uri(normalized);
        var host = uri.Host.ToLowerInvariant();
        if (host == "youtu.be")
        {
            return ValidId(uri.AbsolutePath.Trim('/'));
        }

        if (!host.EndsWith("youtube.com", StringComparison.Ordinal))
        {
            return null;
        }

        var query = QueryValue(uri.Query, "v");
        if (query is not null)
        {
            return ValidId(query);
        }

        var match = PathIdRegex().Match(uri.AbsolutePath);
        return match.Success ? ValidId(match.Groups[1].Value) : null;
    }

    public static string YouTubeChatUrl(string videoId) => $"https://www.youtube.com/live_chat?is_popout=1&v={videoId}";

    /// <summary>The channel name of a twitch.tv/channel link, or null.</summary>
    public static string? TwitchChannel(string? url)
    {
        if (Normalize(url) is not { } normalized)
        {
            return null;
        }

        var uri = new Uri(normalized);
        if (!uri.Host.EndsWith("twitch.tv", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var first = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return first is not null && TwitchNameRegex().IsMatch(first) && first is not ("directory" or "videos" or "popout" or "settings")
            ? first.ToLowerInvariant()
            : null;
    }

    public static string TwitchChatUrl(string channel) => $"https://www.twitch.tv/popout/{channel}/chat?popout=";

    private static string? ValidId(string candidate) => VideoIdRegex().IsMatch(candidate) ? candidate : null;

    private static string? QueryValue(string query, string name)
    {
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pieces = part.Split('=', 2);
            if (pieces.Length == 2 && pieces[0] == name)
            {
                return Uri.UnescapeDataString(pieces[1]);
            }
        }

        return null;
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{11}$")]
    private static partial Regex VideoIdRegex();

    [GeneratedRegex("^/(?:live|shorts|embed)/([^/?#]+)")]
    private static partial Regex PathIdRegex();

    [GeneratedRegex("^[A-Za-z0-9_]{3,25}$")]
    private static partial Regex TwitchNameRegex();
}
