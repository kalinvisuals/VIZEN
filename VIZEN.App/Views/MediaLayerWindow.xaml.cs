using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using VIZEN.App.Interop;
using VIZEN.App.Localization;
using VIZEN.App.Media;
using VIZEN.App.Models;
using VIZEN.App.ViewModels;

namespace VIZEN.App.Views;

/// <summary>
/// Shows one media layer. As an overlay it is always on top, click-through capable, and — for images and
/// GIFs — truly transparent per pixel (PNG alpha is kept). Videos use an opaque layered window instead,
/// because per-pixel transparency would copy every video frame through the CPU. As an OBS output it is a
/// normal, borderless window with a solid (chroma-key) background and no UI.
/// </summary>
public partial class MediaLayerWindow : Window
{
    private readonly MediaLayerViewModel _layer;
    private readonly TopmostLayerStyle? _style;
    private Task<bool>? _loading;
    private bool _placedByUser;
    private bool _animating;
    private Point _restPosition;
    private double _transitionFactor = 1;
    private EventHandler? _tween;

    public MediaLayerWindow(MediaLayerViewModel layer)
    {
        _layer = layer;
        IsOutput = layer.IsOutput;
        var perPixelAlpha = !IsOutput && layer.Kind != MediaKind.Video;
        if (perPixelAlpha)
        {
            AllowsTransparency = true;
        }

        InitializeComponent();
        DataContext = layer;

        if (IsOutput)
        {
            // Clean OBS source: normal window (listed by OBS), no hover UI, solid background.
            Topmost = false;
            ShowInTaskbar = true;
            HoverChrome.Visibility = Visibility.Collapsed;
            Root.Background = Brushes.Transparent;
            ApplyOutputBackground();
        }
        else
        {
            Background = perPixelAlpha ? Brushes.Transparent : Brushes.Black;
            _style = new TopmostLayerStyle(this, () => _layer.ClickThrough, perPixelAlpha);
            ApplyBorder();
        }

        ApplyLock();
        SourceInitialized += OnSourceInitialized;
        LocationChanged += (_, _) => TrackBounds();
        SizeChanged += (_, _) => TrackBounds();
        layer.PropertyChanged += OnLayerPropertyChanged;
        layer.PlaybackRequested += OnPlaybackRequested;
        layer.PlacementRequested += OnPlacementRequested;
        layer.FitRequested += OnFitRequested;
        layer.ScaleRequested += OnScaleRequested;
        Media.VideoOpened += OnVideoOpened;
        Closed += OnClosed;
    }

    public bool IsOutput { get; }

    public IntPtr Handle => new WindowInteropHelper(this).Handle;

    /// <summary>Opens the file once (decoding GIFs in the background). False (and an error on the layer) on failure.</summary>
    public Task<bool> EnsureLoadedAsync() => _loading ??= LoadAsync();

    private async Task<bool> LoadAsync()
    {
        var error = await Media.LoadAsync(_layer, reportToLayer: true);
        if (error is not null)
        {
            ShowMessage(error);
            return false;
        }

        return true;
    }
    /// <summary>Places the window at its saved bounds, or at <paramref name="fallback"/> when it has none.</summary>
    public void Place(Rect? saved, Rect fallback)
    {
        var bounds = saved ?? fallback;
        _placedByUser = saved is not null;
        Left = bounds.Left;
        Top = bounds.Top;
        Width = Math.Max(MinWidth, bounds.Width);
        Height = Math.Max(MinHeight, bounds.Height);
        _restPosition = new Point(Left, Top);
    }

    /// <summary>Shows the layer with its in-transition and starts playback from the beginning.</summary>
    public void ShowLayer()
    {
        if (!IsVisible)
        {
            _restPosition = new Point(Left, Top);
            ApplyTransitionFrame(_layer.TransitionIn, 0);
            Show();
            _style?.ApplyExtendedStyle();
        }

        StartPlayback(fromStart: true);
        RunTransition(entering: true, _layer.TransitionIn, null);
    }

    /// <summary>Hides the layer with its out-transition and pauses playback.</summary>
    public void HideLayer(Action? completed = null)
    {
        if (!IsVisible)
        {
            completed?.Invoke();
            return;
        }

        RunTransition(entering: false, _layer.TransitionOut, () =>
        {
            Hide();
            PausePlayback();
            ResetTransitionFrame();
            completed?.Invoke();
        });
    }

    // ----- Playback -----

    private void StartPlayback(bool fromStart) => Media.Play(fromStart);

    private void PausePlayback() => Media.Pause();

    private void OnPlaybackRequested(object? sender, PlaybackAction action)
    {
        switch (action)
        {
            case PlaybackAction.Play:
                Media.Play(fromStart: false);
                break;
            case PlaybackAction.Pause:
                Media.Pause();
                break;
            case PlaybackAction.Stop:
                Media.Stop();
                break;
            case PlaybackAction.Restart:
                Media.Play(fromStart: true);
                break;
        }
    }

