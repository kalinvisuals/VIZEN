using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Shapes;
using VIZEN.App.Capture;

namespace VIZEN.App.Views;

/// <summary>
/// In-app live preview of a capture. Like the overlays (and OBS's preview), it shows the video through a native swap
/// chain drawn on the capture thread from the GPU frame — no CPU copy, no WPF bitmap — at the source's frame rate.
/// Falls back to the WPF image when the capture cannot present natively (camera, CPU renderer).
/// A native child window is not clipped by WPF, so the control clips it itself to the visible part of any scrolling
/// ancestor, with rounded corners. Nothing WPF can be drawn on top of the video.
/// </summary>
public sealed class LivePreview : Grid
{
    public static readonly DependencyProperty CaptureProperty = DependencyProperty.Register(
        nameof(Capture), typeof(LiveCapture), typeof(LivePreview), new PropertyMetadata(null, (d, _) => ((LivePreview)d).OnCaptureChanged()));

    public static readonly DependencyProperty ViewboxProperty = DependencyProperty.Register(
        nameof(Viewbox), typeof(Rect), typeof(LivePreview), new PropertyMetadata(new Rect(0, 0, 1, 1), (d, _) => ((LivePreview)d).OnViewboxChanged()));

    public static readonly DependencyProperty LeaseProperty = DependencyProperty.Register(
        nameof(Lease), typeof(CaptureLease), typeof(LivePreview), new PropertyMetadata(null, (d, e) => ((LivePreview)d).OnLeaseChanged((CaptureLease?)e.OldValue)));

    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.Register(
        nameof(CornerRadius), typeof(double), typeof(LivePreview), new PropertyMetadata(0.0));

    private readonly Rectangle _wpfVideo = new();
    private readonly ImageBrush _wpfBrush = new() { ViewboxUnits = BrushMappingMode.RelativeToBoundingBox, Stretch = Stretch.Uniform };
    private readonly ContentControl _slot = new() { Focusable = false, IsTabStop = false };
    private NativeVideoHost? _host;
    private NativeVideoPresenter? _presenter;
    private WindowCapture? _nativeCapture;
    private LiveCapture? _observed;
    private Int32Rect _clip = new(-1, -1, -1, -1);
    private WindowCapture? _failed;

    public LivePreview()
    {
        RenderOptions.SetBitmapScalingMode(_wpfVideo, BitmapScalingMode.Linear);
        _wpfVideo.Fill = _wpfBrush;
        Children.Add(_wpfVideo);
        Children.Add(_slot);
        Loaded += (_, _) => UpdateMode();
        Unloaded += (_, _) => Stop();
        IsVisibleChanged += (_, _) => UpdateMode();
        LayoutUpdated += (_, _) => UpdateClip();
    }

    public LiveCapture? Capture
    {
        get => (LiveCapture?)GetValue(CaptureProperty);
        set => SetValue(CaptureProperty, value);
    }

    /// <summary>Part of the frame to show (0..1); the whole frame by default.</summary>
    public Rect Viewbox
    {
        get => (Rect)GetValue(ViewboxProperty);
        set => SetValue(ViewboxProperty, value);
    }

    /// <summary>The lease the preview's capture was acquired with: told when no CPU copy is needed for this consumer.</summary>
    public CaptureLease? Lease
    {
        get => (CaptureLease?)GetValue(LeaseProperty);
        set => SetValue(LeaseProperty, value);
    }

