using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32;
using VIZEN.App.Models;

namespace VIZEN.App.Services;

/// <summary>What one update source answered.</summary>
public sealed record UpdateSourceResult(string Source, UpdateManifest? Manifest, string? Error, string? UnverifiedVersion = null, string? ReleasePage = null)
{
    public bool IsVerified => Manifest is not null;
}

public enum UpdateCheckStatus
{
    UpToDate,
    Available,

    /// <summary>A newer release exists but has no signed manifest: VIZEN only offers the download page.</summary>
    AvailableUnverified,

    /// <summary>No source could be read or trusted.</summary>
    Failed,
}

public sealed record UpdateCheckResult(UpdateCheckStatus Status, UpdateManifest? Manifest, string? Source, string? Version,
    string? ReleasePage, IReadOnlyList<UpdateSourceResult> Sources, string? Warning)
{
    public string? Error => Status == UpdateCheckStatus.Failed ? string.Join(" · ", Sources.Select(s => $"{s.Source}: {s.Error}")) : null;
}

public sealed class UpdateException(string message) : Exception(message);

/// <summary>
/// Reads the official release information: the signed manifest on vizen.ro first, the same signed manifest attached to
/// the latest GitHub release as the fallback. Only a manifest whose signature matches the built-in VIZEN key is
/// trusted; an unsigned GitHub release is shown, never installed. Never offers an older version than the running one.
/// </summary>
public sealed class UpdateChecker
{
    public static readonly Uri WebsiteManifest = new("https://vizen.ro/updates/latest.json");
    public static readonly Uri GitHubLatestRelease = new("https://api.github.com/repos/kalinvisuals/VIZEN/releases/latest");
    public const string ManifestAssetName = "latest.json";

    private readonly HttpClient _http;
    private readonly IReadOnlyList<string>? _trustedKeys;
    private readonly Uri _websiteManifest;
    private readonly Uri _gitHubRelease;

    public UpdateChecker(HttpClient http, IReadOnlyList<string>? trustedKeys = null, Uri? websiteManifest = null, Uri? gitHubRelease = null)
    {
        _http = http;
        _trustedKeys = trustedKeys;
        // VIZEN_UPDATE_FEED / VIZEN_UPDATE_GITHUB point the checks at a test server; what it serves must still carry a
        // valid VIZEN signature, so this cannot make VIZEN trust anything else.
        _websiteManifest = websiteManifest ?? EnvironmentUri("VIZEN_UPDATE_FEED") ?? WebsiteManifest;
        _gitHubRelease = gitHubRelease ?? EnvironmentUri("VIZEN_UPDATE_GITHUB") ?? GitHubLatestRelease;
    }

    private static Uri? EnvironmentUri(string name) =>
        Uri.TryCreate(Environment.GetEnvironmentVariable(name), UriKind.Absolute, out var uri) ? uri : null;