    private void OnVideoOpened(object? sender, EventArgs e)
    {
        if (!_placedByUser)
        {
            FitToContent();
        }
    }
    // ----- Transitions -----

    private void RunTransition(bool entering, MediaTransition transition, Action? completed)
    {
        StopTween();
        var seconds = _layer.TransitionSeconds;
        if (transition == MediaTransition.None || seconds <= 0)
        {
            ApplyTransitionFrame(transition, entering ? 1 : 0);
            completed?.Invoke();
            return;
        }

        _animating = true;
        var start = Stopwatch.GetTimestamp();
        var from = _transitionFactor;
        _tween = (_, _) =>
        {
            var progress = Math.Clamp(Stopwatch.GetElapsedTime(start).TotalSeconds / seconds, 0, 1);
            var eased = 1 - Math.Pow(1 - progress, 3);
            ApplyTransitionFrame(transition, entering ? from + (1 - from) * eased : from * (1 - eased));
            if (progress >= 1)
            {
                StopTween();
                _animating = false;
                completed?.Invoke();
            }
        };
        CompositionTarget.Rendering += _tween;
    }

    private void StopTween()
    {
        if (_tween is not null)
        {
            CompositionTarget.Rendering -= _tween;
            _tween = null;
        }
    }

    /// <summary>Applies a transition at <paramref name="shown"/> (0 = hidden, 1 = fully shown).</summary>
    private void ApplyTransitionFrame(MediaTransition transition, double shown)
    {
        _transitionFactor = shown;
        ApplyOpacity();

        var hidden = 1 - shown;
        var dx = transition switch
        {
            MediaTransition.SlideLeft => -SlideDistance(horizontal: true) * hidden,
            MediaTransition.SlideRight => SlideDistance(horizontal: true) * hidden,
            _ => 0,
        };
        var dy = transition switch
        {
            MediaTransition.SlideUp => SlideDistance(horizontal: false) * hidden,
            MediaTransition.SlideDown => -SlideDistance(horizontal: false) * hidden,
            _ => 0,
        };

        if (IsOutput)
        {
            // OBS captures a fixed window: slide the content inside it.
            StageShift.X = dx;
            StageShift.Y = dy;
        }
        else if (dx != 0 || dy != 0 || transition is MediaTransition.SlideLeft or MediaTransition.SlideRight
                 or MediaTransition.SlideUp or MediaTransition.SlideDown)
        {
            Left = _restPosition.X + dx;
            Top = _restPosition.Y + dy;
        }

        var scale = transition == MediaTransition.Zoom ? 0.6 + 0.4 * shown : 1;
        StageScale.ScaleX = scale;
        StageScale.ScaleY = scale;
    }

    private void ResetTransitionFrame()
    {
        _animating = true;
        _transitionFactor = 1;
        StageShift.X = StageShift.Y = 0;
        StageScale.ScaleX = StageScale.ScaleY = 1;
        if (!IsOutput)
        {
            Left = _restPosition.X;
            Top = _restPosition.Y;
        }

        _animating = false;
    }

    private double SlideDistance(bool horizontal) =>
        Math.Min(300, Math.Max(80, (horizontal ? ActualWidth : ActualHeight) * 0.6));

    private void ApplyOpacity()
    {
        var opacity = _layer.Opacity * _transitionFactor;
        if (_style is not null)
        {
            _style.ApplyOpacity(opacity);
        }
        else
        {
            Stage.Opacity = opacity;
        }
    }

    // ----- Window behaviour -----

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        if (_style is not null)
        {
            // Logos, GIFs and videos: never a Windows outline or rounded corners, only the media itself.
            _style.Attach(systemFrame: false);
        }
        else
        {
            var corner = 1; // DWMWCP_DONOTROUND: OBS should capture square corners
            NativeMethods.DwmSetWindowAttribute(Handle, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
        }

        ApplyOpacity();
    }

