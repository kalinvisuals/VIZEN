using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using VIZEN.App.Capture;
using VIZEN.App.Infrastructure;
using VIZEN.App.Localization;
using VIZEN.App.Models;
using VIZEN.App.Services;

namespace VIZEN.App.ViewModels;

/// <summary>Gaming mode: live always-on-top overlays of window sources.</summary>
public sealed class OverlaysViewModel : ObservableObject
{
    private readonly CaptureService _capture;
    private readonly IOverlayPresenter _presenter;
    private readonly ActivityLog _activity;
    private string? _errorMessage;
    private bool _bulkChange;

    public OverlaysViewModel(
        WindowSourcesViewModel sources,
        CaptureService capture,
        IOverlayPresenter presenter,
        ActivityLog activity,
        Action<ObservableObject> navigate)
    {
        Sources = sources;
        _capture = capture;
        _presenter = presenter;
        _activity = activity;

        BrowseSourcesCommand = new RelayCommand(() => navigate(sources));
        CloseAllCommand = new RelayCommand(CloseAll, () => Overlays.Count > 0);
        Overlays.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(Count));
            OnPropertyChanged(nameof(HasOverlays));
            OnPropertyChanged(nameof(SummaryText));
        };
    }

    /// <summary>
    /// E.g. "3 active · 1 capture": overlays cropped from the same window share one capture.
    /// </summary>
    public string SummaryText => Loc.F("Gaming.Summary", Overlays.Count, Overlays.Select(o => o.Capture).Distinct().Count());

    public WindowSourcesViewModel Sources { get; }

    public ObservableCollection<OverlayViewModel> Overlays { get; } = [];

    public int Count => Overlays.Count;

    public bool HasOverlays => Overlays.Count > 0;

    public bool IsCaptureSupported => _capture.IsSupported;

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set => SetProperty(ref _errorMessage, value);
    }

    public ICommand BrowseSourcesCommand { get; }

    public ICommand CloseAllCommand { get; }

    /// <summary>Set by the shell: sound controls for a source's app.</summary>
    public Func<WindowSource, AppAudioViewModel?>? AudioFor { get; set; }

    /// <summary>
    /// Creates a live overlay for <paramref name="source"/> — the whole window, or the normalized
    /// <paramref name="region"/> of it — optionally restoring a saved <paramref name="layout"/>
    /// (whose own region is used when <paramref name="region"/> is null). Returns false if capture could not start.
    /// </summary>
    public bool CreateOverlay(WindowSource source, OverlayLayout? layout = null, Rect? region = null, string? regionName = null)
    {
        ErrorMessage = null;
        if (!_capture.CanCapture(source))
        {
            ErrorMessage = Loc.T("Capture.Unsupported");
            return false;
        }

        CaptureLease lease;
        try
        {
            lease = _capture.Acquire(source, presentsNatively: true);
        }
        catch (Exception ex)
        {
            ErrorMessage = Loc.F("Capture.StartFailed", ex.Message);
            _activity.Add("", "Activity.OverlayFailed", ActivityLog.RawDetail, ErrorMessage);
            return false;
        }

        if (lease.Capture.Status == CaptureStatus.Failed)
        {
            ErrorMessage = $"{source.Title}: {lease.Capture.StatusText}";
            _activity.Add("", "Activity.OverlayFailed", ActivityLog.RawDetail, ErrorMessage);
            lease.Dispose();
            return false;
        }

        var overlay = new OverlayViewModel(
            source, lease, region ?? layout?.Region ?? RegionMath.Full, regionName ?? layout?.RegionName)
        {
            InitialBounds = layout is { Width: > 0, Height: > 0 }
                ? new Rect(layout.Left, layout.Top, layout.Width, layout.Height)
                : null,
        };
        if (layout is not null)
        {
            overlay.Opacity = layout.Opacity;
            overlay.IsClickThrough = layout.ClickThrough;
        }

        overlay.Audio = AudioFor?.Invoke(source);
        var title = overlay.Title;
        overlay.Closed += (_, _) =>
        {
            Overlays.Remove(overlay);
            _activity.Add("", "Activity.OverlayClosed", "Activity.OverlayClosedDetail", title);
        };
        EventHandler? onSourceClosed = null;
        onSourceClosed = (_, _) => _activity.Add("", "Activity.OverlaySourceClosed", ActivityLog.RawDetail, title);
        overlay.Capture.SourceClosed += onSourceClosed;
        overlay.Closed += (_, _) => overlay.Capture.SourceClosed -= onSourceClosed;

        overlay.PropertyChanged += (_, e) => OnOverlayPropertyChanged(overlay, e.PropertyName);

        Overlays.Add(overlay);
        _presenter.Show(overlay, Overlays.Count - 1);
        _activity.Add("", "Activity.OverlayCreated", "Activity.OverlayCreatedDetail", title, DescribeContent(overlay));
        return true;
    }

    /// <summary>Hotkey action: if any overlay accepts the mouse, make all click-through; otherwise make all interactive.</summary>
    public void ToggleClickThroughAll()
    {
        if (Overlays.Count == 0)
        {
            return;
        }

        var enable = Overlays.Any(o => !o.IsClickThrough);
        _bulkChange = true;
        foreach (var overlay in Overlays)
        {
            overlay.IsClickThrough = enable;
        }

        _bulkChange = false;

        _activity.Add("", enable ? "Activity.ClickThroughOn" : "Activity.ClickThroughOff", "Activity.ViaHotkey", Overlays.Count);
    }

    /// <summary>Hotkey action: if any overlay is visible, hide all; otherwise show all.</summary>
    public void ToggleVisibilityAll()
    {
        if (Overlays.Count == 0)
        {
            return;
        }

        var show = Overlays.All(o => !o.IsVisible);
        _bulkChange = true;
        foreach (var overlay in Overlays)
        {
            overlay.IsVisible = show;
        }

        _bulkChange = false;

        _activity.Add("", show ? "Activity.OverlaysShown" : "Activity.OverlaysHidden", "Activity.ViaHotkey", Overlays.Count);
    }

    /// <summary>Confirms toggles made on an overlay card (hotkeys log one summary entry instead).</summary>
    private void OnOverlayPropertyChanged(OverlayViewModel overlay, string? propertyName)
    {
        if (_bulkChange)
        {
            return;
        }

        switch (propertyName)
        {
            case nameof(OverlayViewModel.IsClickThrough):
                _activity.Add("\uE7C9", overlay.IsClickThrough ? "Activity.ClickThroughOn" : "Activity.ClickThroughOff",
                    overlay.IsClickThrough ? "Activity.ClickThroughOnDetail" : "Activity.ClickThroughOffDetail", overlay.Title);
                break;
            case nameof(OverlayViewModel.IsVisible):
                _activity.Add("\uE7B3", overlay.IsVisible ? "Activity.OverlayShown" : "Activity.OverlayHidden",
                    ActivityLog.RawDetail, overlay.Title);
                break;
        }
    }

    /// <summary>"whole window" or the region size in source pixels, for confirmation messages.</summary>
    internal static string DescribeContent(OverlayViewModel overlay)
    {
        if (!overlay.IsCropped)
        {
            return Loc.T("Activity.WholeWindow");
        }

        var capture = overlay.Capture;
        return capture.FrameWidth <= 0
            ? Loc.T("Activity.RegionSizePending")
            : Loc.F("Editor.PixelSize", (int)(capture.FrameWidth * overlay.Viewbox.Width), (int)(capture.FrameHeight * overlay.Viewbox.Height));
    }

    public void CloseAll()
    {
        foreach (var overlay in Overlays.ToList())
        {
            overlay.Close();
        }
    }
}
