using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using VIZEN.App.Capture;
using VIZEN.App.Interop;
using VIZEN.App.ViewModels;

namespace VIZEN.App.Views;

/// <summary>
/// Borderless, always-on-top window that shows a live capture.
/// Opacity and click-through are applied at the Win32 level (layered + transparent extended styles)
/// so that click-through truly passes mouse input to the window underneath.
/// </summary>
public partial class OverlayWindow : Window
{
    private readonly OverlayViewModel _overlay;
    private readonly TopmostLayerStyle _style;
    private bool _fittedToFirstFrame;

    // Native video: a swap chain in a child window, drawn on the capture thread (no WPF image per frame).
    private NativeVideoHost? _nativeHost;
    private NativeVideoPresenter? _presenter;
    private WindowCapture? _nativeCapture;
    private bool _popupChrome;
    private readonly DispatcherTimer _hoverCheck;
    private const int GWL_STYLE = -16;
    private const long WS_CLIPCHILDREN = 0x02000000;

    public OverlayWindow(OverlayViewModel overlay)
    {
        InitializeComponent();
        _overlay = overlay;
        DataContext = overlay;
        _style = new TopmostLayerStyle(this, () => _overlay.IsClickThrough, perPixelAlpha: false);

        // A restored profile already has the size the user chose; and a shared capture that is already
        // running was used for the initial size. Only fit later if we're still waiting for the first frame.
        _fittedToFirstFrame = overlay.InitialBounds is not null || overlay.Capture.HasFrame;

        SourceInitialized += OnSourceInitialized;
        LocationChanged += (_, _) => TrackBounds();
        SizeChanged += (_, _) => TrackBounds();
        overlay.Capture.PropertyChanged += OnCapturePropertyChanged;
        overlay.PropertyChanged += OnOverlayPropertyChanged;
        overlay.Closed += OnOverlayClosed;
        Closed += OnWindowClosed;
        Loaded += (_, _) => StartNativeVideo();
        _hoverCheck = new DispatcherTimer(TimeSpan.FromMilliseconds(150), DispatcherPriority.Input, (_, _) => UpdateHoverPopup(), Dispatcher);
        Root.MouseEnter += (_, _) => UpdateHoverPopup();
        Root.MouseLeave += (_, _) => _hoverCheck.Start();
        HoverBar.MouseLeave += (_, _) => _hoverCheck.Start();
        Root.SizeChanged += (_, _) => PositionBadge();
        Badge.SizeChanged += (_, _) => PositionBadge();
        LocationChanged += (_, _) => NudgePopups();
        BadgePopup.Opened += (_, _) => MakePopupClickThrough(BadgePopup);
    }

    // ---------- native video ----------

    private void StartNativeVideo()
    {
        if (_nativeHost is not null || _overlay.IsClosed)
        {
            return;
        }

        if (_overlay.Capture is not WindowCapture { SupportsNativePresentation: true } capture)
        {
            _overlay.Lease.SetNativePresenter(false); // the WPF image needs CPU copies
            return;
        }

        MemTrace.Mark("overlay: window loaded");
        _nativeCapture = capture;
        capture.NativePresentersReset += OnNativePresentersReset;

        // Keep WPF from painting its background over the video child window.
        var hwnd = new WindowInteropHelper(this).Handle;
        NativeMethods.SetWindowLongPtr(hwnd, GWL_STYLE, NativeMethods.GetWindowLongPtr(hwnd, GWL_STYLE) | WS_CLIPCHILDREN);
        _nativeHost = new NativeVideoHost();
        _nativeHost.PixelSizeChanged += OnNativeSizeChanged;
        UpdateNativeVisibility();
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

        _presenter = _nativeCapture.AttachPresenter(hwnd, width, height, _overlay.Viewbox, flipModel: Environment.GetEnvironmentVariable("VIZEN_SWAP") != "blt");
        if (_presenter is null)
        {
            StopNativeVideo(); // no swap chain here: keep the WPF image
            return;
        }

        _overlay.Lease.SetNativePresenter(true);
        EnterPopupChrome();
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

        _overlay.Lease.SetNativePresenter(false);
        if (_nativeHost is not null)
        {
            _nativeHost.PixelSizeChanged -= OnNativeSizeChanged;
            NativeSlot.Content = null;
            _nativeHost.Dispose();
            _nativeHost = null;
        }

        ExitPopupChrome();
    }

    /// <summary>The capture moved to a new GPU device: its presenters are gone, attach a new one.</summary>
    private void OnNativePresentersReset(object? sender, EventArgs e)
    {
        _presenter = null; // already released by the capture
        StopNativeVideo();
        StartNativeVideo();
    }

