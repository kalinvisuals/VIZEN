using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using VIZEN.App.Models;
using VIZEN.App.Services;

namespace VIZEN.Tests;

public class UpdateTests : IDisposable
{
    private static readonly Uri Website = new("https://vizen.test/updates/latest.json");
    private static readonly Uri GitHubApi = new("https://api.github.test/repos/kalinvisuals/VIZEN/releases/latest");
    private const string GitHubManifest = "https://github.test/download/latest.json";
    private static readonly AppVersion Current = Parse("0.4.5");

    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly string[] _trusted;
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "vizen-update-" + Guid.NewGuid().ToString("N"));
    private readonly FakeServer _server = new();

    public UpdateTests() => _trusted = [Convert.ToBase64String(_key.ExportSubjectPublicKeyInfo())];

    public void Dispose()
    {
        _key.Dispose();
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, true);
        }
    }

    private static AppVersion Parse(string v) => AppVersion.TryParse(v, out var version) ? version : throw new ArgumentException(v);

    private static byte[] Installer(int size = 300_000, int seed = 1)
    {
        var data = new byte[size];
        new Random(seed).NextBytes(data);
        return data;
    }

    private byte[] Manifest(string version, byte[] installer, string url = "https://github.test/download/VIZEN-Setup-x64.exe", string? mirror = null) =>
        Encoding.UTF8.GetBytes(
            "{\"schema\":1,\"product\":\"VIZEN\",\"channel\":\"stable\",\"version\":\"" + version + "\",\"releaseDate\":\"2026-10-18\"," +
            "\"notes\":{\"en\":\"Notes " + version + "\",\"ro\":\"Note " + version + "\"},\"notesUrl\":\"https://github.test/releases/tag/v" + version + "\"," +
            "\"installer\":{\"url\":\"" + url + "\",\"mirrors\":[" + (mirror is null ? "" : "\"" + mirror + "\"") + "],\"size\":" + installer.Length + "," +
            "\"sha256\":\"" + Convert.ToHexString(SHA256.HashData(installer)) + "\"}}");

    private string Sign(byte[] manifest) => Convert.ToBase64String(_key.SignData(manifest, HashAlgorithmName.SHA256));

    private void PublishOnWebsite(byte[] manifest, string? signature = null)
    {
        _server.Files[Website.ToString()] = manifest;
        _server.Files[Website + ".sig"] = Encoding.ASCII.GetBytes(signature ?? Sign(manifest));
    }

    private void PublishOnGitHub(string tag, byte[]? manifest, string? signature = null)
    {
        var assets = manifest is null ? "[]" : $$"""
            [{"name":"VIZEN-Setup-x64.exe","browser_download_url":"https://github.test/download/VIZEN-Setup-x64.exe"},
             {"name":"latest.json","browser_download_url":"{{GitHubManifest}}"},
             {"name":"latest.json.sig","browser_download_url":"{{GitHubManifest}}.sig"}]
            """;
        _server.Files[GitHubApi.ToString()] = Encoding.UTF8.GetBytes($$"""
            {"tag_name":"{{tag}}","html_url":"https://github.test/releases/tag/{{tag}}","draft":false,"prerelease":false,"assets":{{assets}}}
            """);
        if (manifest is not null)
        {
            _server.Files[GitHubManifest] = manifest;
            _server.Files[GitHubManifest + ".sig"] = Encoding.ASCII.GetBytes(signature ?? Sign(manifest));
        }
    }

    private Task<UpdateCheckResult> Check(AppVersion? current = null) =>
        new UpdateChecker(UpdateChecker.CreateHttpClient(_server), _trusted, Website, GitHubApi).CheckAsync(current ?? Current, CancellationToken.None);

    // ----- versions -----

    [Theory]
    [InlineData("0.4.5", "0.4.6", -1)]
    [InlineData("0.4.10", "0.4.9", 1)]
    [InlineData("v1.0.0", "1.0", 0)]
    [InlineData("1.0.0-beta.1", "1.0.0", -1)]
    [InlineData("1.0.0-beta.2", "1.0.0-beta.1", 1)]
    [InlineData("0.4.5.1", "0.4.5", 1)]
    [InlineData("1.0.0+build.7", "1.0.0", 0)]
    public void VersionsCompare(string a, string b, int expected) => Assert.Equal(expected, Math.Sign(Parse(a).CompareTo(Parse(b))));

    [Theory]
    [InlineData("")]
    [InlineData("1")]
    [InlineData("1.x.0")]
    [InlineData("1.2.3.4.5")]
    [InlineData("1.0.0-")]
    public void BadVersionsAreRejected(string text) => Assert.False(AppVersion.TryParse(text, out _));

    // ----- checks -----

    [Fact]
    public async Task NoUpdateAvailable()
    {
        var installer = Installer();
        PublishOnWebsite(Manifest("0.4.5", installer));
        PublishOnGitHub("v0.4.5", Manifest("0.4.5", installer));
        var result = await Check();
        Assert.Equal(UpdateCheckStatus.UpToDate, result.Status);
    }

    [Fact]
    public async Task NewVersionOnTheWebsite()
    {
        var installer = Installer();
        PublishOnWebsite(Manifest("0.4.6", installer));
        PublishOnGitHub("v0.4.6", Manifest("0.4.6", installer));
        var result = await Check();
        Assert.Equal(UpdateCheckStatus.Available, result.Status);
        Assert.Equal("vizen.ro", result.Source);
        Assert.Equal("0.4.6", result.Version);
        Assert.Equal("Note 0.4.6", result.Manifest!.NotesFor("ro"));
        Assert.Null(result.Warning);
    }

    [Fact]
    public async Task WebsiteDownFallsBackToGitHub()
    {
        PublishOnGitHub("v0.4.6", Manifest("0.4.6", Installer()));
        _server.Down.Add("vizen.test");
        var result = await Check();
        Assert.Equal(UpdateCheckStatus.Available, result.Status);
        Assert.Equal("GitHub", result.Source);
    }

    [Fact]
    public async Task GitHubDownWebsiteStillWorks()
    {
        PublishOnWebsite(Manifest("0.4.6", Installer()));
        _server.Down.Add("api.github.test");
        var result = await Check();
        Assert.Equal(UpdateCheckStatus.Available, result.Status);
        Assert.Equal("vizen.ro", result.Source);
    }

    [Fact]
    public async Task BothSourcesDown()
    {
        _server.Down.Add("vizen.test");
        _server.Down.Add("api.github.test");
        var result = await Check();
        Assert.Equal(UpdateCheckStatus.Failed, result.Status);
        Assert.Contains("vizen.ro", result.Error);
        Assert.Contains("GitHub", result.Error);
    }

    [Fact]
    public async Task TamperedManifestIsNotTrusted()
    {
        var manifest = Manifest("0.4.6", Installer());
        var signature = Sign(manifest);
        manifest[^5] ^= 1; // changed after signing
        PublishOnWebsite(manifest, signature);
        _server.Down.Add("api.github.test");
        var result = await Check();
        Assert.Equal(UpdateCheckStatus.Failed, result.Status);
        Assert.Contains("not signed by VIZEN", result.Error);
    }

    [Fact]
    public async Task ManifestSignedWithAnotherKeyIsNotTrusted()
    {
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var manifest = Manifest("0.4.6", Installer());
        PublishOnWebsite(manifest, Convert.ToBase64String(other.SignData(manifest, HashAlgorithmName.SHA256)));
        _server.Down.Add("api.github.test");
        Assert.Equal(UpdateCheckStatus.Failed, (await Check()).Status);
    }

    [Fact]
    public async Task VersionMismatchBetweenSourcesTakesTheNewestSigned()
    {
        var installer = Installer();
        PublishOnWebsite(Manifest("0.4.6", installer));
        PublishOnGitHub("v0.4.7", Manifest("0.4.7", Installer(seed: 2)));
        var result = await Check();
        Assert.Equal(UpdateCheckStatus.Available, result.Status);
        Assert.Equal("0.4.7", result.Version);
        Assert.Equal("GitHub", result.Source);
        Assert.NotNull(result.Warning);
    }

    [Fact]
    public async Task SameVersionDifferentFilesInstallsNothing()
    {
        PublishOnWebsite(Manifest("0.4.6", Installer(seed: 1)));
        PublishOnGitHub("v0.4.6", Manifest("0.4.6", Installer(seed: 2)));
        var result = await Check();
        Assert.Equal(UpdateCheckStatus.Failed, result.Status);
        Assert.Null(result.Manifest);
    }

    [Fact]
    public async Task OlderVersionIsNeverOffered()
    {
        PublishOnWebsite(Manifest("0.4.3", Installer()));
        _server.Down.Add("api.github.test");
        Assert.Equal(UpdateCheckStatus.UpToDate, (await Check()).Status);
    }

    [Fact]
    public async Task UpdateFromAnOlderInstalledVersion()
    {
        PublishOnWebsite(Manifest("0.4.5", Installer()));
        _server.Down.Add("api.github.test");
        var result = await Check(Parse("0.4.4"));
        Assert.Equal(UpdateCheckStatus.Available, result.Status);
        Assert.Equal("0.4.5", result.Version);
    }

    [Fact]
    public async Task UnsignedGitHubReleaseIsShownButNeverInstalled()
    {
        _server.Down.Add("vizen.test");
        PublishOnGitHub("v0.4.6", manifest: null);
        var result = await Check();
        Assert.Equal(UpdateCheckStatus.AvailableUnverified, result.Status);
        Assert.Null(result.Manifest);
        Assert.Equal("https://github.test/releases/tag/v0.4.6", result.ReleasePage);
    }

    [Fact]
    public void ManifestMustUseHttps()
    {
        var manifest = UpdateManifest.Parse(Manifest("0.4.6", Installer(), url: "http://github.test/x.exe"))!;
        Assert.Equal("installer address is not HTTPS", manifest.Problem());
    }

    // ----- downloads -----

    private UpdateDownloader Downloader() => new(UpdateChecker.CreateHttpClient(_server), _folder);

    private UpdateManifest Announce(byte[] installer, string? mirror = null)
    {
        _server.Files["https://github.test/download/VIZEN-Setup-x64.exe"] = installer;
        return UpdateManifest.Parse(Manifest("0.4.6", installer, mirror: mirror))!;
    }

    [Fact]
    public async Task DownloadIsVerified()
    {
        var installer = Installer();
        var path = await Downloader().DownloadAsync(Announce(installer), null, CancellationToken.None, checkVersionInfo: false);
        Assert.Equal(installer, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task InterruptedDownloadResumes()
    {
        var installer = Installer();
        var manifest = Announce(installer);
        _server.BreakAfterBytes = 100_000; // the first response stops in the middle
        var path = await Downloader().DownloadAsync(manifest, null, CancellationToken.None, checkVersionInfo: false);
        Assert.Equal(installer, await File.ReadAllBytesAsync(path));
        Assert.Contains(_server.Requests, r => r.Range is not null); // the rest was asked for, not the whole file again
    }

    [Fact]
    public async Task TamperedPackageIsRejected()
    {
        var installer = Installer();
        var manifest = Announce(installer);
        var changed = (byte[])installer.Clone();
        changed[1234] ^= 0xFF;
        _server.Files["https://github.test/download/VIZEN-Setup-x64.exe"] = changed;
        var ex = await Assert.ThrowsAsync<UpdateException>(() => Downloader().DownloadAsync(manifest, null, CancellationToken.None, checkVersionInfo: false));
        Assert.Contains("SHA-256", ex.Message);
        Assert.Empty(Directory.GetFiles(_folder, "*.exe")); // nothing runnable is left behind
    }

    [Fact]
    public async Task MirrorIsUsedWhenTheMainAddressFails()
    {
        var installer = Installer();
        var manifest = Announce(installer, mirror: "https://vizen.test/download/VIZEN-Setup-x64-0.4.6.exe");
        _server.Files.Remove("https://github.test/download/VIZEN-Setup-x64.exe");
        _server.Down.Add("github.test");
        _server.Files["https://vizen.test/download/VIZEN-Setup-x64-0.4.6.exe"] = installer;
        var path = await Downloader().DownloadAsync(manifest, null, CancellationToken.None, checkVersionInfo: false);
        Assert.Equal(installer, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task FileThatIsNotAVizenInstallerIsRejected()
    {
        var installer = Installer();
        var manifest = Announce(installer);
        var ex = await Assert.ThrowsAsync<UpdateException>(() => Downloader().DownloadAsync(manifest, null, CancellationToken.None, checkVersionInfo: true));
        Assert.Contains("not a VIZEN installer", ex.Message);
    }

    [Fact]
    public async Task VerifiedDownloadIsReused()
    {
        var installer = Installer();
        var manifest = Announce(installer);
        await Downloader().DownloadAsync(manifest, null, CancellationToken.None, checkVersionInfo: false);
        var before = _server.Requests.Count;
        await Downloader().DownloadAsync(manifest, null, CancellationToken.None, checkVersionInfo: false);
        Assert.Equal(before, _server.Requests.Count); // no second download
    }

    // ----- channels and data -----

    [Fact]
    public void ThisTestRunIsNotAStoreOrInstallerCopy()
    {
        Assert.False(PackagedApp.IsPackaged);
        Assert.Equal(UpdateChannel.Unmanaged, UpdateChannels.Detect());
        Assert.False(UpdateChannels.IsInstallerCopy(Path.GetTempPath()));
    }

    [Fact]
    public void InstallerNeverTouchesUserData()
    {
        // User data lives in %LOCALAPPDATA%\VIZEN; the installer only writes to {app} (Program Files) and never deletes data.
        var iss = File.ReadAllText(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\installer\VIZEN.iss")));
        Assert.DoesNotContain("[UninstallDelete]", iss);
        Assert.DoesNotContain("[InstallDelete]", iss);
        Assert.Contains("DestDir: \"{app}\"", iss);
        Assert.Contains("RelaunchAfterUpdate", iss);
    }

    [Fact]
    public void BuiltInUpdateKeyIsAValidPublicKey()
    {
        Assert.True(UpdateSignature.TrustedKeys.Length >= 2, "a main and a backup key"); // losing one key must not stop updates
        Assert.Equal(UpdateSignature.TrustedKeys.Length, UpdateSignature.TrustedKeys.Distinct().Count());
        foreach (var key in UpdateSignature.TrustedKeys)
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(key), out _);
            Assert.Equal(256, ecdsa.KeySize);
        }
    }

    /// <summary>Serves files from memory; can be "down" per host and cut the first download short.</summary>
    private sealed class FakeServer : HttpMessageHandler
    {
        public Dictionary<string, byte[]> Files { get; } = [];
        public HashSet<string> Down { get; } = [];
        public List<(string Url, System.Net.Http.Headers.RangeHeaderValue? Range)> Requests { get; } = [];
        public int BreakAfterBytes { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            Requests.Add((url, request.Headers.Range));
            if (Down.Contains(request.RequestUri.Host))
            {
                throw new HttpRequestException($"{request.RequestUri.Host} cannot be reached");
            }

            if (!Files.TryGetValue(url, out var data))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            var from = request.Headers.Range?.Ranges.FirstOrDefault()?.From ?? 0;
            var body = data.AsSpan((int)from).ToArray();
            HttpContent content = BreakAfterBytes > 0 ? new BrokenContent(body, BreakAfterBytes) : new ByteArrayContent(body);
            BreakAfterBytes = 0;
            var response = new HttpResponseMessage(from > 0 ? HttpStatusCode.PartialContent : HttpStatusCode.OK) { Content = content };
            response.Content.Headers.ContentLength = body.Length;
            return Task.FromResult(response);
        }
    }

    /// <summary>Sends the first bytes, then fails like a dropped connection.</summary>
    private sealed class BrokenContent(byte[] data, int cut) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            await stream.WriteAsync(data.AsMemory(0, cut));
            throw new IOException("connection reset");
        }

        protected override bool TryComputeLength(out long length)
        {
            length = data.Length;
            return true;
        }

        // Streamed like a network response: the bytes before the cut arrive, then the read fails.
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new BreakingStream(data, cut));
    }

    private sealed class BreakingStream(byte[] data, int cut) : Stream
    {
        private int _position;

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_position >= cut)
            {
                throw new IOException("connection reset");
            }

            var n = Math.Min(count, cut - _position);
            Array.Copy(data, _position, buffer, offset, n);
            _position += n;
            return n;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => data.Length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
