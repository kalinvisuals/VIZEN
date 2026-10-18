using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using VIZEN.App.Capture;
using VIZEN.App.Infrastructure;
using VIZEN.App.Localization;
using VIZEN.App.Models;
using VIZEN.App.Services;

namespace VIZEN.App.ViewModels;

/// <summary>
/// Interactive crop editor for one source window: draw, move and resize several regions on a live
/// preview, lock aspect ratios, and open each region as its own overlay. Regions are remembered per app.
/// </summary>
public sealed class RegionEditorViewModel : ObservableObject, IDisposable
{
    /// <summary>Editing needs a smooth preview but not the source's full rate.</summary>
    private const double EditorMaxFps = 30;

    private readonly CaptureLease _lease;
    private readonly RegionLibrary _library;
    private readonly OverlaysViewModel _overlays;
    private readonly OutputsViewModel _outputs;
    private readonly ActivityLog _activity;
    private CropRegionViewModel? _selectedRegion;
    private string? _statusMessage;
    private string? _errorMessage;
    private bool _disposed;

    public RegionEditorViewModel(
        WindowSource source,
        CaptureService capture,
        RegionLibrary library,
        OverlaysViewModel overlays,
        OutputsViewModel outputs,
        ActivityLog activity,
        Action back)
    {
        Source = source;
        _library = library;
        _overlays = overlays;
        _outputs = outputs;
        _activity = activity;
        _lease = capture.Acquire(source, EditorMaxFps);
        Capture.PropertyChanged += OnCapturePropertyChanged;

        foreach (var saved in library.Get(source))
        {
            Regions.Add(Track(CropRegionViewModel.FromSaved(saved)));
        }

        Regions.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasRegions));
            OnPropertyChanged(nameof(RegionCountText));
        };
        SyncRegionsWithFrame();
        SelectedRegion = Regions.FirstOrDefault();

        AspectOptions =
        [
            new AspectOption("free", null),
            new AspectOption("16:9", 16.0 / 9),
            new AspectOption("4:3", 4.0 / 3),
            new AspectOption("1:1", 1.0),
            new AspectOption("9:16", 9.0 / 16),
        ];

        BackCommand = new RelayCommand(back);
        AddRegionCommand = new RelayCommand<object>(p => AddRegion(p is Rect r ? r : null), _ => true);
        AddDefaultRegionCommand = new RelayCommand(() => AddRegion(null));
        DeleteRegionCommand = new RelayCommand(DeleteSelected, () => SelectedRegion is not null);
        CommitCommand = new RelayCommand(CommitGeometry);
        OpenOverlayCommand = new RelayCommand<CropRegionViewModel>(r => OpenOverlays([r]));
        OpenAllCommand = new RelayCommand(() => OpenOverlays(Regions.ToList()), () => Regions.Count > 0);
        OpenOutputCommand = new RelayCommand<CropRegionViewModel>(r => OpenOutputs([r]));
        OpenAllOutputsCommand = new RelayCommand(() => OpenOutputs(Regions.ToList()), () => Regions.Count > 0);
    }

    public WindowSource Source { get; }

    public LiveCapture Capture => _lease.Capture;

    public System.Windows.Media.ImageSource? Frame => Capture.Frame;

    public ObservableCollection<CropRegionViewModel> Regions { get; } = [];

    public IReadOnlyList<AspectOption> AspectOptions { get; }

    public bool HasRegions => Regions.Count > 0;

    public string RegionCountText => Loc.F("Editor.RegionCount", Regions.Count);

    public CropRegionViewModel? SelectedRegion
    {
        get => _selectedRegion;
        set
        {
            if (SetProperty(ref _selectedRegion, value))
            {
                OnPropertyChanged(nameof(HasSelection));
                OnPropertyChanged(nameof(SelectedAspect));
            }
        }
    }

    public bool HasSelection => SelectedRegion is not null;

    /// <summary>Aspect choice of the selected region; setting it reshapes the region around its centre.</summary>
    public AspectOption? SelectedAspect
    {
        get => SelectedRegion is null
            ? null
            : AspectOptions.FirstOrDefault(o => o.Ratio is { } r && SelectedRegion.Aspect is { } a && Math.Abs(r - a) < 0.001)
              ?? AspectOptions[0];
        set
        {
            if (SelectedRegion is null || value is null)
            {
                return;
            }

            SelectedRegion.Aspect = value.Ratio;
            if (value.Ratio is { } ratio && Capture.FrameWidth > 0)
            {
                SelectedRegion.Bounds = RegionMath.WithAspect(
                    SelectedRegion.Bounds, RegionMath.NormalizedAspect(ratio, Capture.FrameWidth, Capture.FrameHeight));
            }

            OnPropertyChanged();
            Save();
            if (value.Ratio is null)
            {
                _activity.Add("\uE799", "Activity.RegionAspect", "Activity.RegionAspectFreeDetail",
                    SelectedRegion.Name, SelectedRegion.PixelSizeText);
            }
            else
            {
                _activity.Add("\uE799", "Activity.RegionAspect", "Activity.RegionAspectDetail",
                    SelectedRegion.Name, value.Label, SelectedRegion.PixelSizeText);
            }
        }
    }

    public string? StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set => SetProperty(ref _errorMessage, value);
    }

    public ICommand BackCommand { get; }

    /// <summary>Adds a region; parameter is the normalized Rect drawn on the canvas (or anything else for a default region).</summary>
    public ICommand AddRegionCommand { get; }

    public ICommand AddDefaultRegionCommand { get; }

    public ICommand DeleteRegionCommand { get; }

    /// <summary>Persists regions after an interactive move/resize ends.</summary>
    public ICommand CommitCommand { get; }

    public ICommand OpenOverlayCommand { get; }

    public ICommand OpenAllCommand { get; }

    public ICommand OpenOutputCommand { get; }

    /// <summary>Opens one clean OBS output window per region.</summary>
    public ICommand OpenAllOutputsCommand { get; }

    private void AddRegion(Rect? bounds)
    {
        var region = new CropRegionViewModel(
            Guid.NewGuid(), NextName(), bounds ?? RegionMath.DefaultRegion(Regions.Count), aspect: null);
        Regions.Add(Track(region));
        SyncRegionsWithFrame();
        SelectedRegion = region;
        Save();
        _activity.Add("\uE7A8", "Activity.RegionAdded", "Activity.RegionAddedDetail", region.Name, region.PixelSizeText, Source.Title);
    }

    private void DeleteSelected()
    {
        if (SelectedRegion is null)
        {
            return;
        }

        var removed = SelectedRegion;
        var index = Regions.IndexOf(SelectedRegion);
        Regions.Remove(SelectedRegion);
        SelectedRegion = Regions.Count == 0 ? null : Regions[Math.Min(index, Regions.Count - 1)];
        Save();
        _activity.Add("\uE74D", "Activity.RegionDeleted", "Activity.RegionDeletedDetail", removed.Name, Source.Title);
    }

    private void OpenOverlays(IReadOnlyList<CropRegionViewModel> regions)
    {
        var opened = regions.Count(r => _overlays.CreateOverlay(Source, region: r.Bounds, regionName: r.Name));
        StatusMessage = opened == regions.Count
            ? Loc.F("Editor.OverlaysOpened", opened)
            : Loc.F("Editor.OverlaysPartlyOpened", opened, regions.Count);
        ErrorMessage = opened == regions.Count ? null : _overlays.ErrorMessage;
    }

    private void OpenOutputs(IReadOnlyList<CropRegionViewModel> regions)
    {
        var opened = regions.Count(r => _outputs.CreateOutput(Source, r.Bounds, r.Name));
        StatusMessage = opened == regions.Count
            ? Loc.F("Editor.OutputsOpened", opened)
            : Loc.F("Editor.OutputsPartlyOpened", opened, regions.Count);
        ErrorMessage = opened == regions.Count ? null : _outputs.ErrorMessage;
    }

    private string NextName()
    {
        for (var n = Regions.Count + 1; ; n++)
        {
            var name = Loc.F("Editor.DefaultRegionName", n);
            if (Regions.All(r => r.Name != name))
            {
                return name;
            }
        }
    }

    /// <summary>Called when a move/resize on the canvas ends: persist and confirm the new geometry.</summary>
    private void CommitGeometry()
    {
        Save();
        if (SelectedRegion is { } region)
        {
            _activity.Add("\uE7A8", "Activity.RegionChanged", "Activity.RegionChangedDetail", region.Name, region.PixelSizeText,
                (int)Math.Round(region.Bounds.X * 100), (int)Math.Round(region.Bounds.Y * 100));
        }
    }

    private void Save()
    {
        try
        {
            _library.Set(Source, Regions.Select(r => r.ToSaved()));
            ErrorMessage = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ErrorMessage = Loc.F("Editor.SaveFailed", ex.Message);
        }
    }

    private void OnCapturePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == nameof(LiveCapture.Frame))
        {
            OnPropertyChanged(nameof(Frame));
            SyncRegionsWithFrame();
        }
    }

    private void SyncRegionsWithFrame()
    {
        var frame = Frame;
        foreach (var region in Regions)
        {
            region.SetSource(frame, Capture.FrameWidth, Capture.FrameHeight);
        }
    }

    /// <summary>Persists renames as soon as they are committed in the list.</summary>
    private CropRegionViewModel Track(CropRegionViewModel region)
    {
        region.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(CropRegionViewModel.Name))
            {
                Save();
                _activity.Add("\uE8AC", "Activity.RegionRenamed", ActivityLog.RawDetail, region.Name);
            }
        };
        return region;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Capture.PropertyChanged -= OnCapturePropertyChanged;
        _lease.Dispose();
    }
}
