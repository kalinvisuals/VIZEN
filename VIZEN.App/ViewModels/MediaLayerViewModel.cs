using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using VIZEN.App.Infrastructure;
using VIZEN.App.Localization;
using VIZEN.App.Media;
using VIZEN.App.Models;

namespace VIZEN.App.ViewModels;

public enum PlaybackAction
{
    Play,
    Pause,
    Stop,
    Restart,
}

/// <summary>Quick placement on the work area of the layer's monitor.</summary>
public enum LayerPlacement
{
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight,
    Center,
}

/// <summary>
/// One media layer (logo, GIF or video): its saved settings (<see cref="MediaItem"/>) plus runtime state
/// reported by its window. Shown/hidden through <see cref="IsShown"/> (the library opens the window).
/// </summary>
public sealed class MediaLayerViewModel : ObservableObject
{
    private readonly Action _saveRequested;
    private bool _isShown;
    private string? _error;
    private bool _isPlaying;
    private int _naturalWidth;
    private int _naturalHeight;
    private TimeSpan? _duration;
    private ImageSource? _thumbnail;
    private bool _thumbnailLoaded;
    private bool _isExpanded;
    private bool _isConfirmingRemove;

    public MediaLayerViewModel(MediaItem item, Action saveRequested)
    {
        Item = item;
        _saveRequested = saveRequested;

        PlayCommand = new RelayCommand(() => RequestPlayback(PlaybackAction.Play), () => IsAnimated && IsShown);
        PauseCommand = new RelayCommand(() => RequestPlayback(PlaybackAction.Pause), () => IsVideo && IsShown);
        StopCommand = new RelayCommand(() => RequestPlayback(PlaybackAction.Stop), () => IsAnimated && IsShown);
        RestartCommand = new RelayCommand(() => RequestPlayback(PlaybackAction.Restart), () => IsAnimated && IsShown);
        RotateLeftCommand = new RelayCommand(() => Rotation = NormalizeAngle(Rotation - 90));
        RotateRightCommand = new RelayCommand(() => Rotation = NormalizeAngle(Rotation + 90));
        ResetCropCommand = new RelayCommand(() => Crop = RegionMath.Full, () => !RegionMath.IsFull(Crop));
        PlaceCommand = new RelayCommand<string>(p =>
        {
            if (Enum.TryParse<LayerPlacement>(p, out var placement))
            {
                PlacementRequested?.Invoke(this, placement);
            }
        });
        FitCommand = new RelayCommand(() => FitRequested?.Invoke(this, EventArgs.Empty), () => IsShown);
        SmallerCommand = new RelayCommand(() => ScaleRequested?.Invoke(this, 1 / 1.1), () => IsShown);
        LargerCommand = new RelayCommand(() => ScaleRequested?.Invoke(this, 1.1), () => IsShown);
        ToggleExpandedCommand = new RelayCommand(() => IsExpanded = !IsExpanded);
        NextTransitionInCommand = new RelayCommand(() => TransitionIn = Next(TransitionIn));
        NextTransitionOutCommand = new RelayCommand(() => TransitionOut = Next(TransitionOut));
        RemoveCommand = new RelayCommand(() => IsConfirmingRemove = true);
        CancelRemoveCommand = new RelayCommand(() => IsConfirmingRemove = false);
    }

    /// <summary>The persisted settings; edit through this view model so changes are saved.</summary>
    public MediaItem Item { get; }

    public Guid Id => Item.Id;
    public MediaKind Kind => Item.Kind;
    public bool IsVideo => Kind == MediaKind.Video;
    public bool IsGif => Kind == MediaKind.Gif;
    public bool IsAnimated => Kind != MediaKind.Image;
    public string FilePath => Item.FilePath;
    public string FileName => Path.GetFileName(Item.FilePath);
    public bool FileExists => File.Exists(Item.FilePath);

    public string KindText => Loc.T(Kind switch
    {
        MediaKind.Gif => "Media.KindGif",
        MediaKind.Video => "Media.KindVideo",
        _ => "Media.KindImage",
    });

    public string KindGlyph => Kind switch
    {
        MediaKind.Gif => "",
        MediaKind.Video => "",
        _ => "",
    };

    // ----- Saved settings -----

    public string Name
    {
        get => Item.Name;
        set => Set(Item.Name, string.IsNullOrWhiteSpace(value) ? Path.GetFileNameWithoutExtension(FilePath) : value.Trim(), v => Item.Name = v);
    }

    public string Group
    {
        get => Item.Group ?? string.Empty;
        set => Set(Item.Group, string.IsNullOrWhiteSpace(value) ? null : value.Trim(), v => Item.Group = v);
    }