    private void OnLayerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MediaLayerViewModel.Opacity):
                ApplyOpacity();
                break;
            case nameof(MediaLayerViewModel.ClickThrough):
                _style?.ApplyExtendedStyle();
                break;
            case nameof(MediaLayerViewModel.Locked):
                ApplyLock();
                break;

            case nameof(MediaLayerViewModel.OutputBackground):
                ApplyOutputBackground();
                break;
            case nameof(MediaLayerViewModel.ShowBorder):
                ApplyBorder();
                ApplyLock();
                break;
        }
    }

    /// <summary>Hover border and title bar: never on OBS outputs, optional on overlays.</summary>
    private void ApplyBorder() =>
        HoverChrome.Visibility = !IsOutput && _layer.ShowBorder ? Visibility.Visible : Visibility.Collapsed;

    private void ApplyOutputBackground()
    {
        if (IsOutput)
        {
            Background = _layer.OutputBackground == "green"
                ? new SolidColorBrush(Color.FromRgb(0, 255, 0))
                : Brushes.Black;
        }
    }

    /// <summary>
    /// Resizable from the edges only when unlocked and "Border" is on: a resizable window gets a thin outline from
    /// Windows 11, so without the border only the bare image is visible (use the size buttons to resize it).
    /// </summary>
    private void ApplyLock()
    {
        var resizable = !_layer.Locked && (_layer.ShowBorder || IsOutput);
        ResizeMode = resizable ? ResizeMode.CanResize : ResizeMode.NoResize;
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 0,
            ResizeBorderThickness = new Thickness(resizable ? 6 : 0),
            GlassFrameThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            UseAeroCaptionButtons = false,
        });
    }

    private void TrackBounds()
    {
        if (!_animating && IsVisible && WindowState == WindowState.Normal && ActualWidth > 0)
        {
            _restPosition = new Point(Left, Top);
            _placedByUser = true;
            _layer.ReportBounds(new Rect(Left, Top, ActualWidth, ActualHeight));
        }
    }

    private void OnRootMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_layer.Locked)
        {
            return;
        }

        if (e.ClickCount == 2)
        {
            FitToContent();
            return;
        }

        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void OnFitRequested(object? sender, EventArgs e) => FitToContent();

    /// <summary>Scales the window around its top-left corner (used when edges are not draggable).</summary>
    private void OnScaleRequested(object? sender, double factor)
    {
        var width = ActualWidth > 0 ? ActualWidth : Width;
        var height = ActualHeight > 0 ? ActualHeight : Height;
        Width = Math.Clamp(width * factor, MinWidth, SystemParameters.VirtualScreenWidth);
        Height = Math.Clamp(height * factor, MinHeight, SystemParameters.VirtualScreenHeight);
    }

    /// <summary>Matches the window's height to the content aspect (no empty bands).</summary>
    private void FitToContent()
    {
        if (_layer.ContentAspect is not { } aspect || aspect <= 0)
        {
            return;
        }

        var width = ActualWidth > 0 ? ActualWidth : Width;
        Height = Math.Clamp(width / aspect, MinHeight, SystemParameters.VirtualScreenHeight);
    }

    private void OnPlacementRequested(object? sender, LayerPlacement placement)
    {
        const double margin = 24;
        var area = MonitorWorkArea();
        var width = ActualWidth > 0 ? ActualWidth : Width;
        var height = ActualHeight > 0 ? ActualHeight : Height;
        Left = placement switch
        {
            LayerPlacement.TopLeft or LayerPlacement.BottomLeft => area.Left + margin,
            LayerPlacement.TopRight or LayerPlacement.BottomRight => area.Right - width - margin,
            _ => area.Left + (area.Width - width) / 2,
        };
        Top = placement switch
        {
            LayerPlacement.TopLeft or LayerPlacement.TopRight => area.Top + margin,
            LayerPlacement.BottomLeft or LayerPlacement.BottomRight => area.Bottom - height - margin,
            _ => area.Top + (area.Height - height) / 2,
        };
    }

    /// <summary>Work area (DIPs) of the monitor this window is on.</summary>
    private Rect MonitorWorkArea()
    {
        var handle = Handle;
        var monitor = NativeMethods.MonitorFromWindow(handle, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var info = new NativeMethods.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        if (handle == IntPtr.Zero || !NativeMethods.GetMonitorInfo(monitor, ref info))
        {
            return SystemParameters.WorkArea;
        }

        var dpi = VisualTreeHelper.GetDpi(this);
        var work = info.rcWork;
        return new Rect(work.Left / dpi.DpiScaleX, work.Top / dpi.DpiScaleY, work.Width / dpi.DpiScaleX, work.Height / dpi.DpiScaleY);
    }

    private void OnHideClick(object sender, RoutedEventArgs e) => _layer.IsShown = false;

    private void ShowMessage(string text)
    {
        Message.Text = text;
        Message.Visibility = Visibility.Visible;
        if (AllowsTransparency)
        {
            Root.Background = new SolidColorBrush(Color.FromArgb(0xE0, 0x0A, 0x0D, 0x14));
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        StopTween();
        Media.Unload();
        _layer.PropertyChanged -= OnLayerPropertyChanged;
        _layer.PlaybackRequested -= OnPlaybackRequested;
        _layer.PlacementRequested -= OnPlacementRequested;
        _layer.FitRequested -= OnFitRequested;
        _layer.ScaleRequested -= OnScaleRequested;
    }
}