    /// <summary>The native window covers the status text, so it is only shown once there is a frame.</summary>
    private void UpdateNativeVisibility()
    {
        if (_nativeHost is not null)
        {
            _nativeHost.Visibility = _overlay.Capture.HasFrame ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    // ---------- hover bar and badge as popups (WPF cannot draw over a native child window) ----------

    private void EnterPopupChrome()
    {
        if (_popupChrome)
        {
            return;
        }

        _popupChrome = true;
        Root.Children.Remove(HoverBar);
        HoverBar.Style = null;
        HoverBar.Visibility = Visibility.Visible;
        HoverPopup.Child = HoverBar;
        Root.Children.Remove(Badge);
        BadgePopup.Child = Badge;
        HoverOutline.Visibility = Visibility.Collapsed;

        // The native window shows the video: don't keep a hidden WPF copy of it rendering underneath.
        WpfVideo.Visibility = Visibility.Collapsed;
        UpdateBadgePopup();

        // WPF now only draws the background behind the video (the bar and badge are popups, separate windows), which
        // the CPU does at no cost. Measured over 12 minutes with 4 overlays: WPF's GPU renderer for these windows held
        // ~34 MB more GPU memory and slowly grew the process memory; software rendering kept both flat.
        SetWpfRenderMode(RenderMode.SoftwareOnly);
    }

    /// <summary>WPF renderer for this window only; the WPF image (camera, CPU path) needs the GPU renderer.</summary>
    private void SetWpfRenderMode(RenderMode mode)
    {
        // Also reached while the window is being destroyed (Closed → StopNativeVideo): there is no window left to change.
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero && HwndSource.FromHwnd(hwnd) is { IsDisposed: false, CompositionTarget: { } target })
        {
            target.RenderMode = mode;
        }
    }

    private void ExitPopupChrome()
    {
        if (!_popupChrome)
        {
            return;
        }

        _popupChrome = false;
        HoverPopup.IsOpen = false;
        BadgePopup.IsOpen = false;
        HoverPopup.Child = null;
        BadgePopup.Child = null;
        HoverBar.ClearValue(StyleProperty);
        HoverBar.ClearValue(VisibilityProperty);
        Root.Children.Add(Badge);
        Root.Children.Add(HoverBar);
        HoverOutline.ClearValue(VisibilityProperty);
        WpfVideo.ClearValue(VisibilityProperty);
        SetWpfRenderMode(RenderMode.Default);
    }

    private void UpdateHoverPopup()
    {
        _hoverCheck.Stop();
        if (_popupChrome)
        {
            HoverPopup.IsOpen = IsVisible && !_overlay.IsClickThrough && (Root.IsMouseOver || HoverBar.IsMouseOver);
        }
    }

    private void UpdateBadgePopup()
    {
        if (_popupChrome)
        {
            BadgePopup.IsOpen = IsVisible && _overlay.ShowPerformance;
            PositionBadge();
        }
    }

    private void PositionBadge()
    {
        if (_popupChrome)
        {
            BadgePopup.VerticalOffset = Math.Max(0, Root.ActualHeight - Badge.ActualHeight - Badge.Margin.Top - Badge.Margin.Bottom);
        }
    }

    /// <summary>Popups don't follow a moved window by themselves; a tiny offset change re-places them.</summary>
    private void NudgePopups()
    {
        foreach (var popup in new[] { HoverPopup, BadgePopup })
        {
            if (popup.IsOpen)
            {
                var offset = popup.HorizontalOffset;
                popup.HorizontalOffset = offset + 0.01;
                popup.HorizontalOffset = offset;
            }
        }
    }

    private static void MakePopupClickThrough(System.Windows.Controls.Primitives.Popup popup)
    {
        if (popup.Child is not null && PresentationSource.FromVisual(popup.Child) is HwndSource source)
        {
            var style = NativeMethods.GetWindowLongPtr(source.Handle, NativeMethods.GWL_EXSTYLE);
            NativeMethods.SetWindowLongPtr(source.Handle, NativeMethods.GWL_EXSTYLE,
                style | NativeMethods.WS_EX_TRANSPARENT | NativeMethods.WS_EX_LAYERED | NativeMethods.WS_EX_NOACTIVATE);
        }
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _style.Attach();
        _style.ApplyOpacity(_overlay.Opacity);
    }

    private void OnOverlayPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(OverlayViewModel.Opacity):
                _style.ApplyOpacity(_overlay.Opacity);
                break;
            case nameof(OverlayViewModel.IsClickThrough):
                _style.ApplyExtendedStyle();
                UpdateHoverPopup();
                break;
            case nameof(OverlayViewModel.IsVisible):
                if (_overlay.IsVisible)
                {
                    Show();
                }
                else
                {
                    Hide();
                }

                UpdateHoverPopup();
                UpdateBadgePopup();
                break;
            case nameof(OverlayViewModel.ShowPerformance):
                UpdateBadgePopup();
                break;
            case nameof(OverlayViewModel.Capture):
                // The source was reconnected to a new window: show the new capture.
                StopNativeVideo();
                StartNativeVideo();
                break;
        }
    }

    private void TrackBounds()
    {
        if (WindowState == WindowState.Normal && ActualWidth > 0)
        {
            _overlay.Bounds = new Rect(Left, Top, ActualWidth, ActualHeight);
        }
    }

    private void OnCapturePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LiveCapture.HasFrame))
        {
            UpdateNativeVisibility();
        }

        // The initial size is a guess; match the real aspect ratio once, when the first frame arrives.
        if (e.PropertyName == nameof(LiveCapture.HasFrame) && _overlay.Capture.HasFrame && !_fittedToFirstFrame)
        {
            _fittedToFirstFrame = true;
            var bottom = Top + ActualHeight;
            FitToSourceAspect();
            Top = Math.Max(SystemParameters.WorkArea.Top, bottom - Height); // keep the bottom edge anchored
        }
    }

    private void OnRootMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            FitToSourceAspect();
            return;
        }

        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    /// <summary>Removes letterboxing by matching the window height to the shown content's aspect ratio (region or whole window).</summary>
    private void FitToSourceAspect()
    {
        if (_overlay.ContentAspect is not { } aspect || aspect <= 0)
        {
            return;
        }

        var width = ActualWidth > 0 ? ActualWidth : Width;
        Height = Math.Clamp(width / aspect, MinHeight, SystemParameters.WorkArea.Height * 0.9);
    }

    private void OnOverlayClosed(object? sender, EventArgs e) => Close();

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        _hoverCheck.Stop();
        StopNativeVideo();
        _overlay.PropertyChanged -= OnOverlayPropertyChanged;
        _overlay.Capture.PropertyChanged -= OnCapturePropertyChanged;
        _overlay.Closed -= OnOverlayClosed;
        _overlay.Close(); // no-op if the overlay initiated the close
    }
}