    /// <summary>Rounded corners of the native video (device-independent pixels).</summary>
    public double CornerRadius
    {
        get => (double)GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    private void OnCaptureChanged()
    {
        if (_observed is not null)
        {
            _observed.PropertyChanged -= OnCapturePropertyChanged;
        }

        _observed = Capture;
        if (_observed is not null)
        {
            _observed.PropertyChanged += OnCapturePropertyChanged;
        }

        BindingOperations.SetBinding(_wpfBrush, ImageBrush.ImageSourceProperty, new Binding(nameof(LiveCapture.Frame)) { Source = _observed });
        Stop();
        UpdateMode();
    }

    private void OnViewboxChanged()
    {
        _wpfBrush.Viewbox = Viewbox;
        if (_presenter is not null)
        {
            _presenter.Viewbox = Viewbox;
        }
    }

    private void OnLeaseChanged(CaptureLease? old)
    {
        old?.SetNativePresenter(false);
        Lease?.SetNativePresenter(_presenter is not null);
    }

    private void OnCapturePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(LiveCapture.HasFrame) or nameof(LiveCapture.Status))
        {
            UpdateMode();
        }
    }

    private void UpdateMode()
    {
        var wanted = IsLoaded && IsVisible
            && !ReferenceEquals(Capture, _failed)
            && Capture is WindowCapture { SupportsNativePresentation: true } && Capture.HasFrame;
        if (!wanted)
        {
            // A capture that is not live (yet) keeps its status text visible underneath.
            if (Capture is not WindowCapture { SupportsNativePresentation: true } || ReferenceEquals(Capture, _failed))
            {
                Stop(); // the WPF image needs CPU copies (Stop tells the lease)
            }
            else if (_host is not null)
            {
                _host.Visibility = Visibility.Collapsed;
            }

            return;
        }

        if (_host is null)
        {
            _nativeCapture = (WindowCapture)Capture!;
            _nativeCapture.NativePresentersReset += OnPresentersReset;
            _host = new NativeVideoHost();
            _host.PixelSizeChanged += OnPixelSizeChanged;
            _slot.Content = _host;
        }

        _host.Visibility = Visibility.Visible;
    }

    private void OnPixelSizeChanged(IntPtr hwnd, int width, int height)
    {
        if (_presenter is not null)
        {
            _presenter.Resize(width, height);
            return;
        }

        if (width <= 1 || height <= 1 || _nativeCapture is null)
        {
            return;
        }

        _presenter = _nativeCapture.AttachPresenter(hwnd, width, height, Viewbox, flipModel: true);
        if (_presenter is null)
        {
            _failed = _nativeCapture;
            Stop(); // no swap chain here: keep the WPF image for this capture
            return;
        }

        if (TryFindResource("Brush.Background") is SolidColorBrush { Color: var c })
        {
            _presenter.Background = (c.R / 255f, c.G / 255f, c.B / 255f);
        }

        _wpfVideo.Visibility = Visibility.Collapsed;
        Lease?.SetNativePresenter(true);
        _clip = new Int32Rect(-1, -1, -1, -1);
        UpdateClip();
    }

    private void OnPresentersReset(object? sender, EventArgs e)
    {
        _presenter = null; // already released by the capture
        Stop();
        UpdateMode();
    }

    private void Stop()
    {
        if (_presenter is not null)
        {
            _nativeCapture?.DetachPresenter(_presenter);
            _presenter = null;
        }

        if (_nativeCapture is not null)
        {
            _nativeCapture.NativePresentersReset -= OnPresentersReset;
            _nativeCapture = null;
        }

        Lease?.SetNativePresenter(false);
        if (_host is not null)
        {
            _host.PixelSizeChanged -= OnPixelSizeChanged;
            _slot.Content = null;
            _host.Dispose();
            _host = null;
        }

        _wpfVideo.Visibility = Visibility.Visible;
    }

    // ---------- clipping of the native child window ----------

    private void UpdateClip()
    {
        if (_host is not { ChildHandle: var hwnd } || hwnd == IntPtr.Zero || _host.ActualWidth <= 0 || PresentationSource.FromVisual(_host) is not { CompositionTarget: { } target })
        {
            return;
        }

        var visible = new Rect(0, 0, _host.ActualWidth, _host.ActualHeight);
        for (DependencyObject? node = VisualTreeHelper.GetParent(_host); node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is ScrollContentPresenter or ScrollViewer || (node is UIElement { ClipToBounds: true } && node is not Window))
            {
                var element = (FrameworkElement)node;
                try
                {
                    var bounds = element.TransformToDescendant(_host).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
                    visible.Intersect(bounds);
                }
                catch (InvalidOperationException)
                {
                    // Not in the same visual tree (being unloaded).
                }

                if (visible.IsEmpty)
                {
                    break;
                }
            }
        }

        var toDevice = target.TransformToDevice;
        var clip = visible.IsEmpty
            ? new Int32Rect(0, 0, 0, 0)
            : new Int32Rect((int)Math.Floor(visible.X * toDevice.M11), (int)Math.Floor(visible.Y * toDevice.M22),
                (int)Math.Ceiling(visible.Width * toDevice.M11), (int)Math.Ceiling(visible.Height * toDevice.M22));
        if (clip == _clip)
        {
            return;
        }

        _clip = clip;
        var fullW = (int)Math.Ceiling(_host.ActualWidth * toDevice.M11);
        var fullH = (int)Math.Ceiling(_host.ActualHeight * toDevice.M22);
        var radius = (int)Math.Round(CornerRadius * 2 * toDevice.M11);
        var region = radius > 0 ? CreateRoundRectRgn(0, 0, fullW + 1, fullH + 1, radius, radius) : CreateRectRgn(0, 0, fullW, fullH);
        var visibleRegion = CreateRectRgn(clip.X, clip.Y, clip.X + clip.Width, clip.Y + clip.Height);
        CombineRgn(region, region, visibleRegion, RGN_AND);
        DeleteObject(visibleRegion);
        if (SetWindowRgn(hwnd, region, true) == 0)
        {
            DeleteObject(region); // the window did not take ownership
        }
    }

    private const int RGN_AND = 1;

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int widthEllipse, int heightEllipse);

    [DllImport("gdi32.dll")]
    private static extern int CombineRgn(IntPtr dest, IntPtr src1, IntPtr src2, int mode);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr hwnd, IntPtr region, bool redraw);
}
