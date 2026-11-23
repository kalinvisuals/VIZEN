using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using VIZEN.App.Capture;
using VIZEN.App.Interop;
using VIZEN.App.ViewModels;

namespace VIZEN.App.Views;

/// <summary>
/// Clean output window for OBS Window Capture. It is a normal top-level window (not a tool window, so OBS
/// lists it) with square corners and nothing but the content. The controls live in VIZEN's Studio page; under the
/// pointer a small bar with a close button appears in a separate window that captures skip.
/// </summary>
public partial class OutputWindow : Window
{
    private const int DWMWCP_DONOTROUND = 1;

    private readonly OutputViewModel _output;

    public OutputWindow(OutputViewModel output)
    {
        InitializeComponent();
        _output = output;
        DataContext = output;

        SourceInitialized += OnSourceInitialized;
        SizeChanged += (_, _) => TrackBounds();
        LocationChanged += (_, _) => TrackBounds();
        output.SizeRequested += OnSizeRequested;
        output.BringToFrontRequested += OnBringToFrontRequested;
        output.Closed += OnOutputClosed;
        Closed += OnWindowClosed;

        // Hover bar: decided from the real pointer position (polled only while the pointer is near), like the overlays.
        _hoverCheck = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Input, (_, _) => CheckHover(), Dispatcher);
        Root.MouseEnter += (_, _) => StartHoverCheck();
        Root.MouseMove += (_, _) => StartHoverCheck();
        HoverPopup.Opened += (_, _) => ExcludeFromCapture(HoverPopup);
        LocationChanged += (_, _) => NudgePopup();
        SizeChanged += (_, _) => NudgePopup();

