using System.Diagnostics;
using System.Text.Json;
using System.Windows.Threading;
using VIZEN.App.Models;
using VIZEN.App.Services;

namespace VIZEN.Tests;

public class BrowserTabsTests
{
    private static BrowserMessage TabsMessage(string? browser, params BrowserTabDto[] tabs) =>
        new() { Type = "tabs", Browser = browser, Tabs = [.. tabs] };

    private static BrowserTabDto Dto(int id, string title, string site, bool audible = false, bool muted = false) =>
        new() { Id = id, WindowId = 1, Title = title, Site = site, Audible = audible, Muted = muted };

    [Fact]
    public async Task FrameRoundTrip()
    {
        var message = TabsMessage("edge", Dto(7, "Lofi radio - YouTube", "youtube.com", audible: true));
        using var stream = new MemoryStream();
        await BrowserTabs.WriteFrameAsync(stream, BrowserTabs.Serialize(message), CancellationToken.None);
        stream.Position = 0;

        var frame = await BrowserTabs.ReadFrameAsync(stream, CancellationToken.None);
        var back = BrowserTabs.Deserialize(frame!)!;

        Assert.Equal("tabs", back.Type);
        Assert.Equal("edge", back.Browser);
        Assert.Equal(7, back.Tabs![0].Id);
        Assert.True(back.Tabs[0].Audible);
        Assert.Null(await BrowserTabs.ReadFrameAsync(stream, CancellationToken.None)); // end of stream
    }

    [Fact]
    public async Task FrameRejectsHugeLength()
    {
        using var stream = new MemoryStream([0xFF, 0xFF, 0xFF, 0x7F, 1, 2, 3]);
        await Assert.ThrowsAsync<InvalidDataException>(() => BrowserTabs.ReadFrameAsync(stream, CancellationToken.None));
    }

    [Fact]
    public void BadJsonIsIgnored() => Assert.Null(BrowserTabs.Deserialize("{not json"u8));

    [Fact]
    public void UnknownBrowserBecomesChromeAndTextIsTrimmed()
    {
        var tabs = BrowserTabs.TabsOf(TabsMessage("netscape", Dto(1, "  Docs  ", " docs.google.com ")));
        Assert.Equal("chrome", tabs[0].Browser);
        Assert.Equal("Docs", tabs[0].Title);
        Assert.Equal("docs.google.com", tabs[0].Site);
        Assert.Equal("chrome.exe", tabs[0].ProcessKey);
    }

    [Theory]
    [InlineData("edge", "msedge.exe")]
    [InlineData("brave", "brave.exe")]
    [InlineData("chrome", "chrome.exe")]
    public void ProcessKeyMatchesTheMixer(string browser, string key) =>
        Assert.Equal(key, BrowserTabs.TabsOf(TabsMessage(browser, Dto(1, "x", "x.com")))[0].ProcessKey);

    [Theory]
    [InlineData("Meet - abc-defg-hij", "meet.google.com", SourceCategory.Calls)]
    [InlineData("Lofi radio - YouTube", "youtube.com", SourceCategory.Media)]
    [InlineData("Docs", "docs.google.com", SourceCategory.Browsers)]
    public void EveryTabHasItsOwnCategory(string title, string site, SourceCategory expected) =>
        Assert.Equal(expected, BrowserTabs.TabsOf(TabsMessage("chrome", Dto(1, title, site)))[0].Category);

    [Fact]
    public void StoreKeepsEachBrowserApart()
    {
        var store = new BrowserTabStore();
        Assert.True(store.Apply(TabsMessage("chrome", Dto(1, "A", "a.com"), Dto(2, "B", "b.com"))));
        Assert.True(store.Apply(TabsMessage("edge", Dto(1, "C", "c.com"))));
        Assert.Equal(3, store.All.Count);

        Assert.False(store.Apply(TabsMessage("chrome", Dto(1, "A", "a.com"), Dto(2, "B", "b.com")))); // same list
        Assert.True(store.Apply(TabsMessage("chrome", Dto(2, "B", "b.com", audible: true))));
        Assert.Equal(2, store.All.Count);

        Assert.True(store.Remove("edge"));
        Assert.Single(store.All);
        Assert.False(store.Apply(new BrowserMessage { Type = "hello", Browser = "chrome" }));
    }

