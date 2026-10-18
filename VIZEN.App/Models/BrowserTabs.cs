using System.Buffers.Binary;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VIZEN.App.Models;

/// <summary>
/// One tab reported by the VIZEN Connector browser extension. Only the title and the site name (host) are known:
/// the extension never sends full addresses, and never incognito tabs. A tab is not a window: Windows can only
/// capture windows, so a tab is captured by moving it into its own window (on the user's request) or through the
/// extension's live mirror window.
/// </summary>
public sealed record BrowserTab(string Browser, int Id, int WindowId, string Title, string Site, bool Audible, bool Muted, bool Active)
{
    /// <summary>"Calls" for Meet / Messenger tabs, "Media" for YouTube / Twitch, otherwise "Browsers".</summary>
    public SourceCategory Category => SourceClassifier.Classify(BrowserTabs.ProcessFor(Browser), $"{Title} - {Site}");

    /// <summary>Key of the browser in the mixer and in Sources ("chrome.exe").</summary>
    public string ProcessKey => BrowserTabs.ProcessFor(Browser) + ".exe";
}

/// <summary>Messages between VIZEN and the extension (JSON, the same through the native host and the pipe).</summary>
public sealed class BrowserMessage
{
    [JsonPropertyName("type")] public string Type { get; set; } = string.Empty;
    [JsonPropertyName("browser")] public string? Browser { get; set; }
    [JsonPropertyName("version")] public string? Version { get; set; }
    [JsonPropertyName("tabs")] public List<BrowserTabDto>? Tabs { get; set; }
    [JsonPropertyName("tabId")] public int? TabId { get; set; }
    [JsonPropertyName("muted")] public bool? Muted { get; set; }
    [JsonPropertyName("command")] public string? Command { get; set; }
    [JsonPropertyName("ok")] public bool? Ok { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
}

public sealed class BrowserTabDto
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("windowId")] public int WindowId { get; set; }
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("site")] public string? Site { get; set; }
    [JsonPropertyName("audible")] public bool Audible { get; set; }
    [JsonPropertyName("muted")] public bool Muted { get; set; }
    [JsonPropertyName("active")] public bool Active { get; set; }
}

public static class BrowserTabs
{
    /// <summary>Name of the native messaging host registered for Chrome, Edge and Brave.</summary>
    public const string HostName = "com.vizen.connector";

    /// <summary>ID of the VIZEN Connector extension loaded from its folder (fixed by the public key in its manifest).</summary>
    public const string ExtensionId = "dlokfgkopfanhmfkaocoaedeppojcheh";

    /// <summary>
    /// Every VIZEN Connector build that may talk to VIZEN: the one loaded from its folder and the ones installed from the
    /// stores (the stores give the extension their own ID).
    /// </summary>
    public static readonly string[] ExtensionIds = [ExtensionId, EdgeAddonsId];

    /// <summary>ID given by Microsoft Edge Add-ons (Partner Center, "CRX ID").</summary>
    public const string EdgeAddonsId = "gkingdeoabnhbnjpikdmklembdjoplfl";

    /// <summary>Largest message accepted from either side (a list of tabs is a few KB).</summary>
    public const int MaxMessageBytes = 1024 * 1024;

    public static readonly string[] KnownBrowsers = ["chrome", "edge", "brave"];

    public static string ProcessFor(string browser) => browser switch
    {
        "edge" => "msedge",
        "brave" => "brave",
        _ => "chrome",
    };

    public static string DisplayName(string browser) => browser switch
    {
        "edge" => "Microsoft Edge",
        "brave" => "Brave",
        _ => "Google Chrome",
    };

    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public static byte[] Serialize(BrowserMessage message) => JsonSerializer.SerializeToUtf8Bytes(message, Json);

    public static BrowserMessage? Deserialize(ReadOnlySpan<byte> json)
    {
        try
        {
            return JsonSerializer.Deserialize<BrowserMessage>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The tabs of one "tabs" message, cleaned up (unknown browser names become "chrome").</summary>
    public static List<BrowserTab> TabsOf(BrowserMessage message)
    {
        var browser = KnownBrowsers.Contains(message.Browser) ? message.Browser! : "chrome";
        return (message.Tabs ?? [])
            .Where(t => t is not null)
            .Select(t => new BrowserTab(browser, t.Id, t.WindowId, (t.Title ?? string.Empty).Trim(), (t.Site ?? string.Empty).Trim(), t.Audible, t.Muted, t.Active))
            .ToList();
    }

    // ----- Native messaging framing: a 32-bit little-endian length, then UTF-8 JSON (used on stdin/stdout and the pipe) -----

    public static async Task WriteFrameAsync(Stream stream, byte[] payload, CancellationToken cancel)
    {
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await stream.WriteAsync(header, cancel).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancel).ConfigureAwait(false);
        await stream.FlushAsync(cancel).ConfigureAwait(false);
    }

    /// <summary>Reads one frame; null at the end of the stream. Throws <see cref="InvalidDataException"/> on a bad length.</summary>
    public static async Task<byte[]?> ReadFrameAsync(Stream stream, CancellationToken cancel)
    {
        var header = new byte[4];
        if (!await ReadExactlyAsync(stream, header, cancel).ConfigureAwait(false))
        {
            return null;
        }

        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length < 0 || length > MaxMessageBytes)
        {
            throw new InvalidDataException($"message of {length} bytes");
        }

        var payload = new byte[length];
        return await ReadExactlyAsync(stream, payload, cancel).ConfigureAwait(false) ? payload : null;
    }

    private static async Task<bool> ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken cancel)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read), cancel).ConfigureAwait(false);
            if (n == 0)
            {
                return false;
            }

            read += n;
        }

        return true;
    }

    public static string Describe(BrowserMessage message) => Encoding.UTF8.GetString(Serialize(message));
}

/// <summary>The latest tabs of every connected browser (UI thread).</summary>
public sealed class BrowserTabStore
{
    private readonly Dictionary<string, List<BrowserTab>> _byBrowser = [];

    public IReadOnlyList<BrowserTab> All => _byBrowser.Values.SelectMany(t => t).ToList();

    public IReadOnlyCollection<string> Browsers => _byBrowser.Keys;

    /// <summary>Replaces the tabs of the message's browser; true when anything changed.</summary>
    public bool Apply(BrowserMessage message)
    {
        if (message.Type != "tabs")
        {
            return false;
        }

        var tabs = BrowserTabs.TabsOf(message);
        var browser = tabs.FirstOrDefault()?.Browser ?? (BrowserTabs.KnownBrowsers.Contains(message.Browser) ? message.Browser! : "chrome");
        if (_byBrowser.TryGetValue(browser, out var old) && old.SequenceEqual(tabs))
        {
            return false;
        }

        _byBrowser[browser] = tabs;
        return true;
    }

    /// <summary>A browser's extension disconnected (browser closed): its tabs go.</summary>
    public bool Remove(string browser) => _byBrowser.Remove(browser);
}