        Loaded += (_, _) => StartNativeVideo();
        output.PropertyChanged += OnOutputPropertyChanged;
    }

    private readonly DispatcherTimer _hoverCheck;

    // ---------- native video ----------
    // Like the overlays: the capture thread draws each new GPU frame into a swap chain of a child window, at the
    // source's frame rate. The WPF image (a CPU copy of every frame, redrawn by WPF on the UI thread) stuttered.
    // The blt swap-chain model draws into the window's own surface, so OBS sees it with BitBlt capture as well as with
    // Windows Graphics Capture. The child window is never hidden, only created and destroyed: a hidden video window
    // once left a frozen picture on screen.

    private NativeVideoHost? _nativeHost;
    private NativeVideoPresenter? _presenter;
    private WindowCapture? _nativeCapture;
    private const int GWL_STYLE = -16;
    private const long WS_CLIPCHILDREN = 0x02000000;

    private void StartNativeVideo()
    {
        if (_nativeHost is not null || !IsLoaded || _output.Capture is not WindowCapture { SupportsNativePresentation: true } capture)
        {
            return; // the WPF image shows the capture (camera, CPU renderer)
        }

        _nativeCapture = capture;
        capture.NativePresentersReset += OnNativePresentersReset;
        var hwnd = new WindowInteropHelper(this).Handle;
        NativeMethods.SetWindowLongPtr(hwnd, GWL_STYLE, NativeMethods.GetWindowLongPtr(hwnd, GWL_STYLE) | WS_CLIPCHILDREN);
        _nativeHost = new NativeVideoHost();
        _nativeHost.PixelSizeChanged += OnNativeSizeChanged;
        NativeSlot.Content = _nativeHost; // the child window is created by the next layout pass
    }

    private void OnNativeSizeChanged(IntPtr hwnd, int width, int height)
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

        _presenter = _nativeCapture.AttachPresenter(hwnd, width, height, _output.Viewbox, flipModel: false);
        if (_presenter is null)
        {
            StopNativeVideo(); // no swap chain here: keep the WPF image
            return;
        }

        ApplyPresenterLook();
        _output.Lease.SetNativePresenter(true);
        WpfVideo.Visibility = Visibility.Collapsed; // no hidden WPF copy rendering underneath
    }

    private void ApplyPresenterLook()
    {
        if (_presenter is null)
        {
            return;
        }

        _presenter.Fit = _output.Stretch;
        _presenter.Background = _output.Background == OutputBackground.Green ? (0f, 1f, 0f) : (0f, 0f, 0f);
        _presenter.Render(); // show the change now, also while the source is paused
    }

    private void StopNativeVideo()
    {
        if (_presenter is not null)
        {
            _nativeCapture?.DetachPresenter(_presenter);
            _presenter = null;
        }

        if (_nativeCapture is not null)
        {
            _nativeCapture.NativePresentersReset -= OnNativePresentersReset;
            _nativeCapture = null;
        }

        _output.Lease.SetNativePresenter(false);
        if (_nativeHost is not null)
        {
            _nativeHost.PixelSizeChanged -= OnNativeSizeChanged;
            NativeSlot.Content = null;
            _nativeHost.Dispose();
            _nativeHost = null;
        }

        WpfVideo.ClearValue(VisibilityProperty);
    }

    /// <summary>The capture moved to a new GPU device: its presenters are gone, attach a new one.</summary>
    private void OnNativePresentersReset(object? sender, EventArgs e)
    {
        _presenter = null; // already released by the capture
        StopNativeVideo();
        StartNativeVideo();
    }

    private void OnOutputPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(OutputViewModel.Stretch):
            case nameof(OutputViewModel.BackgroundBrush):
                ApplyPresenterLook();
                break;
            case nameof(OutputViewModel.Capture):
                // The source was reconnected to a new window: show the new capture.
                StopNativeVideo();
                StartNativeVideo();
                break;
        }
    }

    private void StartHoverCheck()
    {
        if (!_hoverCheck.IsEnabled)
        {
            CheckHover();
            _hoverCheck.Start();
        }
    }

    private void CheckHover()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var inside = hwnd != IntPtr.Zero && WindowState == WindowState.Normal && IsVisible
            && NativeMethods.GetCursorPos(out var p) && NativeMethods.GetWindowRect(hwnd, out var r)
            && p.X >= r.Left && p.X < r.Right && p.Y >= r.Top && p.Y < r.Bottom;
        if (!inside)
        {
            _hoverCheck.Stop();
        }

        HoverPopup.IsOpen = inside;
    }

    /// <summary>The bar is a separate window that screen and window capture skip: OBS never shows it.</summary>
    private static void ExcludeFromCapture(System.Windows.Controls.Primitives.Popup popup)
    {
        if (popup.Child is not null && PresentationSource.FromVisual(popup.Child) is HwndSource source)
        {
            SetWindowDisplayAffinity(source.Handle, WDA_EXCLUDEFROMCAPTURE);
        }
    }

    /// <summary>Popups don't follow a moved window by themselves; a tiny offset change re-places it.</summary>
    private void NudgePopup()
    {
        if (HoverPopup.IsOpen)
        {
            var offset = HoverPopup.HorizontalOffset;
            HoverPopup.HorizontalOffset = offset + 0.01;
            HoverPopup.HorizontalOffset = offset;
        }
    }

    private const uint WDA_EXCLUDEFROMCAPTURE = 0x11;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        // Windows 11 rounds window corners; a capture source should be a plain rectangle.
        var hwnd = new WindowInteropHelper(this).Handle;
        var corner = DWMWCP_DONOTROUND;
        NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
    }

    /// <summary>
    /// Sets the client size from a physical-pixel size (what OBS will capture), limited to the screen size,
    /// and returns the size actually achieved.
    /// </summary>
    public Size SetPixelSize(Size pixels) => ApplyPixelSize(this, pixels);

    /// <summary>Shared by every OBS window: sizes <paramref name="window"/> in physical pixels, limited to the virtual screen.</summary>
    internal static Size ApplyPixelSize(Window window, Size pixels)
    {
        var dpi = VisualTreeHelper.GetDpi(window);
        window.Width = Math.Clamp(pixels.Width / dpi.DpiScaleX, window.MinWidth, Math.Max(window.MinWidth, SystemParameters.VirtualScreenWidth));
        window.Height = Math.Clamp(pixels.Height / dpi.DpiScaleY, window.MinHeight, Math.Max(window.MinHeight, SystemParameters.VirtualScreenHeight));
        window.UpdateLayout();
        return new Size(Math.Round(window.ActualWidth * dpi.DpiScaleX), Math.Round(window.ActualHeight * dpi.DpiScaleY));
    }

    private void OnSizeRequested(object? sender, OutputSizeRequest request) => request.Applied = SetPixelSize(request.Requested);

    private void OnBringToFrontRequested(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
    }

    private void TrackBounds()
    {
        if (WindowState != WindowState.Normal || ActualWidth <= 0)
        {
            return;
        }

        _output.Bounds = new Rect(Left, Top, ActualWidth, ActualHeight);
        var dpi = VisualTreeHelper.GetDpi(this);
        _output.ReportPixelSize((int)Math.Round(ActualWidth * dpi.DpiScaleX), (int)Math.Round(ActualHeight * dpi.DpiScaleY));
    }

    private void OnContentMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void OnOutputClosed(object? sender, EventArgs e) => Close();

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        _hoverCheck.Stop();
        HoverPopup.IsOpen = false;
        StopNativeVideo();
        _output.PropertyChanged -= OnOutputPropertyChanged;
        _output.SizeRequested -= OnSizeRequested;
        _output.BringToFrontRequested -= OnBringToFrontRequested;
        _output.Closed -= OnOutputClosed;
        _output.Close(); // no-op if the output initiated the close
    }
}
