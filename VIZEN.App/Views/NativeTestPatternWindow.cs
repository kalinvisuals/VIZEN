using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using VIZEN.App.Capture;

namespace VIZEN.App.Views;

/// <summary>
/// Game-like test source: a borderless window whose content is a Direct3D 11 flip-model swap chain presented with vsync
/// (<c>Present(interval)</c>, like a game), so it really produces one new frame per display refresh — unlike the WPF test
/// window, which can fall behind on a busy PC. Every presented frame carries the next number in the
/// <see cref="FrameMarker"/>, so VIZEN's skipped/repeated counts compare directly with the frames the source presented.
/// With <c>VIZEN_PATTERN_LOG=&lt;folder&gt;</c> it writes, once a second, how many frames it presented and how many DXGI
/// reports as displayed (<c>pattern-&lt;id&gt;.log</c>). Started as <c>VIZEN.App.exe --test-pattern-native 60 1 1920x1080</c>.
/// </summary>
public sealed class NativeTestPatternWindow : Window
{
    public const string Argument = "--test-pattern-native";
    private const int BlockPixels = 8;

    private readonly int _fps;
    private readonly int _id;
    private readonly int _pixelWidth;
    private readonly int _pixelHeight;
    private readonly NativeVideoHost _host = new();
    private Thread? _thread;
    private volatile bool _stop;

