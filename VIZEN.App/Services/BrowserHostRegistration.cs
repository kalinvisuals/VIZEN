using System.IO;
using System.Text.Json;
using Microsoft.Win32;
using VIZEN.App.Models;

namespace VIZEN.App.Services;

/// <summary>Where browsers look for native messaging hosts (per user, no administrator rights).</summary>
public interface IHostRegistry
{
    string? Get(string keyPath);
    void Set(string keyPath, string value);
    void Delete(string keyPath);
}

/// <summary>HKEY_CURRENT_USER, the default value of each key.</summary>
public sealed class CurrentUserHostRegistry : IHostRegistry
{
    public string? Get(string keyPath)
    {
        using var key = Registry.CurrentUser.OpenSubKey(keyPath);
        return key?.GetValue(null) as string;
    }

    public void Set(string keyPath, string value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(keyPath);
        key.SetValue(null, value);
    }

    public void Delete(string keyPath) => Registry.CurrentUser.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false);
}

/// <summary>
/// Connects the VIZEN Connector extension to VIZEN, only when the user asks: writes the native messaging host
/// manifest (which program the browser may start, and that only the VIZEN extension may start it) and registers it for
/// Chrome, Edge and Brave under HKEY_CURRENT_USER. "Disconnect" removes both. Nothing is installed in the browsers:
/// the user adds the extension there.
/// </summary>
public sealed class BrowserHostRegistration
{
    public static readonly (string Browser, string KeyPath)[] Keys =
    [
        ("chrome", @"Software\Google\Chrome\NativeMessagingHosts\" + BrowserTabs.HostName),
        ("edge", @"Software\Microsoft\Edge\NativeMessagingHosts\" + BrowserTabs.HostName),
        ("brave", @"Software\BraveSoftware\Brave-Browser\NativeMessagingHosts\" + BrowserTabs.HostName),
    ];

    private readonly IHostRegistry _registry;
    private readonly string _folder;

    /// <param name="folder">Where the host manifest is written (the VIZEN data folder).</param>
    public BrowserHostRegistration(IHostRegistry registry, string folder)
    {
        _registry = registry;
        _folder = folder;
    }

    public string ManifestPath => Path.Combine(_folder, "browser", BrowserTabs.HostName + ".json");

    /// <summary>Registered, and the manifest starts this VIZEN (not an older copy in another folder).</summary>
    public bool IsRegistered(string exePath) =>
        Keys.Any(k => string.Equals(_registry.Get(k.KeyPath), ManifestPath, StringComparison.OrdinalIgnoreCase))
        && File.Exists(ManifestPath)
        && string.Equals(ReadManifestPath(), exePath, StringComparison.OrdinalIgnoreCase);

    public void Register(string exePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ManifestPath)!);
        var manifest = new Dictionary<string, object>
        {
            ["name"] = BrowserTabs.HostName,
            ["description"] = "VIZEN Connector: shows browser tabs in the VIZEN app on this PC.",
            ["path"] = exePath,
            ["type"] = "stdio",
            ["allowed_origins"] = new[] { $"chrome-extension://{BrowserTabs.ExtensionId}/" },
        };
        File.WriteAllText(ManifestPath, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
        foreach (var (_, keyPath) in Keys)
        {
            _registry.Set(keyPath, ManifestPath);
        }
    }

    public void Unregister()
    {
        foreach (var (_, keyPath) in Keys)
        {
            if (string.Equals(_registry.Get(keyPath), ManifestPath, StringComparison.OrdinalIgnoreCase))
            {
                _registry.Delete(keyPath);
            }
        }

        if (File.Exists(ManifestPath))
        {
            File.Delete(ManifestPath);
        }
    }

    private string? ReadManifestPath()
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(ManifestPath));
            return doc.RootElement.TryGetProperty("path", out var path) ? path.GetString() : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