    public static HttpClient CreateHttpClient(HttpMessageHandler? handler = null)
    {
        var http = handler is null ? new HttpClient() : new HttpClient(handler);
        http.Timeout = TimeSpan.FromSeconds(30);
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("VIZEN", AssemblyVersion()));
        return http;
    }

    private static string AssemblyVersion() => typeof(UpdateChecker).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    public async Task<UpdateCheckResult> CheckAsync(AppVersion current, CancellationToken cancel)
    {
        var website = await FromWebsiteAsync(cancel).ConfigureAwait(false);
        var gitHub = await FromGitHubAsync(cancel).ConfigureAwait(false);
        return Decide(current, [website, gitHub]);
    }

    /// <summary>Chooses from what the sources answered (public for tests).</summary>
    public static UpdateCheckResult Decide(AppVersion current, IReadOnlyList<UpdateSourceResult> sources)
    {
        var verified = sources.Where(s => s.IsVerified).ToList();
        if (verified.Count == 0)
        {
            var unverified = sources
                .Where(s => AppVersion.TryParse(s.UnverifiedVersion, out var v) && v > current)
                .Select(s => (Source: s, Version: Parse(s.UnverifiedVersion)))
                .OrderByDescending(x => x.Version)
                .FirstOrDefault();
            if (unverified.Source is not null)
            {
                return new UpdateCheckResult(UpdateCheckStatus.AvailableUnverified, null, unverified.Source.Source, unverified.Version.ToString(),
                    unverified.Source.ReleasePage, sources, null);
            }

            return sources.Any(s => s.Error is null)
                ? new UpdateCheckResult(UpdateCheckStatus.UpToDate, null, null, null, null, sources, null)
                : new UpdateCheckResult(UpdateCheckStatus.Failed, null, null, null, null, sources, null);
        }

        var best = verified.OrderByDescending(s => Parse(s.Manifest!.Version)).ThenBy(s => sources.ToList().IndexOf(s)).First();
        var bestVersion = Parse(best.Manifest!.Version);
        string? warning = null;
        foreach (var other in verified.Where(s => s != best))
        {
            var otherVersion = Parse(other.Manifest!.Version);
            if (otherVersion.CompareTo(bestVersion) == 0
                && !string.Equals(other.Manifest.Installer!.Sha256, best.Manifest.Installer!.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                // Same version, different file: one source is wrong or was changed. Install nothing.
                return new UpdateCheckResult(UpdateCheckStatus.Failed, null, null, bestVersion.ToString(), null,
                    sources.Select(s => s.IsVerified ? s with { Error = "the sources describe different files for the same version" } : s).ToList(), null);
            }

            if (otherVersion.CompareTo(bestVersion) != 0)
            {
                warning = $"{other.Source} {otherVersion} · {best.Source} {bestVersion}";
            }
        }

        var page = best.Manifest.NotesUrl ?? sources.FirstOrDefault(s => s.ReleasePage is not null)?.ReleasePage;
        var newerUnsigned = sources.Where(s => AppVersion.TryParse(s.UnverifiedVersion, out var v) && v > current && v > bestVersion)
            .OrderByDescending(s => Parse(s.UnverifiedVersion)).FirstOrDefault();
        if (bestVersion <= current && newerUnsigned is not null)
        {
            return new UpdateCheckResult(UpdateCheckStatus.AvailableUnverified, null, newerUnsigned.Source, Parse(newerUnsigned.UnverifiedVersion).ToString(),
                newerUnsigned.ReleasePage, sources, null);
        }

        return bestVersion > current
            ? new UpdateCheckResult(UpdateCheckStatus.Available, best.Manifest, best.Source, bestVersion.ToString(), page, sources, warning)
            : new UpdateCheckResult(UpdateCheckStatus.UpToDate, best.Manifest, best.Source, bestVersion.ToString(), page, sources, warning);
    }

    private static AppVersion Parse(string? version) => AppVersion.TryParse(version, out var v) ? v : default;

    private async Task<UpdateSourceResult> FromWebsiteAsync(CancellationToken cancel)
    {
        const string source = "vizen.ro";
        try
        {
            var manifest = await GetBytesAsync(_websiteManifest, cancel).ConfigureAwait(false);
            var signature = await GetStringAsync(new Uri(_websiteManifest + ".sig"), cancel).ConfigureAwait(false);
            return Verify(source, manifest, signature, null);
        }
        catch (Exception ex) when (IsNetworkError(ex, cancel))
        {
            return new UpdateSourceResult(source, null, ex.Message);
        }
    }

    private async Task<UpdateSourceResult> FromGitHubAsync(CancellationToken cancel)
    {
        const string source = "GitHub";
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, _gitHubRelease);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            using var response = await _http.SendAsync(request, cancel).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(cancel).ConfigureAwait(false));
            var root = doc.RootElement;
            if (root.TryGetProperty("draft", out var draft) && draft.GetBoolean() || root.TryGetProperty("prerelease", out var pre) && pre.GetBoolean())
            {
                return new UpdateSourceResult(source, null, "the latest release is not a final release");
            }

            var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
            var page = root.TryGetProperty("html_url", out var h) ? h.GetString() : null;
            string? manifestUrl = null, signatureUrl = null;
            if (root.TryGetProperty("assets", out var assets))
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    var name = asset.GetProperty("name").GetString();
                    var url = asset.GetProperty("browser_download_url").GetString();
                    if (name == ManifestAssetName) manifestUrl = url;
                    else if (name == ManifestAssetName + ".sig") signatureUrl = url;
                }
            }

            if (manifestUrl is null || signatureUrl is null)
            {
                // An older release without a signed manifest: show it, never install it.
                return new UpdateSourceResult(source, null, null, tag, page);
            }

            var manifest = await GetBytesAsync(new Uri(manifestUrl), cancel).ConfigureAwait(false);
            var signature = await GetStringAsync(new Uri(signatureUrl), cancel).ConfigureAwait(false);
            return Verify(source, manifest, signature, page);
        }
        catch (Exception ex) when (IsNetworkError(ex, cancel) || ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return new UpdateSourceResult(source, null, ex.Message);
        }
    }

    private UpdateSourceResult Verify(string source, byte[] manifestBytes, string signature, string? page)
    {
        if (!UpdateSignature.Verify(manifestBytes, signature, _trustedKeys))
        {
            return new UpdateSourceResult(source, null, "the release information is not signed by VIZEN", null, page);
        }

        var manifest = UpdateManifest.Parse(manifestBytes);
        var problem = manifest is null ? "unreadable release information" : manifest.Problem();
        return problem is null
            ? new UpdateSourceResult(source, manifest, null, null, page)
            : new UpdateSourceResult(source, null, problem, null, page);
    }

    private async Task<byte[]> GetBytesAsync(Uri url, CancellationToken cancel)
    {
        using var response = await _http.GetAsync(url, cancel).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > 256 * 1024)
        {
            throw new HttpRequestException("release information too large");
        }

        return await response.Content.ReadAsByteArrayAsync(cancel).ConfigureAwait(false);
    }

    private async Task<string> GetStringAsync(Uri url, CancellationToken cancel) =>
        System.Text.Encoding.ASCII.GetString(await GetBytesAsync(url, cancel).ConfigureAwait(false));

    internal static bool IsNetworkError(Exception ex, CancellationToken cancel) =>
        ex is HttpRequestException or IOException || ex is TaskCanceledException && !cancel.IsCancellationRequested;
}

