using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using VIZEN.App.Capture;
using VIZEN.App.Infrastructure;
using VIZEN.App.Localization;
using VIZEN.App.Models;
using VIZEN.App.Services;

namespace VIZEN.App.ViewModels;

/// <summary>A category chip on the Sources page ("All" when <see cref="Category"/> is null).</summary>
public sealed class CategoryFilter : ObservableObject
{
    private readonly Action<CategoryFilter> _select;
    private int _count;
    private bool _isSelected;

    public CategoryFilter(SourceCategory? category, Action<CategoryFilter> select)
    {
        Category = category;
        _select = select;
    }

    public SourceCategory? Category { get; }

    public string Label => Category is { } c ? Loc.T($"Category.{c}") : Loc.T("Category.All");

    public string Glyph => Category is { } c ? SourceClassifier.Glyph(c) : "";

    public int Count
    {
        get => _count;
        internal set => SetProperty(ref _count, value);
    }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (value && !_isSelected)
            {
                _select(this);
            }
            else if (!value && _isSelected)
            {
                // RadioButton groups uncheck the old chip; the parent already tracks the selection.
                return;
            }
        }
    }

    internal void SetSelected(bool selected) => SetProperty(ref _isSelected, selected, nameof(IsSelected));
}

public sealed class WindowSourcesViewModel : ObservableObject
{
    /// <summary>Previews are small; capping their frame rate keeps CPU use low.</summary>
    private const double PreviewMaxFps = 24;

    private static readonly TimeSpan AutoRefreshInterval = TimeSpan.FromSeconds(3);

    /// <summary>Camera devices change rarely; re-enumerate them on every Nth auto-refresh.</summary>
    private const int CameraRefreshEvery = 10;

    private readonly WindowEnumerationService _enumerator;
    private readonly CameraDeviceService _cameraService;
    private readonly CaptureService _capture;
    private readonly AppSettings _settings;
    private readonly ActivityLog _activity;
    private readonly AsyncRelayCommand _refreshCommand;
    private readonly DispatcherTimer _autoRefreshTimer;

    private string _searchText = string.Empty;
    private WindowSource? _selectedSource;
    private CategoryFilter _selectedFilter;
    private bool _isBusy;
    private DateTime? _lastRefreshed;
    private string? _errorDetail;
    private string? _cameraErrorDetail;
    private bool _isActive;
    private LiveCapture? _preview;
    private CaptureLease? _previewLease;
    private int _autoRefreshTicks;

    public WindowSourcesViewModel(
        WindowEnumerationService enumerator,
        CameraDeviceService cameraService,
        CaptureService capture,
        AppSettings settings,
        ActivityLog activity)
    {
        _enumerator = enumerator;
        _cameraService = cameraService;
        _capture = capture;
        _settings = settings;
        _activity = activity;

        Filters =
        [
            new CategoryFilter(null, SelectFilter),
            new CategoryFilter(SourceCategory.Browsers, SelectFilter),
            new CategoryFilter(SourceCategory.Calls, SelectFilter),
            new CategoryFilter(SourceCategory.Media, SelectFilter),
            new CategoryFilter(SourceCategory.Cameras, SelectFilter),
            new CategoryFilter(SourceCategory.Other, SelectFilter),
        ];
        _selectedFilter = Filters[0];
        _selectedFilter.SetSelected(true);

        SourcesView = CollectionViewSource.GetDefaultView(Sources);
        SourcesView.Filter = FilterSource;

        _refreshCommand = new AsyncRelayCommand(() => RefreshAsync(userInitiated: true));
        CopyDetailsCommand = new RelayCommand(CopyDetails, () => SelectedSource is not null);
        CreateOverlayCommand = new RelayCommand(
            () => CreateOverlayRequested?.Invoke(SelectedSource!),
            () => SelectedSource is { } selected && _capture.CanCapture(selected));
        EditRegionsCommand = new RelayCommand(
            () => EditRegionsRequested?.Invoke(SelectedSource!),
            () => SelectedSource is { } selected && _capture.CanCapture(selected));
        CreateOutputCommand = new RelayCommand(
            () => CreateOutputRequested?.Invoke(SelectedSource!),
            () => SelectedSource is { } selected && _capture.CanCapture(selected));

        _settings.PropertyChanged += OnSettingsChanged;

        _autoRefreshTimer = new DispatcherTimer(AutoRefreshInterval, DispatcherPriority.Background, OnAutoRefreshTick,
            Dispatcher.CurrentDispatcher);
        _autoRefreshTimer.IsEnabled = _settings.AutoRefreshSources;
    }

