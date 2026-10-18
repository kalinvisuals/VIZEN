using System.Windows;
using System.Windows.Input;
using VIZEN.App.Capture;
using VIZEN.App.Infrastructure;
using VIZEN.App.Models;

namespace VIZEN.App.ViewModels;

/// <summary>
/// One live overlay: a source window — or a cropped region of it — shown in its own always-on-top window.
/// The capture itself is shared with every other consumer of the same window.
/// </summary>
public sealed class OverlayViewModel : ObservableObject
{
    public const double MinOpacity = 0.15;

    private CaptureLease _lease;
    private double _opacity = 1.0;
    private bool _isClickThrough;
    private bool _isVisible = true;
    private bool _showPerformance;

    public OverlayViewModel(WindowSource source, CaptureLease lease, Rect region, string? regionName)
    {
        Source = source;
        _lease = lease;
        Viewbox = RegionMath.IsFull(region) ? RegionMath.Full : RegionMath.ClampInside(region);
        RegionName = RegionMath.IsFull(Viewbox) ? null : regionName;
        CloseCommand = new RelayCommand(Close);
    }

    public WindowSource Source { get; private set; }

    public LiveCapture Capture => _lease.Capture;

    /// <summary>The capture lease (the overlay window reports whether it presents natively).</summary>
    internal CaptureLease Lease => _lease;

    private AppAudioViewModel? _audio;

    /// <summary>Sound controls for the source's app (null for cameras).</summary>
    public AppAudioViewModel? Audio
    {
        get => _audio;
        set
        {
            _audio?.Detach();
            SetProperty(ref _audio, value);
        }
    }

    /// <summary>
    /// The source window was closed and a matching one reappeared (e.g. a restarted call): continue on the new
    /// window without closing this overlay.
    /// </summary>
    internal void Reconnect(WindowSource source, CaptureLease lease)
    {
        var old = _lease;
        Source = source;
        _lease = lease;
        old.Dispose();
        OnPropertyChanged(nameof(Source));
        OnPropertyChanged(nameof(Capture));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(ProcessAndCategory));
    }

    /// <summary>Normalized part of the source shown by this overlay ((0,0,1,1) = whole window).</summary>
    public Rect Viewbox { get; }

    public bool IsCropped => !RegionMath.IsFull(Viewbox);

    /// <summary>Name of the crop region, or null for the whole window.</summary>
    public string? RegionName { get; }

    public string Title => RegionName is null ? Source.Title : $"{RegionName} · {Source.Title}";

    /// <summary>Pixel aspect ratio (w/h) of what this overlay shows, or null before the first frame.</summary>
    public double? ContentAspect
    {
        get
        {
            if (Capture.FrameWidth == 0 || Capture.FrameHeight == 0)
            {
                return null;
            }

            return Capture.FrameWidth * Viewbox.Width / (Capture.FrameHeight * Viewbox.Height);
        }
    }

    /// <summary>Whole-window opacity, 0.15–1.</summary>
    public double Opacity
    {
        get => _opacity;
        set
        {
            if (SetProperty(ref _opacity, Math.Clamp(value, MinOpacity, 1.0)))
            {
                OnPropertyChanged(nameof(OpacityText));
            }
        }
    }

    public string OpacityText => $"{Math.Round(Opacity * 100)}%";

    public string ProcessAndCategory => $"{Source.ProcessName}  ·  {Source.CategoryText}";

    /// <summary>When true the overlay ignores the mouse, so clicks reach the window underneath.</summary>
    public bool IsClickThrough
    {
        get => _isClickThrough;
        set => SetProperty(ref _isClickThrough, value);
    }

    public bool IsVisible
    {
        get => _isVisible;
        set => SetProperty(ref _isVisible, value);
    }

    /// <summary>Shows the measured state (LIVE / NO NEW FRAMES / SOURCE LIMITED, fps, drops, latency) on the overlay itself.</summary>
    public bool ShowPerformance
    {
        get => _showPerformance;
        set => SetProperty(ref _showPerformance, value);
    }

    /// <summary>Current on-screen bounds in DIPs, kept up to date by the overlay window.</summary>
    public Rect Bounds { get; set; } = Rect.Empty;

    /// <summary>Where to open the window (e.g. when restoring a profile). Null = default placement.</summary>
    public Rect? InitialBounds { get; init; }

    public bool IsClosed { get; private set; }

    public ICommand CloseCommand { get; }

    /// <summary>Raised once when the overlay is closed, from either the window or the manager.</summary>
    public event EventHandler? Closed;

    public void Close()
    {
        if (IsClosed)
        {
            return;
        }

        IsClosed = true;
        _audio?.Detach();
        _lease.Dispose();
        Closed?.Invoke(this, EventArgs.Empty);
    }
}
