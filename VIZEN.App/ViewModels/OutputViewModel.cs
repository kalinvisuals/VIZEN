using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using VIZEN.App.Capture;
using VIZEN.App.Infrastructure;
using VIZEN.App.Localization;
using VIZEN.App.Models;

namespace VIZEN.App.ViewModels;

public enum OutputBackground
{
    Black,
    Green,
}

/// <summary>A resize request in physical pixels; the window fills in the size it actually achieved.</summary>
public sealed class OutputSizeRequest(Size requested)
{
    public Size Requested { get; } = requested;

    /// <summary>Client size actually applied (may be smaller when limited by the screen). Null if not handled.</summary>
    public Size? Applied { get; set; }
}

public enum OutputFit
{
    /// <summary>Whole content visible, letterboxed.</summary>
    Fit,

    /// <summary>Window filled, content cropped at the edges.</summary>
    Fill,

    /// <summary>Window filled, content distorted.</summary>
    Stretch,
}

/// <summary>
/// A clean output window for OBS "Window Capture": only the (cropped) source content, no borders or UI,
/// with a stable title so OBS finds it again next session. Shares the source's capture with everything else.
/// </summary>
public sealed class OutputViewModel : ObservableObject
{
    private static readonly Brush GreenScreen = Freeze(new SolidColorBrush(Color.FromRgb(0, 255, 0)));

    private CaptureLease _lease;
    private OutputBackground _background;
    private OutputFit _fit;
    private int _pixelWidth;
    private int _pixelHeight;

    public OutputViewModel(WindowSource source, CaptureLease lease, Rect region, string? regionName, string windowTitle)
    {
        Source = source;
        _lease = lease;
        Viewbox = RegionMath.IsFull(region) ? RegionMath.Full : RegionMath.ClampInside(region);
        RegionName = RegionMath.IsFull(Viewbox) ? null : regionName;
        WindowTitle = windowTitle;

        CloseCommand = new RelayCommand(Close);
        BringToFrontCommand = new RelayCommand(() => BringToFrontRequested?.Invoke(this, EventArgs.Empty));
        SizePresetCommand = new RelayCommand<string>(ApplySizePreset);
    }

    public WindowSource Source { get; private set; }

    public LiveCapture Capture => _lease.Capture;

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

    /// <summary>Continues on a reopened source window (same app and title) without closing this output.</summary>
    internal void Reconnect(WindowSource source, CaptureLease lease)
    {
        var old = _lease;
        Source = source;
        _lease = lease;
        old.Dispose();
        OnPropertyChanged(nameof(Source));
        OnPropertyChanged(nameof(Capture));
        OnPropertyChanged(nameof(ContentPixelSize));
    }

    public Rect Viewbox { get; }

    public string? RegionName { get; }

    /// <summary>Exact window title, as listed by OBS. Stable for the same app + region name.</summary>
    public string WindowTitle { get; }

    public string DisplayName => RegionName is null ? Source.ProcessName : $"{RegionName} · {Source.ProcessName}";

    public OutputBackground Background
    {
        get => _background;
        set
        {
            if (SetProperty(ref _background, value))
            {
                OnPropertyChanged(nameof(BackgroundBrush));
                OnPropertyChanged(nameof(IsBlackBackground));
                OnPropertyChanged(nameof(IsGreenBackground));
            }
        }
    }

    public Brush BackgroundBrush => Background == OutputBackground.Green ? GreenScreen : Brushes.Black;

    public bool IsBlackBackground
    {
        get => Background == OutputBackground.Black;
        set
        {
            if (value)
            {
                Background = OutputBackground.Black;
            }
        }
    }

    public bool IsGreenBackground
    {
        get => Background == OutputBackground.Green;
        set
        {
            if (value)
            {
                Background = OutputBackground.Green;
            }
        }
    }

    public OutputFit Fit
    {
        get => _fit;
        set
        {
            if (SetProperty(ref _fit, value))
            {
                OnPropertyChanged(nameof(Stretch));
                OnPropertyChanged(nameof(IsFit));
                OnPropertyChanged(nameof(IsFill));
                OnPropertyChanged(nameof(IsStretch));
            }
        }
    }

    /// <summary>ImageBrush stretch for the current <see cref="Fit"/>.</summary>
    public Stretch Stretch => Fit switch
    {
        OutputFit.Fill => Stretch.UniformToFill,
        OutputFit.Stretch => Stretch.Fill,
        _ => Stretch.Uniform,
    };

    public bool IsFit
    {
        get => Fit == OutputFit.Fit;
        set
        {
            if (value)
            {
                Fit = OutputFit.Fit;
            }
        }
    }

    public bool IsFill
    {
        get => Fit == OutputFit.Fill;
        set
        {
            if (value)
            {
                Fit = OutputFit.Fill;
            }
        }
    }

    public bool IsStretch
    {
        get => Fit == OutputFit.Stretch;
        set
        {
            if (value)
            {
                Fit = OutputFit.Stretch;
            }
        }
    }

    /// <summary>Content size in source pixels (frame × region), or null before the first frame.</summary>
    public Size? ContentPixelSize
    {
        get
        {
            return Capture.FrameWidth <= 0
                ? null
                : new Size(Math.Max(1, Capture.FrameWidth * Viewbox.Width), Math.Max(1, Capture.FrameHeight * Viewbox.Height));
        }
    }

    /// <summary>Current client size of the output window in physical pixels (what OBS captures).</summary>
    public string SizeText => _pixelWidth > 0 ? Loc.F("Editor.PixelSize", _pixelWidth, _pixelHeight) : "—";

    /// <summary>Current on-screen bounds in DIPs, kept up to date by the window.</summary>
    public Rect Bounds { get; set; } = Rect.Empty;

    /// <summary>Where to open the window (e.g. when restoring a profile). Null = default placement.</summary>
    public Rect? InitialBounds { get; init; }

    public bool IsClosed { get; private set; }

    public ICommand CloseCommand { get; }

    public ICommand BringToFrontCommand { get; }

    /// <summary>Parameter: "1:1", "50", "720p" or "1080p".</summary>
    public ICommand SizePresetCommand { get; }

    /// <summary>Asks the window to resize its client area to this many physical pixels.</summary>
    public event EventHandler<OutputSizeRequest>? SizeRequested;

    /// <summary>Raised after the window handled a size request; <see cref="OutputSizeRequest.Applied"/> is the real result.</summary>
    public event EventHandler<OutputSizeRequest>? Resized;

    public event EventHandler? BringToFrontRequested;

    public event EventHandler? Closed;

    internal void ReportPixelSize(int width, int height)
    {
        if (_pixelWidth != width || _pixelHeight != height)
        {
            _pixelWidth = width;
            _pixelHeight = height;
            OnPropertyChanged(nameof(SizeText));
        }
    }

    private void ApplySizePreset(string preset)
    {
        Size? target = preset switch
        {
            "1:1" => ContentPixelSize,
            "50" => ContentPixelSize is { } s ? new Size(s.Width / 2, s.Height / 2) : null,
            "720p" => new Size(1280, 720),
            "1080p" => new Size(1920, 1080),
            _ => null,
        };

        if (target is { } size)
        {
            var request = new OutputSizeRequest(new Size(Math.Round(size.Width), Math.Round(size.Height)));
            SizeRequested?.Invoke(this, request);
            Resized?.Invoke(this, request);
        }
    }

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

    private static Brush Freeze(Brush brush)
    {
        brush.Freeze();
        return brush;
    }
}