/// <summary>
/// Downloads the installer described by a verified manifest into the data folder: resumes an interrupted download,
/// tries the mirrors, and keeps the file only when its size and SHA-256 match the manifest and its version
/// information says VIZEN with the expected version. A digitally signed file must also pass Windows' signature check.
/// </summary>
public sealed class UpdateDownloader
{
    private readonly HttpClient _http;
    private readonly string _folder;

    public UpdateDownloader(HttpClient http, string folder)
    {
        _http = http;
        _folder = folder;
    }

    public string PathFor(UpdateManifest manifest) => Path.Combine(_folder, $"VIZEN-Setup-{manifest.Version}.exe");

    /// <summary>Returns the verified installer's path. Throws <see cref="UpdateException"/> when no copy can be verified.</summary>
    public async Task<string> DownloadAsync(UpdateManifest manifest, IProgress<double>? progress, CancellationToken cancel, bool checkVersionInfo = true)
    {
        Directory.CreateDirectory(_folder);
        var target = PathFor(manifest);
        foreach (var old in Directory.EnumerateFiles(_folder, "VIZEN-Setup-*").Where(f => !f.StartsWith(target, StringComparison.OrdinalIgnoreCase)))
        {
            TryDelete(old); // installers of other versions
        }

        if (File.Exists(target) && Verify(target, manifest, checkVersionInfo) is null)
        {
            progress?.Report(1);
            return target;
        }

        TryDelete(target);
        var partial = target + ".part";
        var problems = new List<string>();
        foreach (var address in manifest.Installer!.Addresses())
        {
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    await DownloadToAsync(address, partial, manifest.Installer.Size, progress, cancel).ConfigureAwait(false);
                    var problem = Verify(partial, manifest, checkVersionInfo);
                    if (problem is null)
                    {
                        File.Move(partial, target, overwrite: true);
                        return target;
                    }

                    problems.Add($"{address.Host}: {problem}");
                    TryDelete(partial); // a wrong file is never resumed
                    break;
                }
                catch (Exception ex) when (UpdateChecker.IsNetworkError(ex, cancel))
                {
                    problems.Add($"{address.Host}: {ex.Message}");
                    if (attempt < 3)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(attempt * 2), cancel).ConfigureAwait(false); // the part file is kept and resumed
                    }
                }
            }
        }

        throw new UpdateException(string.Join(" · ", problems.Distinct()));
    }

    private async Task DownloadToAsync(Uri address, string partial, long expected, IProgress<double>? progress, CancellationToken cancel)
    {
        var have = File.Exists(partial) ? new FileInfo(partial).Length : 0;
        if (have > expected)
        {
            TryDelete(partial);
            have = 0;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, address);
        if (have > 0)
        {
            request.Headers.Range = new RangeHeaderValue(have, null);
        }

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel).ConfigureAwait(false);
        if (have > 0 && response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && have == expected)
        {
            return; // already complete
        }

        response.EnsureSuccessStatusCode();
        var append = have > 0 && response.StatusCode == HttpStatusCode.PartialContent;
        if (!append)
        {
            have = 0; // the server sent the whole file
        }

        await using var input = await response.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false);
        await using var output = new FileStream(partial, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        var buffer = new byte[81920];
        int read;
        while ((read = await input.ReadAsync(buffer, cancel).ConfigureAwait(false)) > 0)
        {
            have += read;
            if (have > expected)
            {
                throw new IOException("the download is larger than announced");
            }

            await output.WriteAsync(buffer.AsMemory(0, read), cancel).ConfigureAwait(false);
            progress?.Report((double)have / expected);
        }
    }

    /// <summary>Null when the file is the announced installer, otherwise why not.</summary>
    public static string? Verify(string path, UpdateManifest manifest, bool checkVersionInfo)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length != manifest.Installer!.Size)
        {
            return "size does not match";
        }

        using (var stream = File.OpenRead(path))
        {
            var hash = Convert.ToHexString(SHA256.HashData(stream));
            if (!string.Equals(hash, manifest.Installer.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                return "SHA-256 does not match (damaged or changed file)";
            }
        }

        if (checkVersionInfo)
        {
            var version = FileVersionInfo.GetVersionInfo(path);
            // Inno Setup pads its version strings with spaces.
            if (version.ProductName?.Trim() != UpdateManifest.ProductName)
            {
                return "not a VIZEN installer";
            }

            if (!AppVersion.TryParse(version.ProductVersion?.Trim(), out var fileVersion)
                || !AppVersion.TryParse(manifest.Version, out var announced) || fileVersion.CompareTo(announced) != 0)
            {
                return "installer version does not match";
            }

            if (Authenticode.Check(path) == Authenticode.Result.Invalid)
            {
                return "invalid digital signature";
            }
        }

        return null;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // in use or protected; it is replaced later
        }
    }
}