    [Fact]
    public void StoreAcceptsAnEmptyList()
    {
        var store = new BrowserTabStore();
        store.Apply(TabsMessage("chrome", Dto(1, "A", "a.com")));
        Assert.True(store.Apply(TabsMessage("chrome")));
        Assert.Empty(store.All);
    }

    [Theory]
    [InlineData("chrome-extension://dlokfgkopfanhmfkaocoaedeppojcheh/", true)]
    [InlineData("--minimized", false)]
    public void HostLaunchIsRecognized(string arg, bool expected) =>
        Assert.Equal(expected, BrowserNativeHost.IsHostLaunch([arg]));

    private sealed class FakeRegistry : IHostRegistry
    {
        public Dictionary<string, string> Values { get; } = [];
        public string? Get(string keyPath) => Values.GetValueOrDefault(keyPath);
        public void Set(string keyPath, string value) => Values[keyPath] = value;
        public void Delete(string keyPath) => Values.Remove(keyPath);
    }

    [Fact]
    public void RegisterWritesManifestForEveryBrowserAndUnregisterRemovesIt()
    {
        var folder = Path.Combine(Path.GetTempPath(), "vizen-host-" + Guid.NewGuid().ToString("N"));
        try
        {
            var registry = new FakeRegistry();
            var registration = new BrowserHostRegistration(registry, folder);
            const string exe = @"C:\Program Files\VIZEN\VIZEN.App.exe";
            Assert.False(registration.IsRegistered(exe));

            registration.Register(exe);

            Assert.True(registration.IsRegistered(exe));
            Assert.False(registration.IsRegistered(@"D:\old\VIZEN.App.exe")); // another copy is not "connected"
            Assert.Equal(3, registry.Values.Count);
            Assert.All(registry.Values.Values, v => Assert.Equal(registration.ManifestPath, v));

            using (var doc = JsonDocument.Parse(File.ReadAllText(registration.ManifestPath)))
            {
                var root = doc.RootElement;
                Assert.Equal(BrowserTabs.HostName, root.GetProperty("name").GetString());
                Assert.Equal(exe, root.GetProperty("path").GetString());
                Assert.Equal("stdio", root.GetProperty("type").GetString());
                var origins = root.GetProperty("allowed_origins").EnumerateArray().Select(o => o.GetString()).ToList();
                Assert.Equal([$"chrome-extension://{BrowserTabs.ExtensionId}/"], origins); // only the VIZEN extension
            }

            registration.Unregister();
            Assert.Empty(registry.Values);
            Assert.False(File.Exists(registration.ManifestPath));
            Assert.False(registration.IsRegistered(exe));
        }
        finally
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, true);
            }
        }
    }

    [Fact]
    public void UnregisterLeavesOtherHostsAlone()
    {
        var registry = new FakeRegistry();
        var key = BrowserHostRegistration.Keys[0].KeyPath;
        registry.Set(key, @"C:\someone-else\host.json");
        new BrowserHostRegistration(registry, Path.GetTempPath()).Unregister();
        Assert.Equal(@"C:\someone-else\host.json", registry.Get(key));
    }

    [Fact]
    public void ExtensionIdMatchesTheManifestKey()
    {
        var manifest = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\browser-extension\manifest.json"));
        using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
        var key = Convert.FromBase64String(doc.RootElement.GetProperty("key").GetString()!);
        var hash = System.Security.Cryptography.SHA256.HashData(key);
        var id = new string(hash.Take(16).SelectMany(b => new[] { (char)('a' + (b >> 4)), (char)('a' + (b & 0xF)) }).ToArray());
        Assert.Equal(BrowserTabs.ExtensionId, id);

        var permissions = doc.RootElement.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()).Order().ToList();
        Assert.Equal(["nativeMessaging", "tabCapture", "tabs"], permissions); // nothing more
        Assert.False(doc.RootElement.TryGetProperty("host_permissions", out _));
        Assert.False(doc.RootElement.TryGetProperty("content_scripts", out _));
    }

    /// <summary>The extension icons are the app icon: right size, transparent corners, opaque centre (not noise).</summary>
    [Theory]
    [InlineData(16)]
    [InlineData(32)]
    [InlineData(48)]
    [InlineData(128)]
    public void ExtensionIconsAreClean(int size)
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, $@"..\..\..\..\browser-extension\icons\icon{size}.png"));
        using var stream = File.OpenRead(path);
        var frame = new System.Windows.Media.Imaging.PngBitmapDecoder(stream, System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat,
            System.Windows.Media.Imaging.BitmapCacheOption.OnLoad).Frames[0];
        var bgra = new System.Windows.Media.Imaging.FormatConvertedBitmap(frame, System.Windows.Media.PixelFormats.Bgra32, null, 0);
        Assert.Equal(size, bgra.PixelWidth);
        Assert.Equal(size, bgra.PixelHeight);
        var pixels = new byte[size * size * 4];
        bgra.CopyPixels(pixels, size * 4, 0);
        byte Alpha(int x, int y) => pixels[(y * size + x) * 4 + 3];

        Assert.All(new[] { Alpha(0, 0), Alpha(size - 1, 0), Alpha(0, size - 1), Alpha(size - 1, size - 1) }, a => Assert.True(a < 40, $"corner alpha {a}"));
        Assert.Equal(255, Alpha(size / 2, size / 4)); // inside the circle
    }

    /// <summary>
    /// The real path: VIZEN.App.exe started like a browser starts it (origin argument, framed JSON on stdin) relays the
    /// tabs to the bridge over the named pipe, and the bridge's commands come back on its stdout.
    /// </summary>
    [Fact]
    public void HostRelaysTabsToVizenAndCommandsBack()
    {
        var exe = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\VIZEN.App\bin\Debug\net10.0-windows10.0.22621.0\VIZEN.App.exe"));
        Assert.True(File.Exists(exe), exe);

        BrowserBridgeService? bridge = null;
        Dispatcher? dispatcher = null;
        var ready = new ManualResetEventSlim();
        var gotTabs = new ManualResetEventSlim();
        var ui = new Thread(() =>
        {
            dispatcher = Dispatcher.CurrentDispatcher;
            bridge = new BrowserBridgeService();
            bridge.Changed += (_, _) =>
            {
                if (bridge.Store.All.Any(t => t.Browser == "edge" && t.Id == 42))
                {
                    gotTabs.Set();
                }
            };
            bridge.Start();
            ready.Set();
            Dispatcher.Run();
        });
        ui.SetApartmentState(ApartmentState.STA);
        ui.IsBackground = true;
        ui.Start();
        ready.Wait();

        using var host = Process.Start(new ProcessStartInfo(exe, $"chrome-extension://{BrowserTabs.ExtensionId}/")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        try
        {
            var stdin = host.StandardInput.BaseStream;
            BrowserTabs.WriteFrameAsync(stdin, BrowserTabs.Serialize(new BrowserMessage { Type = "hello", Browser = "edge", Version = "1.0.0" }), CancellationToken.None).Wait();
            BrowserTabs.WriteFrameAsync(stdin, BrowserTabs.Serialize(TabsMessage("edge", Dto(42, "Lofi radio - YouTube", "youtube.com", audible: true))), CancellationToken.None).Wait();

            Assert.True(gotTabs.Wait(TimeSpan.FromSeconds(15)), "the bridge did not receive the tabs");
            var tab = dispatcher!.Invoke(() => bridge!.Store.All.Single(t => t.Id == 42));
            Assert.Equal("youtube.com", tab.Site);
            Assert.Equal(SourceCategory.Media, tab.Category);
            Assert.Contains("edge", bridge!.ConnectedBrowsers);

            bridge.SetMutedAsync(tab, true).Wait();
            var read = BrowserTabs.ReadFrameAsync(host.StandardOutput.BaseStream, CancellationToken.None);
            Assert.True(read.Wait(TimeSpan.FromSeconds(10)), "no command on the host's stdout");
            var command = BrowserTabs.Deserialize(read.Result!)!;
            Assert.Equal("mute", command.Type);
            Assert.Equal(42, command.TabId);
            Assert.True(command.Muted);

            // The browser closes the connection: the host exits on its own.
            stdin.Close();
            Assert.True(host.WaitForExit(10000), "the host did not exit");
            Assert.Equal(0, host.ExitCode);
        }
        finally
        {
            if (!host.HasExited)
            {
                host.Kill();
            }

            dispatcher?.Invoke(() => bridge?.Dispose());
            dispatcher?.InvokeShutdown();
        }
    }
}
