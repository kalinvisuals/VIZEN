namespace VIZEN.App.Models;

public enum SourceCategory
{
    Browsers,
    Calls,
    Media,
    Cameras,
    Other,
}

/// <summary>
/// Heuristic categorisation from process name and window title. For browsers, only the window title
/// (= the active tab's title) is visible to VIZEN, so a browser window is put under Calls or Media when
/// its active tab looks like a call or a media site. Individual tabs are not detected.
/// </summary>
public static class SourceClassifier
{
    private static readonly HashSet<string> BrowserProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome", "msedge", "firefox", "brave", "opera", "vivaldi", "arc", "librewolf", "waterfox",
        "chromium", "thorium", "floorp", "zen", "iexplore", "browser", "yandex", "VIZEN Browser",
    };

    private static readonly HashSet<string> CallProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "discord", "discordptb", "discordcanary", "zoom", "teams", "ms-teams", "skype", "slack", "webex",
        "ciscocollabhost", "whatsapp", "whatsapp.root", "telegram", "signal", "messenger", "viber",
    };

    private static readonly HashSet<string> MediaProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "vlc", "mpc-hc", "mpc-hc64", "mpc-be", "mpc-be64", "potplayer", "potplayermini", "potplayermini64",
        "wmplayer", "microsoft.media.player", "spotify", "mpv", "kodi", "video.ui", "music.ui", "foobar2000",
        "aimp", "itunes", "plex", "stremio",
    };

    private static readonly HashSet<string> CameraProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "windowscamera",
    };

    // Matched against browser window titles (the active tab).
    private static readonly string[] CallTitleHints =
    [
        "Google Meet", "Messenger", "Zoom", "Microsoft Teams", "Discord", "WhatsApp", "Skype", "Jitsi", "Whereby", "Webex",
        // Generic call-window titles (e.g. Facebook's call pop-up), English and Romanian.
        "Video call", "Audio call", "Group call", "Apel video", "Apel audio", "Apel de grup", "Apel Messenger",
    ];

    private static readonly string[] CallTitlePrefixes = ["Meet - ", "Meet – "];

    private static readonly string[] MediaTitleHints =
    [
        "YouTube", "Twitch", "Kick", "Vimeo", "Spotify", "SoundCloud", "Netflix", "Prime Video", "Disney+",
    ];

    public static bool IsBrowser(string processName) => BrowserProcesses.Contains(processName);

    public static SourceCategory Classify(string processName, string title)
    {
        if (CameraProcesses.Contains(processName))
        {
            return SourceCategory.Cameras;
        }

        if (CallProcesses.Contains(processName))
        {
            return SourceCategory.Calls;
        }

        if (MediaProcesses.Contains(processName))
        {
            return SourceCategory.Media;
        }

        if (!BrowserProcesses.Contains(processName))
        {
            return SourceCategory.Other;
        }

        if (CallTitlePrefixes.Any(p => title.StartsWith(p, StringComparison.OrdinalIgnoreCase))
            || CallTitleHints.Any(h => title.Contains(h, StringComparison.OrdinalIgnoreCase)))
        {
            return SourceCategory.Calls;
        }

        if (MediaTitleHints.Any(h => title.Contains(h, StringComparison.OrdinalIgnoreCase)))
        {
            return SourceCategory.Media;
        }

        return SourceCategory.Browsers;
    }

    public static string Glyph(SourceCategory category) => category switch
    {
        SourceCategory.Browsers => "",
        SourceCategory.Calls => "",
        SourceCategory.Media => "",
        SourceCategory.Cameras => "",
        _ => "",
    };
}
