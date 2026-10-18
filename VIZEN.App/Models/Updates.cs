using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VIZEN.App.Models;

/// <summary>How this copy of VIZEN was installed; each one is updated differently.</summary>
public enum UpdateChannel
{
    /// <summary>The classic installer (Inno Setup, Program Files): VIZEN downloads, verifies and runs the new installer.</summary>
    Installer,

    /// <summary>Microsoft Store: updates come from the Store only.</summary>
    Store,

    /// <summary>A signed MSIX installed from an .appinstaller feed: Windows App Installer updates it.</summary>
    AppInstaller,

    /// <summary>A package installed by hand (developer or sideloaded MSIX): no update path.</summary>
    Sideloaded,

    /// <summary>A build that was not installed (development, copied folder): it can only tell that a new version exists.</summary>
    Unmanaged,
}

/// <summary>
/// The machine-readable release description (latest.json), published on vizen.ro and as a GitHub release asset, always
/// with a detached signature (latest.json.sig) made with the VIZEN update key. The app accepts it only when the
/// signature matches one of its built-in public keys.
/// </summary>
public sealed class UpdateManifest
{
    [JsonPropertyName("schema")] public int Schema { get; set; }
    [JsonPropertyName("product")] public string? Product { get; set; }
    [JsonPropertyName("channel")] public string? Channel { get; set; }
    [JsonPropertyName("version")] public string? Version { get; set; }
    [JsonPropertyName("releaseDate")] public string? ReleaseDate { get; set; }
    [JsonPropertyName("notes")] public Dictionary<string, string>? Notes { get; set; }
    [JsonPropertyName("notesUrl")] public string? NotesUrl { get; set; }
    [JsonPropertyName("installer")] public UpdateInstaller? Installer { get; set; }

    public const int CurrentSchema = 1;
    public const string ProductName = "VIZEN";

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = false };

    public static UpdateManifest? Parse(ReadOnlySpan<byte> json)
    {
        try
        {
            return JsonSerializer.Deserialize<UpdateManifest>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Null when usable, otherwise why not (never trusted for anything else).</summary>
    public string? Problem()
    {
        if (Schema != CurrentSchema) return $"unsupported schema {Schema}";
        if (Product != ProductName) return "not a VIZEN manifest";
        if (!AppVersion.TryParse(Version, out _)) return "invalid version";
        if (Installer is null) return "no installer";
        if (!Uri.TryCreate(Installer.Url, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps) return "installer address is not HTTPS";
        if (Installer.Mirrors?.Any(m => !Uri.TryCreate(m, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps) == true) return "mirror address is not HTTPS";
        if (Installer.Size <= 0) return "missing size";
        if (Installer.Sha256 is not { Length: 64 } hash || !hash.All(Uri.IsHexDigit)) return "missing SHA-256";
        return null;
    }

    /// <summary>Release notes in the UI language, else English.</summary>
    public string NotesFor(string language) =>
        Notes is null ? string.Empty
        : Notes.TryGetValue(language, out var text) ? text
        : Notes.TryGetValue("en", out var english) ? english
        : Notes.Values.FirstOrDefault() ?? string.Empty;
}

public sealed class UpdateInstaller
{
    [JsonPropertyName("url")] public string? Url { get; set; }
    [JsonPropertyName("mirrors")] public List<string>? Mirrors { get; set; }
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("sha256")] public string? Sha256 { get; set; }

    public IEnumerable<Uri> Addresses() =>
        new[] { Url }.Concat(Mirrors ?? []).Where(u => u is not null).Select(u => new Uri(u!));
}

/// <summary>Major.minor.patch[.build][-label]; a version with a label (pre-release) is older than the same version without.</summary>
public readonly record struct AppVersion(int Major, int Minor, int Patch, int Build, string? Label) : IComparable<AppVersion>
{
    public static bool TryParse(string? text, out AppVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var value = text.Trim().TrimStart('v', 'V');
        var plus = value.IndexOf('+');
        if (plus >= 0)
        {
            value = value[..plus]; // build metadata does not change the order
        }

        string? label = null;
        var dash = value.IndexOf('-');
        if (dash >= 0)
        {
            label = value[(dash + 1)..];
            value = value[..dash];
            if (label.Length == 0)
            {
                return false;
            }
        }

        var parts = value.Split('.');
        if (parts.Length is < 2 or > 4)
        {
            return false;
        }

        var numbers = new int[4];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i]))
            {
                return false;
            }
        }

        version = new AppVersion(numbers[0], numbers[1], numbers[2], numbers[3], label);
        return true;
    }

    public int CompareTo(AppVersion other)
    {
        var c = Major.CompareTo(other.Major);
        if (c == 0) c = Minor.CompareTo(other.Minor);
        if (c == 0) c = Patch.CompareTo(other.Patch);
        if (c == 0) c = Build.CompareTo(other.Build);
        if (c != 0) return c;
        return (Label, other.Label) switch
        {
            (null, null) => 0,
            (null, _) => 1,
            (_, null) => -1,
            _ => string.CompareOrdinal(Label, other.Label),
        };
    }

    public static bool operator >(AppVersion a, AppVersion b) => a.CompareTo(b) > 0;
    public static bool operator <(AppVersion a, AppVersion b) => a.CompareTo(b) < 0;
    public static bool operator >=(AppVersion a, AppVersion b) => a.CompareTo(b) >= 0;
    public static bool operator <=(AppVersion a, AppVersion b) => a.CompareTo(b) <= 0;

    public override string ToString() =>
        $"{Major}.{Minor}.{Patch}{(Build > 0 ? "." + Build : string.Empty)}{(Label is null ? string.Empty : "-" + Label)}";
}

/// <summary>
/// Checks the detached signature of a manifest: ECDSA P-256 with SHA-256 over the exact bytes of latest.json
/// (IEEE P1363 r||s, base64 in latest.json.sig). The private key never leaves the release PC / the GitHub secret.
/// </summary>
public static class UpdateSignature
{
    /// <summary>Public keys of the VIZEN update key (SubjectPublicKeyInfo, base64). More than one allows a key change.</summary>
    public static readonly string[] TrustedKeys =
    [
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEAQGWdvIz8racIt15jo9eOteJj/7TwjejBY/uPjE5pHhgEfRAbcjilmtih5LhIuQuxLdq8zdYO6OPNPx9xqIaKw==", // main key, 2026-10-18
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEyo4rO2UuWuTTlm9oU4SMi+eshXTsOwN+uCYoZF0tPbS+FruHaYtEjT8a1oPcE4bl2DXHJjpIwXxT+3oBJBgw8g==", // backup key (kept offline), 2026-10-18
    ];

    public static bool Verify(byte[] manifest, string? signatureBase64, IEnumerable<string>? trustedKeys = null)
    {
        if (string.IsNullOrWhiteSpace(signatureBase64))
        {
            return false;
        }

        byte[] signature;
        try
        {
            signature = Convert.FromBase64String(signatureBase64.Trim());
        }
        catch (FormatException)
        {
            return false;
        }

        foreach (var key in trustedKeys ?? TrustedKeys)
        {
            try
            {
                using var ecdsa = ECDsa.Create();
                ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(key), out _);
                if (ecdsa.VerifyData(manifest, signature, HashAlgorithmName.SHA256))
                {
                    return true;
                }
            }
            catch (Exception ex) when (ex is FormatException or CryptographicException)
            {
                // not a usable key (e.g. the placeholder in an unconfigured build)
            }
        }

        return false;
    }
}
