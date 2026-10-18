using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using VIZEN.App.Capture;

namespace VIZEN.App.Views;

/// <summary>
/// Test source for measuring real window capture without a game: a borderless window that changes at exactly
/// <c>fps</c> frames per second (moving circles, a scrolling bar, a big frame counter) and carries a frame-number
/// marker in its top-left corner (<see cref="FrameMarker"/>). Started as a separate process
/// (<c>VIZEN.App.exe --test-pattern 60 1</c>) so it is captured like any other application.
/// </summary>
public sealed class TestPatternWindow : Window
{
    public const string Argument = "--test-pattern";
    private const int BlockDip = 8;

    private readonly int _fps;
    private readonly Canvas _canvas = new();
    private readonly Rectangle[] _marker = new Rectangle[FrameMarker.Blocks];
    private readonly Ellipse[] _balls = new Ellipse[6];
    private readonly Rectangle _bar = new() { Fill = Brushes.White };
    private readonly TextBlock _counter = new() { Foreground = Brushes.White, FontFamily = new FontFamily("Segoe UI"), FontWeight = FontWeights.SemiBold };
    private readonly Stopwatch _clock = new();
    private readonly int _pixelWidth;
    private readonly int _pixelHeight;
    private long _frame = -1;

    // Source-side accounting (VIZEN_PATTERN_LOG): frames actually drawn, and frame numbers this window skipped itself.
    private readonly string? _log;
    private int _drawnThisSecond;
    private long _selfSkippedThisSecond;
    private long _secondStart;

    public TestPatternWindow(int fps, int id, int pixelWidth, int pixelHeight)
    {
        _fps = Math.Clamp(fps, 1, 240);
        _pixelWidth = pixelWidth;
        _pixelHeight = pixelHeight;
        Title = $"{WindowCapture.TestPatternTitlePrefix} {id} · {_fps} fps";
        _log = Environment.GetEnvironmentVariable("VIZEN_PATTERN_LOG") is { Length: > 0 } dir ? System.IO.Path.Combine(dir, $"pattern-{id}.log") : null;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        Background = Brushes.Black;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        // Fully on screen: Windows does not render the off-screen part of a window, which would arrive black.
        Left = 0;
        Top = 0;
        Content = _canvas;

        for (var i = 0; i < _balls.Length; i++)
        {
            _balls[i] = new Ellipse { Fill = new SolidColorBrush(Color.FromRgb((byte)(80 + i * 30), (byte)(200 - i * 25), 255)) };
            _canvas.Children.Add(_balls[i]);
        }

        _canvas.Children.Add(_bar);
        _canvas.Children.Add(_counter);
        for (var k = 0; k < _marker.Length; k++)
        {
            _marker[k] = new Rectangle();
            _canvas.Children.Add(_marker[k]);
        }

        MouseLeftButtonDown += (_, _) => DragMove();
        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        Closed += (_, _) => CompositionTarget.Rendering -= OnRendering;
    }

    /// <summary>Parses "--test-pattern [fps] [id] [WxH]" from the command line; null when it is not a test-pattern start.</summary>
    public static TestPatternWindow? FromArguments(string[] args)
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

        return new TestPatternWindow(Arg(1, 60), Arg(2, 1), w, h);
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        // Square corners: Windows 11 would otherwise round off the marker's first block.
        var hwnd = new WindowInteropHelper(this).Handle;
        var preference = 1; // DWMWCP_DONOTROUND
        _ = DwmSetWindowAttribute(hwnd, 33, ref preference, sizeof(int));
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        Width = _pixelWidth / dpi.DpiScaleX;
        Height = _pixelHeight / dpi.DpiScaleY;

        // Marker blocks a whole number of physical pixels wide, so the decoder sees clean edges.
        var block = Math.Max(2, Math.Round(BlockDip * dpi.DpiScaleX)) / dpi.DpiScaleX;
        for (var k = 0; k < _marker.Length; k++)
        {
            _marker[k].Width = block;
            _marker[k].Height = block;
            Canvas.SetLeft(_marker[k], k * block);
            Canvas.SetTop(_marker[k], 0);
        }

        _clock.Start();
        CompositionTarget.Rendering += OnRendering;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        var due = (long)(_clock.Elapsed.TotalSeconds * _fps);
        if (due == _frame)
        {
            return;
        }

        if (_frame >= 0 && due > _frame + 1)
        {
            _selfSkippedThisSecond += due - _frame - 1; // this window was too late to draw these numbers at all
        }

        _frame = due;
        _drawnThisSecond++;
        var tick = _clock.ElapsedTicks;
        if (_log is not null && tick - _secondStart >= Stopwatch.Frequency)
        {
            System.IO.File.AppendAllText(_log, string.Create(CultureInfo.InvariantCulture,
                $"{DateTime.Now:HH:mm:ss} drawn={_drawnThisSecond} selfSkipped={_selfSkippedThisSecond} index={_frame}{Environment.NewLine}"));
            _drawnThisSecond = 0;
            _selfSkippedThisSecond = 0;
            _secondStart = tick;
        }
        var w = ActualWidth;
        var h = ActualHeight;
        var n = (double)_frame;
        Background = new SolidColorBrush(Color.FromRgb((byte)(20 + _frame % 40), 24, (byte)(40 + _frame % 60)));
        for (var i = 0; i < _balls.Length; i++)
        {
            var size = h * 0.14;
            _balls[i].Width = size;
            _balls[i].Height = size;
            Canvas.SetLeft(_balls[i], w / 2 + Math.Cos(n / 20 + i) * w * 0.35 - size / 2);
            Canvas.SetTop(_balls[i], h / 2 + Math.Sin(n / 13 + i) * h * 0.30 - size / 2);
        }

        _bar.Width = Math.Max(4, w * 0.02);
        _bar.Height = h;
        Canvas.SetLeft(_bar, (_frame * 12) % Math.Max(1, (long)w));
        _counter.FontSize = h * 0.11;
        _counter.Text = $"{_fps} fps  ·  frame {_frame}";
        Canvas.SetLeft(_counter, w * 0.04);
        Canvas.SetTop(_counter, h * 0.78);

        var index = (uint)_frame;
        for (var k = 0; k < _marker.Length; k++)
        {
            var white = k == 0 || (k >= 2 && ((index >> (FrameMarker.Bits - 1 - (k - 2))) & 1) == 1);
            _marker[k].Fill = white ? Brushes.White : Brushes.Black;
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