/// <summary>Windows' own Authenticode check (WinVerifyTrust), with no network revocation lookup.</summary>
public static class Authenticode
{
    public enum Result { Valid, NotSigned, Invalid }

    private const uint TrustENoSignature = 0x800B0100;
    private const uint TrustESubjectFormUnknown = 0x800B0003;
    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    public static Result Check(string path)
    {
        var file = new WinTrustFileInfo { cbStruct = (uint)Marshal.SizeOf<WinTrustFileInfo>(), pcwszFilePath = path };
        var filePtr = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
        try
        {
            Marshal.StructureToPtr(file, filePtr, false);
            var data = new WinTrustData
            {
                cbStruct = (uint)Marshal.SizeOf<WinTrustData>(),
                dwUIChoice = 2, // no UI
                fdwRevocationChecks = 0,
                dwUnionChoice = 1, // file
                pFile = filePtr,
                dwStateAction = 0,
                dwProvFlags = 0x00000080, // cache only, no network lookups
            };
            var action = GenericVerifyV2;
            var result = (uint)WinVerifyTrust(IntPtr.Zero, ref action, ref data);
            return result switch
            {
                0 => Result.Valid,
                TrustENoSignature or TrustESubjectFormUnknown => Result.NotSigned,
                _ => Result.Invalid,
            };
        }
        finally
        {
            Marshal.FreeHGlobal(filePtr);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo
    {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid action, ref WinTrustData data);
}

/// <summary>Which update path applies to this copy of VIZEN, and the Windows-managed ones (Store, App Installer).</summary>
public static class UpdateChannels
{
    /// <summary>VIZEN in Microsoft Store (Partner Center, Store ID).</summary>
    public const string StoreProductId = "9P7K298JRBK2";

    /// <summary>Inno Setup's uninstall key for VIZEN (AppId in installer\VIZEN.iss).</summary>
    private const string InstallerKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{8F3B2C71-6E4A-4D7B-9B1E-5A0C2E7D4F19}_is1";

    public static UpdateChannel Detect()
    {
        // Documentation and update-flow tests (VIZEN_DEMO=1 only): behave like the given channel.
        if (DemoMode.IsOn && Enum.TryParse<UpdateChannel>(Environment.GetEnvironmentVariable("VIZEN_DEMO_CHANNEL"), true, out var demo))
        {
            return demo;
        }

        if (PackagedApp.IsPackaged)
        {
            try
            {
                var package = Windows.ApplicationModel.Package.Current;
                if (package.SignatureKind == Windows.ApplicationModel.PackageSignatureKind.Store)
                {
                    return UpdateChannel.Store;
                }

                return package.GetAppInstallerInfo() is not null ? UpdateChannel.AppInstaller : UpdateChannel.Sideloaded;
            }
            catch (Exception ex) when (ex is InvalidOperationException or COMException or TypeLoadException)
            {
                return UpdateChannel.Sideloaded;
            }
        }

        return IsInstallerCopy(AppContext.BaseDirectory) ? UpdateChannel.Installer : UpdateChannel.Unmanaged;
    }

    /// <summary>True when this program runs from the folder the VIZEN installer installed.</summary>
    public static bool IsInstallerCopy(string baseDirectory)
    {
        try
        {
            using var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = hive.OpenSubKey(InstallerKey);
            return key?.GetValue("InstallLocation") is string location
                && Path.GetFullPath(location).TrimEnd('\\').Equals(Path.GetFullPath(baseDirectory).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Microsoft Store: whether the Store has an update for VIZEN (null when it cannot tell).</summary>
    public static async Task<bool?> StoreUpdateAvailableAsync()
    {
        try
        {
            var context = Windows.Services.Store.StoreContext.GetDefault();
            var updates = await context.GetAppAndOptionalStorePackageUpdatesAsync();
            return updates.Count > 0;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>App Installer feed: whether Windows sees a newer package (null when it cannot tell).</summary>
    public static async Task<bool?> AppInstallerUpdateAvailableAsync()
    {
        try
        {
            var result = await Windows.ApplicationModel.Package.Current.CheckUpdateAvailabilityAsync();
            return result.Availability is Windows.ApplicationModel.PackageUpdateAvailability.Available
                or Windows.ApplicationModel.PackageUpdateAvailability.Required;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Opens Windows' own update UI for a Store / App Installer copy.</summary>
    public static void OpenManagedUpdate(UpdateChannel channel)
    {
        var target = channel switch
        {
            UpdateChannel.Store => "ms-windows-store://pdp/?productid=" + StoreProductId,
            UpdateChannel.AppInstaller when SafeAppInstallerUri() is { } uri => "ms-appinstaller:?source=" + Uri.EscapeDataString(uri.ToString()),
            _ => null,
        };
        if (target is not null)
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
    }

    private static Uri? SafeAppInstallerUri()
    {
        try
        {
            return Windows.ApplicationModel.Package.Current.GetAppInstallerInfo()?.Uri;
        }
        catch (Exception ex) when (ex is InvalidOperationException or COMException)
        {
            return null;
        }
    }

    /// <summary>
    /// Runs a verified installer: it closes VIZEN, installs over the current version (settings and data in
    /// %LOCALAPPDATA%\VIZEN are not touched) and starts VIZEN again. Windows asks for administrator permission.
    /// Returns false when the user declined that request.
    /// </summary>
    public static bool StartInstaller(string installerPath)
    {
        try
        {
            Process.Start(new ProcessStartInfo(installerPath, "/SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /RELAUNCH=1")
            {
                UseShellExecute = true,
            });
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return false; // the administrator request was declined
        }
    }
}