    public double Opacity
    {
        get => Item.Opacity;
        set
        {
            if (Set(Item.Opacity, Math.Clamp(value, 0.05, 1), v => Item.Opacity = v))
            {
                OnPropertyChanged(nameof(OpacityText));
            }
        }
    }

    public string OpacityText => $"{Math.Round(Opacity * 100)}%";

    public double Rotation
    {
        get => Item.Rotation;
        set
        {
            if (Set(Item.Rotation, NormalizeAngle(Math.Round(value)), v => Item.Rotation = v))
            {
                OnPropertyChanged(nameof(RotationText));
            }
        }
    }

    public string RotationText => $"{Rotation:0}°";

    public bool Locked
    {
        get => Item.Locked;
        set => Set(Item.Locked, value, v => Item.Locked = v);
    }

    public bool ClickThrough
    {
        get => Item.ClickThrough;
        set => Set(Item.ClickThrough, value, v => Item.ClickThrough = v);
    }

    /// <summary>Hover border and title bar on the overlay; off = only the image is ever visible.</summary>
    public bool ShowBorder
    {
        get => Item.ShowBorder;
        set => Set(Item.ShowBorder, value, v => Item.ShowBorder = v);
    }

    /// <summary>True = clean OBS output window; false = always-on-top overlay.</summary>
    public bool IsOutput
    {
        get => Item.Target == MediaTarget.Output;
        set => SetTarget(value, MediaTarget.Output);
    }

    public bool IsOverlay
    {
        get => Item.Target == MediaTarget.Overlay;
        set => SetTarget(value, MediaTarget.Overlay);
    }

    /// <summary>Shown only inside compositions (no window of its own).</summary>
    public bool IsCompositionOnly
    {
        get => Item.Target == MediaTarget.Composition;
        set => SetTarget(value, MediaTarget.Composition);
    }

    public MediaTarget Target => Item.Target;

    private void SetTarget(bool selected, MediaTarget target)
    {
        if (!selected || !Set(Item.Target, target, v => Item.Target = v, nameof(Target)))
        {
            return;
        }

        OnPropertyChanged(nameof(IsOutput));
        OnPropertyChanged(nameof(IsOverlay));
        OnPropertyChanged(nameof(IsCompositionOnly));
        OnPropertyChanged(nameof(WindowTitle));
    }

    public string OutputBackground
    {
        get => Item.OutputBackground;
        set => Set(Item.OutputBackground, value, v => Item.OutputBackground = v);
    }

    public bool IsGreenBackground
    {
        get => OutputBackground == "green";
        set
        {
            if (value)
            {
                OutputBackground = "green";
                OnPropertyChanged(nameof(IsBlackBackground));
            }
        }
    }

    public bool IsBlackBackground
    {
        get => OutputBackground == "black";
        set
        {
            if (value)
            {
                OutputBackground = "black";
                OnPropertyChanged(nameof(IsGreenBackground));
            }
        }
    }

    public int Layer
    {
        get => Item.Layer;
        set => Set(Item.Layer, value, v => Item.Layer = v);
    }

    public string LayerText => Loc.F("Media.LayerText", Layer);

    public bool Loop
    {
        get => Item.Loop;
        set => Set(Item.Loop, value, v => Item.Loop = v);
    }

    public bool Muted
    {
        get => Item.Muted;
        set => Set(Item.Muted, value, v => Item.Muted = v);
    }

    public double Volume
    {
        get => Item.Volume;
        set
        {
            if (Set(Item.Volume, Math.Clamp(value, 0, 1), v => Item.Volume = v))
            {
                OnPropertyChanged(nameof(VolumeText));
            }
        }
    }

    public string VolumeText => $"{Math.Round(Volume * 100)}%";