    /// <summary>All discovered windows, in discovery order (new windows are appended, so the list doesn't jump).</summary>
    public ObservableCollection<WindowSource> Sources { get; } = [];

    /// <summary>Detected camera devices (detection only; camera capture is a later milestone).</summary>
    public ObservableCollection<CameraDevice> Cameras { get; } = [];

    public ICollectionView SourcesView { get; }

    public IReadOnlyList<CategoryFilter> Filters { get; }

    /// <summary>Raised after every refresh (manual or automatic). UI thread.</summary>
    public event EventHandler? Refreshed;

    /// <summary>Set by the shell: creates an overlay for the given source.</summary>
    public Action<WindowSource>? CreateOverlayRequested { get; set; }

    public ICommand RefreshCommand => _refreshCommand;

    public ICommand CopyDetailsCommand { get; }

    public ICommand CreateOverlayCommand { get; }

    /// <summary>Set by the shell: opens the region editor for the given source.</summary>
    public Action<WindowSource>? EditRegionsRequested { get; set; }

    public ICommand EditRegionsCommand { get; }

    /// <summary>Set by the shell: opens a clean OBS output window for the given source.</summary>
    public Action<WindowSource>? CreateOutputRequested { get; set; }

    public ICommand CreateOutputCommand { get; }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                ApplyFilter();
            }
        }
    }

    public SourceCategory? SelectedCategory => _selectedFilter.Category;

    public bool ShowCameraDevices => SelectedCategory is null or SourceCategory.Cameras;

    public bool HasCameras => Cameras.Count > 0;

    /// <summary>Explains the browser limitation when looking at categories that contain browser windows.</summary>
    public bool ShowBrowserNote => SelectedCategory is SourceCategory.Browsers or SourceCategory.Calls or SourceCategory.Media;

    public WindowSource? SelectedSource
    {
        get => _selectedSource;
        set
        {
            if (SetProperty(ref _selectedSource, value))
            {
                OnPropertyChanged(nameof(HasSelection));
                UpdatePreview();
                SelectedAudio = value is null ? null : AudioFor?.Invoke(value);
            }
        }
    }

    private AppAudioViewModel? _selectedAudio;

    /// <summary>Sound controls for the selected source's app (null for cameras or nothing selected).</summary>
    public AppAudioViewModel? SelectedAudio
    {
        get => _selectedAudio;
        private set
        {
            _selectedAudio?.Detach();
            SetProperty(ref _selectedAudio, value);
        }
    }

    /// <summary>Set by the shell: sound controls for a source's app.</summary>
    public Func<WindowSource, AppAudioViewModel?>? AudioFor { get; set; }

    public bool HasSelection => SelectedSource is not null;

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                RaiseCountsChanged();
            }
        }
    }

    public DateTime? LastRefreshed
    {
        get => _lastRefreshed;
        private set
        {
            if (SetProperty(ref _lastRefreshed, value))
            {
                OnPropertyChanged(nameof(LastRefreshedText));
                OnPropertyChanged(nameof(HasScanned));
            }
        }
    }

    public string? ErrorMessage => _errorDetail;

    public string? CameraError => _cameraErrorDetail is null ? null : Loc.F("Sources.CameraError", _cameraErrorDetail);

    public bool HasScanned => LastRefreshed is not null;

    public int TotalCount => Sources.Count;

    public int VisibleCount => SourcesView.Cast<object>().Count();

    /// <summary>True once a scan finished and nothing matches the current category/search.</summary>
    public bool ShowEmptyState => !IsBusy && HasScanned && VisibleCount == 0;

    public bool IsFiltered => !string.IsNullOrWhiteSpace(SearchText) || SelectedCategory is not null;

    public string SummaryText
    {
        get
        {
            if (IsBusy && TotalCount == 0)
            {
                return Loc.T("Sources.Scanning");
            }

            return IsFiltered
                ? Loc.F("Sources.CountFiltered", VisibleCount, TotalCount)
                : Loc.F("Sources.Count", TotalCount);
        }
    }

    public string LastRefreshedText
    {
        get
        {
            if (LastRefreshed is not { } t)
            {
                return Loc.T("Sources.NotScanned");
            }

            var time = t.ToString("HH:mm:ss");
            return _settings.AutoRefreshSources ? Loc.F("Sources.UpdatedAuto", time) : Loc.F("Sources.Updated", time);
        }
    }

    public string EmptyStateTitle => IsFiltered ? Loc.T("Sources.EmptyFilteredTitle") : Loc.T("Sources.EmptyTitle");

    public string EmptyStateMessage => IsFiltered ? Loc.T("Sources.EmptyFilteredMessage") : Loc.T("Sources.EmptyMessage");

    // ----- Live preview -----

    /// <summary>True while this page is on screen. The live preview only runs while active.</summary>
    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (SetProperty(ref _isActive, value))
            {
                UpdatePreview();
            }
        }
    }

    /// <summary>Live capture of the selected window, or null.</summary>
    public LiveCapture? Preview
    {
        get => _preview;
        private set
        {
            if (_preview is not null)
            {
                _preview.PropertyChanged -= OnPreviewPropertyChanged;
            }

            if (SetProperty(ref _preview, value))
            {
                if (_preview is not null)
                {
                    _preview.PropertyChanged += OnPreviewPropertyChanged;
                }

                OnPropertyChanged(nameof(PreviewMessage));
                OnPropertyChanged(nameof(HasPreviewFrame));
                OnPropertyChanged(nameof(ShowIdleBrowserHint));
            }
        }
    }

    /// <summary>The lease of <see cref="Preview"/> (the native preview tells it when no CPU copy is needed).</summary>
    public CaptureLease? PreviewLease => _previewLease;

    public bool HasPreviewFrame => Preview?.HasFrame == true;

    /// <summary>
    /// Chrome/Edge do not draw a window that was opened in the background and never shown, so its capture
    /// stays idle. Tell the user how to wake it up (bring it to the front once).
    /// </summary>
    public bool ShowIdleBrowserHint =>
        SelectedSource?.IsBrowser == true && Preview is { IsLive: true, Fps: 0, IsSourceMinimized: false };

    /// <summary>Text shown in the preview area while there is no frame to display.</summary>
    public string PreviewMessage => SelectedSource is { } source && !_capture.CanCapture(source)
        ? Loc.T("Capture.Unsupported")
        : Preview?.StatusText ?? string.Empty;

    /// <summary>Selects a category chip (used by other pages, e.g. "show call windows").</summary>
    public void ShowCategory(SourceCategory? category)
        => SelectFilter(Filters.First(f => f.Category == category));

    public Task RefreshAsync(bool userInitiated = false) => IsBusy ? Task.CompletedTask : RefreshCoreAsync(userInitiated, refreshCameras: true);

    private async Task RefreshCoreAsync(bool userInitiated, bool refreshCameras)
    {
        IsBusy = true;
        var hadError = _errorDetail is not null;

        try
        {
            var options = new WindowEnumerationOptions(_settings.ExcludeOwnWindows, _settings.IncludeMinimizedWindows);
            var windows = await Task.Run(() => _enumerator.EnumerateWindows(options));
            Merge(windows);
            LastRefreshed = DateTime.Now;
            _errorDetail = null;

            if (refreshCameras)
            {
                await RefreshCamerasAsync();
            }

            if (userInitiated)
            {
                int CountOf(SourceCategory c) => windows.Count(w => w.Category == c);
                var processCount = windows.Select(w => w.ProcessId).Distinct().Count();
                _activity.Add("", "Activity.ScanCompleted", "Activity.ScanCompletedDetail",
                    windows.Count, processCount,
                    CountOf(SourceCategory.Browsers), CountOf(SourceCategory.Calls), CountOf(SourceCategory.Media),
                    CountOf(SourceCategory.Cameras), CountOf(SourceCategory.Other), Cameras.Count);
            }
        }
        catch (Exception ex)
        {
            _errorDetail = Loc.F("Sources.EnumerationFailed", ex.Message);
            if (userInitiated || !hadError)
            {
                _activity.Add("", "Activity.ScanFailed", ActivityLog.RawDetail, ex.Message);
            }
        }
        finally
        {
            OnPropertyChanged(nameof(ErrorMessage));
            IsBusy = false;
            UpdateCategoryCounts();
            RaiseCountsChanged();
            Refreshed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Updates the collection in place: existing windows are refreshed (keeping selection and bindings),
    /// closed windows are removed and new windows are appended.
    /// </summary>
    private void Merge(IReadOnlyList<WindowSource> fresh)
    {
        var freshByHandle = fresh.ToDictionary(w => w.Handle);

        // Cameras are managed by RefreshCamerasAsync; this merge only touches windows.
        for (var i = Sources.Count - 1; i >= 0; i--)
        {
            if (!Sources[i].IsCamera && !freshByHandle.ContainsKey(Sources[i].Handle))
            {
                Sources.RemoveAt(i);
            }
        }

        var existing = Sources.Where(s => !s.IsCamera).ToDictionary(s => s.Handle);
        var categoryChanged = false;
        foreach (var window in fresh)
        {
            if (existing.TryGetValue(window.Handle, out var current))
            {
                var oldCategory = current.Category;
                current.UpdateFrom(window);
                categoryChanged |= oldCategory != current.Category;
            }
            else
            {
                Sources.Add(window);
            }
        }

        if (categoryChanged || IsFiltered)
        {
            // Titles/categories changed in place; re-evaluate the filter (selection is kept when still visible).
            SourcesView.Refresh();
        }
    }

    /// <summary>
    /// Keeps one source per detected camera in <see cref="Sources"/> (same instance across refreshes, so
    /// selection and open overlays keep pointing at it). Unplugged cameras are removed.
    /// </summary>
    private void SyncCameraSources(IReadOnlyList<CameraDevice> cameras)
    {
        var ids = cameras.Select(c => c.Id).ToHashSet();
        for (var i = Sources.Count - 1; i >= 0; i--)
        {
            if (Sources[i].CameraDeviceId is { } id && !ids.Contains(id))
            {
                Sources.RemoveAt(i);
            }
        }

        var present = Sources.Where(s => s.IsCamera).Select(s => s.CameraDeviceId!).ToHashSet();
        foreach (var camera in cameras.Where(c => !present.Contains(c.Id)))
        {
            Sources.Add(camera.ToSource());
        }
    }

    private async Task RefreshCamerasAsync()
    {
        try
        {
            var cameras = await _cameraService.FindCamerasAsync();
            if (!cameras.Select(c => c.Id).SequenceEqual(Cameras.Select(c => c.Id)))
            {
                Cameras.Clear();
                foreach (var camera in cameras)
                {
                    Cameras.Add(camera);
                }

                SyncCameraSources(cameras);
            }

            _cameraErrorDetail = null;
        }
        catch (Exception ex)
        {
            _cameraErrorDetail = ex.Message;
        }

        OnPropertyChanged(nameof(CameraError));
        OnPropertyChanged(nameof(HasCameras));
    }

    private void OnAutoRefreshTick(object? sender, EventArgs e)
    {
        if (IsBusy)
        {
            return;
        }

        _autoRefreshTicks++;
        _ = RefreshCoreAsync(userInitiated: false, refreshCameras: _autoRefreshTicks % CameraRefreshEvery == 0);
    }

    private void SelectFilter(CategoryFilter filter)
    {
        if (ReferenceEquals(filter, _selectedFilter))
        {
            return;
        }

        _selectedFilter.SetSelected(false);
        _selectedFilter = filter;
        _selectedFilter.SetSelected(true);
        OnPropertyChanged(nameof(SelectedCategory));
        OnPropertyChanged(nameof(ShowCameraDevices));
        OnPropertyChanged(nameof(ShowBrowserNote));
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        SourcesView.Refresh();
        RaiseCountsChanged();
        RefreshTabs();
    }

    // ----- Browser tabs (VIZEN Connector extension) -----

    private BrowserExtensionViewModel? _browserTabs;

    /// <summary>The browser extension's tabs, listed under the windows (filtered by the same category and search).</summary>
    public BrowserExtensionViewModel? BrowserTabs
    {
        get => _browserTabs;
        set
        {
            if (_browserTabs is not null)
            {
                _browserTabs.TabsChanged -= OnTabsChanged;
                _browserTabs.PropertyChanged -= OnTabsChanged;
            }

            _browserTabs = value;
            if (value is not null)
            {
                value.TabsChanged += OnTabsChanged;
                value.PropertyChanged += OnTabsChanged;
            }

            RefreshTabs();
        }
    }

    public ObservableCollection<BrowserTabViewModel> VisibleTabs { get; } = [];

    public bool HasVisibleTabs => VisibleTabs.Count > 0;

    /// <summary>Tabs belong to the browser categories (and "All").</summary>
    public bool ShowTabsSection => SelectedCategory is null or SourceCategory.Browsers or SourceCategory.Calls or SourceCategory.Media;

    /// <summary>The extension is connected and a browser reports tabs (otherwise the section explains how to connect).</summary>
    public bool IsTabsConnected => _browserTabs?.IsBrowserConnected == true;

    /// <summary>Connected, but no tab matches the category or search.</summary>
    public bool ShowTabsEmpty => IsTabsConnected && !HasVisibleTabs;

    public Action? ConnectBrowserRequested { get; set; }

    public ICommand ConnectBrowserCommand => new RelayCommand(() => ConnectBrowserRequested?.Invoke());

    private void OnTabsChanged(object? sender, EventArgs e) => RefreshTabs();

    private void RefreshTabs()
    {
        var wanted = _browserTabs?.Filter(SelectedCategory, SearchText).ToList() ?? [];
        if (!VisibleTabs.SequenceEqual(wanted))
        {
            VisibleTabs.Clear();
            foreach (var tab in wanted)
            {
                VisibleTabs.Add(tab);
            }
        }

        OnPropertyChanged(nameof(HasVisibleTabs));
        OnPropertyChanged(nameof(ShowTabsSection));
        OnPropertyChanged(nameof(IsTabsConnected));
        OnPropertyChanged(nameof(ShowTabsEmpty));
    }

    private bool FilterSource(object item)
    {
        if (item is not WindowSource source)
        {
            return true;
        }

        if (SelectedCategory is { } category && source.Category != category)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(SearchText))
        {
            return true;
        }

        var term = SearchText.Trim();
        return source.Title.Contains(term, StringComparison.OrdinalIgnoreCase)
            || source.ProcessName.Contains(term, StringComparison.OrdinalIgnoreCase)
            || source.ClassName.Contains(term, StringComparison.OrdinalIgnoreCase);
    }

    private void UpdateCategoryCounts()
    {
        foreach (var filter in Filters)
        {
            filter.Count = filter.Category switch
            {
                null => Sources.Count,
                SourceCategory.Cameras => Sources.Count(s => s.Category == SourceCategory.Cameras),
                { } c => Sources.Count(s => s.Category == c),
            };
        }
    }

    private void UpdatePreview()
    {
        var target = IsActive && SelectedSource is { } selected && _capture.CanCapture(selected) ? SelectedSource : null;
        var alreadyPreviewing = target is not null && Preview is not null && Preview.Key.Equals(CaptureService.KeyFor(target))
            && Preview.Status is CaptureStatus.Starting or CaptureStatus.Live;
        if (alreadyPreviewing || (target is null && Preview is null))
        {
            return;
        }

        // Leases share the capture with overlays/editor of the same window; releasing ours never stops theirs.
        var oldLease = _previewLease;
        // Shown natively (GPU), the preview follows the source rate like the overlays; the WPF image is capped.
        _previewLease = target is null ? null : _capture.Acquire(target, _capture.GpuRendering ? 0 : PreviewMaxFps, presentsNatively: true);
        OnPropertyChanged(nameof(PreviewLease));
        Preview = _previewLease?.Capture;
        oldLease?.Dispose();
    }

    private void OnPreviewPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.PropertyName)
            || e.PropertyName is nameof(LiveCapture.StatusText) or nameof(LiveCapture.Status))
        {
            OnPropertyChanged(nameof(PreviewMessage));
            OnPropertyChanged(nameof(ShowIdleBrowserHint));
        }

        if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == nameof(LiveCapture.HasFrame))
        {
            OnPropertyChanged(nameof(HasPreviewFrame));
        }

        // A camera's resolution is only known once it delivers frames; show it in the details.
        if (e.PropertyName is nameof(LiveCapture.Frame) or nameof(LiveCapture.HasFrame)
            && SelectedSource is { IsCamera: true } camera && Preview is { FrameWidth: > 0 } frame
            && (camera.Width != frame.FrameWidth || camera.Height != frame.FrameHeight))
        {
            camera.UpdateFrom(new WindowSource
            {
                Handle = camera.Handle,
                ProcessName = camera.ProcessName,
                ProcessId = camera.ProcessId,
                ClassName = camera.ClassName,
                Title = camera.Title,
                Width = frame.FrameWidth,
                Height = frame.FrameHeight,
                State = camera.State,
                Category = camera.Category,
                Icon = camera.Icon,
            });
        }
    }

    private void CopyDetails()
    {
        if (SelectedSource is null)
        {
            return;
        }

        try
        {
            Clipboard.SetText(SelectedSource.ToDetailsText());
            _activity.Add("", "Activity.CopiedDetails", "Activity.CopiedDetailsDetail", SelectedSource.Title);
        }
        catch (COMException)
        {
            // Another process is holding the clipboard open.
            _errorDetail = Loc.T("Sources.ClipboardBusy");
            OnPropertyChanged(nameof(ErrorMessage));
        }
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AppSettings.ExcludeOwnWindows) or nameof(AppSettings.IncludeMinimizedWindows):
                _ = RefreshAsync();
                break;
            case nameof(AppSettings.AutoRefreshSources):
                _autoRefreshTimer.IsEnabled = _settings.AutoRefreshSources;
                OnPropertyChanged(nameof(LastRefreshedText));
                break;
        }
    }

    private void RaiseCountsChanged()
    {
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(VisibleCount));
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(ShowEmptyState));
        OnPropertyChanged(nameof(IsFiltered));
        OnPropertyChanged(nameof(EmptyStateTitle));
        OnPropertyChanged(nameof(EmptyStateMessage));
    }
}