    private NativeTestPatternWindow(int fps, int id, int pixelWidth, int pixelHeight)
    {
        _fps = Math.Clamp(fps, 1, 240);
        _id = id;
        _pixelWidth = pixelWidth;
        _pixelHeight = pixelHeight;
        Title = $"{WindowCapture.TestPatternTitlePrefix} {id} · {_fps} fps";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        Background = Brushes.Black;
        // Fully on screen: Windows does not render the off-screen part of a window, which would arrive black.
        Left = 0;
        Top = 0;
        Content = _host;
        SourceInitialized += OnSourceInitialized;
        Loaded += (_, _) =>
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            Width = _pixelWidth / dpi.DpiScaleX;
            Height = _pixelHeight / dpi.DpiScaleY;
        };
        _host.PixelSizeChanged += OnPixelSizeChanged;
        Closed += (_, _) =>
        {
            _stop = true;
            _thread?.Join(2000);
        };
    }

    /// <summary>Parses "--test-pattern-native [fps] [id] [WxH]"; null when it is not a native test-pattern start.</summary>
    public static NativeTestPatternWindow? FromArguments(string[] args)
    {
        var at = Array.FindIndex(args, a => string.Equals(a, Argument, StringComparison.OrdinalIgnoreCase));
        if (at < 0)
        {
            return null;
        }

        int Arg(int offset, int fallback) =>
            at + offset < args.Length && int.TryParse(args[at + offset], NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;

        int w = 1920, h = 1080;
        if (at + 3 < args.Length && args[at + 3].Split('x') is [var ws, var hs]
            && int.TryParse(ws, CultureInfo.InvariantCulture, out var pw) && int.TryParse(hs, CultureInfo.InvariantCulture, out var ph))
        {
            (w, h) = (Math.Clamp(pw, 320, 7680), Math.Clamp(ph, 240, 4320));
        }

        return new NativeTestPatternWindow(Arg(1, 60), Arg(2, 1), w, h);
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        // Square corners: Windows 11 would otherwise round off the marker's first block.
        var hwnd = new WindowInteropHelper(this).Handle;
        var preference = 1; // DWMWCP_DONOTROUND
        _ = DwmSetWindowAttribute(hwnd, 33, ref preference, sizeof(int));
    }

    private void OnPixelSizeChanged(IntPtr hwnd, int width, int height)
    {
        // Fixed size: the render thread starts once the child window has exactly the requested pixel size. A swap chain made
        // at an intermediate size would be stretched by Windows, blurring the marker edges so frame numbers misread.
        if (_thread is not null || Math.Abs(width - _pixelWidth) > 1 || Math.Abs(height - _pixelHeight) > 1)
        {
            return;
        }

        var refreshHz = MainDisplay.DisplayHz > 0 ? MainDisplay.DisplayHz : 60;
        var interval = (uint)Math.Clamp(Math.Round(refreshHz / _fps), 1, 4);
        var log = Environment.GetEnvironmentVariable("VIZEN_PATTERN_LOG") is { Length: > 0 } dir ? Path.Combine(dir, $"pattern-{_id}.log") : null;
        _thread = new Thread(() => Render(hwnd, width, height, interval, log)) { IsBackground = true, Name = "Native test pattern" };
        _thread.Start();
    }

    private unsafe void Render(IntPtr hwnd, int width, int height, uint interval, string? log)
    {
        using var device = CaptureDevice.Create();
        var factory = D3D11Interop.GetFactory2(device.Device);
        IntPtr swapChain;
        try
        {
            var desc = new D3D11Interop.SwapChainDesc1
            {
                Width = (uint)width,
                Height = (uint)height,
                Format = D3D11Interop.DXGI_FORMAT_B8G8R8A8_UNORM,
                SampleCount = 1,
                BufferUsage = D3D11Interop.DXGI_USAGE_RENDER_TARGET_OUTPUT,
                BufferCount = 2,
                SwapEffect = D3D11Interop.DXGI_SWAP_EFFECT_FLIP_DISCARD,
            };
            Marshal.ThrowExceptionForHR(D3D11Interop.CreateSwapChainForHwnd(factory, device.Device, hwnd, ref desc, out swapChain));
        }
        finally
        {
            Marshal.Release(factory);
        }

        // CPU-side pixels for the marker strip and the moving bar (both small; the rest is a clear).
        var markerWidth = BlockPixels * FrameMarker.Blocks;
        var marker = new byte[markerWidth * 4 * BlockPixels];
        var barWidth = Math.Max(4, width / 50);
        var bar = new byte[barWidth * 4 * height];
        Array.Fill(bar, (byte)255);

        uint index = 0;
        int presentedThisSecond = 0;
        var second = Stopwatch.GetTimestamp();
        D3D11Interop.GetFrameStatistics(swapChain, out var lastStats);
        try
        {
            while (!_stop)
            {
                // The current back buffer, fetched every frame: CPU writes (UpdateSubresource) must land in the buffer that
                // is presented next, and a reference kept from the first frame did not follow the flip-model rotation.
                Marshal.ThrowExceptionForHR(D3D11Interop.GetBackBuffer(swapChain, out var backBuffer));
                Marshal.ThrowExceptionForHR(D3D11Interop.CreateRenderTargetView(device.Device, backBuffer, out var target));
                D3D11Interop.ClearRenderTarget(device.Context, target, (20 + index % 40) / 255f, 24 / 255f, (40 + index % 60) / 255f);
                FrameMarker.Encode(marker, markerWidth, BlockPixels, markerWidth * 4, index, BlockPixels);
                fixed (byte* m = marker)
                {
                    D3D11Interop.UpdateSubresource(device.Context, backBuffer,
                        new D3D11Interop.Box { Right = (uint)markerWidth, Bottom = BlockPixels, Back = 1 }, m, (uint)markerWidth * 4);
                }

                var x = (uint)((index * 12) % (uint)Math.Max(1, width - barWidth));
                fixed (byte* b = bar)
                {
                    D3D11Interop.UpdateSubresource(device.Context, backBuffer,
                        new D3D11Interop.Box { Left = x, Top = BlockPixels, Right = x + (uint)barWidth, Bottom = (uint)height, Back = 1 }, b, (uint)barWidth * 4);
                }

                Marshal.Release(target);
                Marshal.Release(backBuffer);

                // Waits for the display like a game with vsync; one new frame (one new marker number) per present.
                var hr = D3D11Interop.Present(swapChain, interval);
                if (hr < 0)
                {
                    break;
                }

                index++;
                presentedThisSecond++;
                var now = Stopwatch.GetTimestamp();
                if (now - second >= Stopwatch.Frequency)
                {
                    if (log is not null && D3D11Interop.GetFrameStatistics(swapChain, out var stats) >= 0)
                    {
                        File.AppendAllText(log, string.Create(CultureInfo.InvariantCulture,
                            $"{DateTime.Now:HH:mm:ss} presented={presentedThisSecond} displayed={stats.PresentCount - lastStats.PresentCount} refreshes={stats.SyncRefreshCount - lastStats.SyncRefreshCount} index={index}{Environment.NewLine}"));
                        lastStats = stats;
                    }

                    presentedThisSecond = 0;
                    second = now;
                }
            }
        }
        finally
        {
            Marshal.Release(swapChain);
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