    public Rect Crop
    {
        get => Item.Crop;
        set
        {
            var clamped = RegionMath.WithMinimumSize(RegionMath.ClampInside(value), 0.02, 0.02);
            if (clamped == Item.Crop)
            {
                return;
            }

            Item.Crop = clamped;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CropLeft));
            OnPropertyChanged(nameof(CropTop));
            OnPropertyChanged(nameof(CropRight));
            OnPropertyChanged(nameof(CropBottom));
            OnPropertyChanged(nameof(CropText));
            OnPropertyChanged(nameof(NaturalSizeText));
            _saveRequested();
        }
    }

    // Crop edges as percentages trimmed from each side (0..95), for sliders.
    public double CropLeft
    {
        get => Math.Round(Crop.X * 100, 1);
        set => Crop = FromEdges(value, CropTop, CropRight, CropBottom);
    }

    public double CropTop
    {
        get => Math.Round(Crop.Y * 100, 1);
        set => Crop = FromEdges(CropLeft, value, CropRight, CropBottom);
    }

    public double CropRight
    {
        get => Math.Round((1 - Crop.Right) * 100, 1);
        set => Crop = FromEdges(CropLeft, CropTop, value, CropBottom);
    }

    public double CropBottom
    {
        get => Math.Round((1 - Crop.Bottom) * 100, 1);
        set => Crop = FromEdges(CropLeft, CropTop, CropRight, value);
    }

    public string CropText => RegionMath.IsFull(Crop)
        ? Loc.T("Media.CropNone")
        : Loc.F("Media.CropEdges", CropLeft, CropTop, CropRight, CropBottom);

    public MediaTransition TransitionIn
    {
        get => Item.TransitionIn;
        set => Set(Item.TransitionIn, value, v => Item.TransitionIn = v);
    }

    public MediaTransition TransitionOut
    {
        get => Item.TransitionOut;
        set => Set(Item.TransitionOut, value, v => Item.TransitionOut = v);
    }

    public double TransitionSeconds
    {
        get => Item.TransitionSeconds;
        set => Set(Item.TransitionSeconds, Math.Clamp(Math.Round(value, 1), 0, 5), v => Item.TransitionSeconds = v);
    }

    public static IReadOnlyList<MediaTransition> Transitions { get; } = Enum.GetValues<MediaTransition>();

    public string TransitionInText => Loc.F("Media.TransitionIn", Loc.T("Media.Transition." + TransitionIn));

    public string TransitionOutText => Loc.F("Media.TransitionOut", Loc.T("Media.Transition." + TransitionOut));

    public string TransitionSecondsText => Loc.F("Media.Seconds", TransitionSeconds.ToString("0.0"));

    private static MediaTransition Next(MediaTransition t) => Transitions[(Array.IndexOf(Transitions.ToArray(), t) + 1) % Transitions.Count];

    // ----- Runtime state -----

    /// <summary>Whether the layer's window is on screen. Setting it shows/hides with the layer's transitions.</summary>
    public bool IsShown
    {
        get => _isShown;
        set
        {
            if (SetProperty(ref _isShown, value))
            {
                OnPropertyChanged(nameof(StatusText));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public string? Error
    {
        get => _error;
        private set
        {
            if (SetProperty(ref _error, value))
            {
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => Error is not null;

    public bool IsPlaying
    {
        get => _isPlaying;
        private set
        {
            if (SetProperty(ref _isPlaying, value))
            {
                OnPropertyChanged(nameof(StatusText));
            }
        }
    }

    public string StatusText => Error ?? (!IsShown
        ? Loc.T("Media.StatusHidden")
        : IsAnimated
            ? Loc.T(IsPlaying ? "Media.StatusPlaying" : "Media.StatusPaused")
            : Loc.T("Media.StatusShown"));

    public int NaturalWidth => _naturalWidth;
    public int NaturalHeight => _naturalHeight;

    /// <summary>Pixel aspect (w/h) of what is shown (after crop and rotation), or null before the file is opened.</summary>
    public double? ContentAspect
    {
        get
        {
            if (_naturalWidth <= 0 || _naturalHeight <= 0)
            {
                return null;
            }

            // Bounding box of the rotated, cropped content (the window shows it uniformly scaled).
            var w = _naturalWidth * Crop.Width;
            var h = _naturalHeight * Crop.Height;
            var radians = Rotation * Math.PI / 180;
            var (cos, sin) = (Math.Abs(Math.Cos(radians)), Math.Abs(Math.Sin(radians)));
            return (w * cos + h * sin) / (w * sin + h * cos);
        }
    }

    public string NaturalSizeText => _naturalWidth > 0
        ? Loc.F("Editor.PixelSize", (int)Math.Round(_naturalWidth * Crop.Width), (int)Math.Round(_naturalHeight * Crop.Height))
        : string.Empty;

    public string DurationText => _duration is { } d ? Loc.F("Media.Duration", d.ToString(d.TotalHours >= 1 ? @"h\:mm\:ss" : @"m\:ss")) : string.Empty;

    public TimeSpan? Duration => _duration;

    /// <summary>Small preview for the library card (images and GIFs; videos show their icon).</summary>
    public ImageSource? Thumbnail
    {
        get
        {
            if (!_thumbnailLoaded && !IsVideo)
            {
                _thumbnailLoaded = true;
                try
                {
                    _thumbnail = MediaFormats.LoadImage(FilePath, 160);
                }
                catch (MediaLoadException ex)
                {
                    Error = ex.Message;
                }
            }

            return _thumbnail;
        }
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    public bool IsConfirmingRemove
    {
        get => _isConfirmingRemove;
        set => SetProperty(ref _isConfirmingRemove, value);
    }

    /// <summary>Window title (OBS finds output windows by title).</summary>
    public string WindowTitle => IsOutput ? $"VIZEN Output · Media · {Name}" : $"VIZEN layer · {Name}";

    public ICommand PlayCommand { get; }
    public ICommand PauseCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand RestartCommand { get; }
    public ICommand RotateLeftCommand { get; }
    public ICommand RotateRightCommand { get; }
    public ICommand ResetCropCommand { get; }
    public ICommand PlaceCommand { get; }
    public ICommand FitCommand { get; }
    public ICommand SmallerCommand { get; }
    public ICommand LargerCommand { get; }
    public ICommand ToggleExpandedCommand { get; }
    public ICommand NextTransitionInCommand { get; }
    public ICommand NextTransitionOutCommand { get; }
    public ICommand RemoveCommand { get; }
    public ICommand CancelRemoveCommand { get; }

    // Set by the library (they act on the collection).
    public ICommand? ConfirmRemoveCommand { get; internal set; }
    public ICommand? DuplicateCommand { get; internal set; }
    public ICommand? BringForwardCommand { get; internal set; }
    public ICommand? SendBackwardCommand { get; internal set; }

    public event EventHandler<PlaybackAction>? PlaybackRequested;
    public event EventHandler<LayerPlacement>? PlacementRequested;
    public event EventHandler? FitRequested;

    /// <summary>Resize by a factor (from the size buttons; the edges are not draggable without a border).</summary>
    public event EventHandler<double>? ScaleRequested;

    /// <summary>A non-looping GIF or video reached its end.</summary>
    public event EventHandler? PlaybackEnded;

    // ----- Reports from the layer window -----

    internal void ReportOpened(int width, int height, TimeSpan? duration)
    {
        _naturalWidth = width;
        _naturalHeight = height;
        _duration = duration;
        Error = null;
        OnPropertyChanged(nameof(NaturalSizeText));
        OnPropertyChanged(nameof(DurationText));
        OnPropertyChanged(nameof(ContentAspect));
    }

    internal void ReportError(string message)
    {
        Error = message;
        IsPlaying = false;
    }

    internal void ClearError() => Error = null;

    internal void ReportPlaying(bool playing) => IsPlaying = playing;

    internal void ReportEnded()
    {
        IsPlaying = false;
        PlaybackEnded?.Invoke(this, EventArgs.Empty);
    }

    internal void ReportBounds(Rect bounds)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        if (Item.Left != bounds.Left || Item.Top != bounds.Top || Item.Width != bounds.Width || Item.Height != bounds.Height)
        {
            (Item.Left, Item.Top, Item.Width, Item.Height) = (bounds.Left, bounds.Top, bounds.Width, bounds.Height);
            _saveRequested();
        }
    }

    internal void RequestPlayback(PlaybackAction action) => PlaybackRequested?.Invoke(this, action);

    private static Rect FromEdges(double left, double top, double right, double bottom)
    {
        static double Edge(double percent) => Math.Clamp(percent, 0, 95) / 100;
        var x = Edge(left);
        var y = Edge(top);
        var w = Math.Max(0.02, 1 - x - Edge(right));
        var h = Math.Max(0.02, 1 - y - Edge(bottom));
        return new Rect(x, y, w, h);
    }

    private static double NormalizeAngle(double degrees)
    {
        var a = degrees % 360;
        return a > 180 ? a - 360 : a <= -180 ? a + 360 : a;
    }

    private bool Set<T>(T current, T value, Action<T> assign, [System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(current, value))
        {
            return false;
        }

        assign(value);
        OnPropertyChanged(propertyName);
        if (propertyName is nameof(Name))
        {
            OnPropertyChanged(nameof(WindowTitle));
        }

        if (propertyName is nameof(Rotation))
        {
            OnPropertyChanged(nameof(ContentAspect));
        }

        if (propertyName is nameof(Layer))
        {
            OnPropertyChanged(nameof(LayerText));
        }

        if (propertyName is nameof(TransitionIn) or nameof(TransitionOut) or nameof(TransitionSeconds))
        {
            OnPropertyChanged(nameof(TransitionInText));
            OnPropertyChanged(nameof(TransitionOutText));
            OnPropertyChanged(nameof(TransitionSecondsText));
        }

        _saveRequested();
        return true;
    }
}
