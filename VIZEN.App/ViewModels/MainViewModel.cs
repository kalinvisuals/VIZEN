using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Input;
using VIZEN.App.Capture;
using VIZEN.App.Infrastructure;
using VIZEN.App.Localization;
using VIZEN.App.Models;
using VIZEN.App.Services;

namespace VIZEN.App.ViewModels;

public sealed class NavigationItem : ObservableObject
{
    private readonly string _titleKey;

    public NavigationItem(string titleKey, string glyph, ObservableObject page, string sectionKey)
    {
        _titleKey = titleKey;
        Glyph = glyph;
        Page = page;
        SectionKey = sectionKey;
    }

    public string Title => Loc.T(_titleKey);

    public string Glyph { get; }

    public ObservableObject Page { get; }

    /// <summary>Sidebar section this item is grouped under (resource key; also the grouping key).</summary>
    public string SectionKey { get; }

    public string Section => Loc.T(SectionKey);
}

/// <summary>
/// Shell view model: owns the services, the page view models and navigation state.
/// </summary>
public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly CaptureService _capture;
    private readonly AppSettings _settings;
    private readonly HotkeyService _hotkeys;
    private readonly RegionLibrary _regions;
    private readonly SourceReconnector _reconnector;
    private readonly AppAudioService _audio;
    private readonly PerformanceMonitor _monitor;
    private readonly AutoPerformanceService _autoPerformance;
    private NavigationItem? _editorReturnItem;
    private NavigationItem? _selectedNavigationItem;
    private ObservableObject? _currentPage;
    private bool _isSidebarCompact;
    private bool _isFullscreenTipVisible;

    public MainViewModel(IOverlayPresenter overlayPresenter, IOutputPresenter outputPresenter,
        IMediaPresenter mediaPresenter, IMediaFilePicker mediaFilePicker, ICompositionPresenter compositionPresenter,
        IBrowserPresenter browserPresenter, INotePresenter notePresenter, ISmartDrawPresenter drawPresenter, IMonitorProvider monitors,
        IMixerPresenter mixerPresenter)
    {
        _settings = AppSettings.LoadAndAutoSave(out var settingsError);

        // Apply the saved language, theme and accent before anything builds text or reads brushes.
        Loc.Apply(_settings.Language);
        ThemeManager.Apply(_settings.Theme, _settings.Accent);

        Activity = new ActivityLog();
        Toasts = new ToastHostViewModel(Activity);
        Activity.Add("", "Activity.Started", "Activity.StartedDetail");
        if (settingsError is not null)
        {
            Activity.Add("", "Activity.SettingsReset", ActivityLog.RawDetail, settingsError);
        }

        _capture = new CaptureService(new SourceWindowHost(_settings, Activity)) { GpuRendering = EffectiveGpuRendering() };
        var enumerator = new WindowEnumerationService();

        Sources = new WindowSourcesViewModel(enumerator, new CameraDeviceService(), _capture, _settings, Activity);
        Overlays = new OverlaysViewModel(Sources, _capture, overlayPresenter, Activity, NavigateTo);
        Outputs = new OutputsViewModel(_capture, outputPresenter, Activity);
        Profiles = new ProfilesViewModel(new ProfileStore(), Overlays, Outputs, enumerator,
            () => Sources.Sources.Where(s => s.IsCamera).ToList(), Activity);
        Calls = new CallsViewModel(Sources, Overlays, NavigateTo);
        Media = new MediaLibraryViewModel(mediaPresenter, mediaFilePicker, Activity);
        Browsers = new BrowserSourcesViewModel(browserPresenter, Activity);
        Notes = new NotesViewModel(notePresenter, Activity);
        SmartDraw = new SmartDrawViewModel(drawPresenter, monitors, Activity);
        Compositions = new CompositionsViewModel(_capture, Outputs, Media, () => Sources.Sources.ToList(), compositionPresenter, Activity);
        Studio = new StudioViewModel(Sources, Outputs, Compositions, NavigateTo);
        _audio = new AppAudioService();
        Sources.AudioFor = AudioFor;
        Overlays.AudioFor = AudioFor;
        Outputs.AudioFor = AudioFor;
        Mixer = new AudioMixerViewModel(_audio, mixerPresenter, Activity);
        BrowserExtension = new BrowserExtensionViewModel(new BrowserBridgeService(),
            new BrowserHostRegistration(new CurrentUserHostRegistry(), JsonStore.DataDirectory), Activity,
            PackagedApp.LaunchPath);
        Sources.BrowserTabs = BrowserExtension;
        Browsers.Extension = BrowserExtension;
        Mixer.BrowserTabs = BrowserExtension;
        _reconnector = new SourceReconnector(Sources, Overlays, Outputs, Compositions, _capture, Activity);
        _monitor = new PerformanceMonitor();
        _autoPerformance = new AutoPerformanceService(_capture, _monitor, _settings, Activity);
        Performance = new PerformanceViewModel(_capture, _monitor, _autoPerformance, _settings, Overlays, Activity, NavigateTo);
        Dashboard = new DashboardViewModel(Sources, Overlays, Profiles, Calls, Studio, _settings, Activity, NavigateTo);

        _hotkeys = new HotkeyService();
        _hotkeys.Pressed += OnHotkeyPressed;
        foreach (var binding in _hotkeys.Bindings)
        {
            if (HotkeyGesture.TryParse(_settings.GetHotkey(binding.Action.ToString()), out var saved))
            {
                _hotkeys.ChangeGesture(binding, saved);
            }
        }

        _hotkeys.SetEnabled(_settings.HotkeysEnabled);
        ReportHotkeyConflicts();

        Schedules = new SchedulesViewModel(Media, _hotkeys, Activity);

        Settings = new SettingsViewModel(_settings, _hotkeys, Activity);
        var updateHttp = UpdateChecker.CreateHttpClient();
        Updates = new UpdatesViewModel(_settings, Activity, new UpdateChecker(updateHttp),
            new UpdateDownloader(updateHttp, System.IO.Path.Combine(JsonStore.DataDirectory, "updates")), UpdateChannels.Detect(),
            SettingsViewModel.AppVersion, () => Overlays.Count + Outputs.Count, () => (System.Windows.Application.Current as App)?.ExitForUpdate());
        Settings.Updates = Updates;
        Updates.Start();
        _settings.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(AppSettings.HotkeysEnabled):
                    _hotkeys.SetEnabled(_settings.HotkeysEnabled);
                    ReportSetting("Settings.GlobalHotkeys", _settings.HotkeysEnabled,
                        string.Join(" · ", _hotkeys.Bindings.Select(b => $"{b.Gesture}: {b.StatusText}")));
                    ReportHotkeyConflicts();
                    break;
                case nameof(AppSettings.Language):
                    Loc.Apply(_settings.Language);
                    Activity.Add("\uE774", "Activity.LanguageChanged", "Activity.LanguageChangedDetail",
                        Loc.Languages.First(l => l.Code == _settings.Language).NativeName);
                    break;
                case nameof(AppSettings.Theme):
                    ThemeManager.Apply(_settings.Theme, _settings.Accent);
                    Activity.Add("\uE790", "Activity.ThemeChanged", "Activity.ThemeChangedDetail",
                        Loc.T("Settings.Theme" + char.ToUpperInvariant(_settings.Theme[0]) + _settings.Theme[1..]));
                    break;
                case nameof(AppSettings.Accent):
                    ThemeManager.Apply(_settings.Theme, _settings.Accent);
                    Activity.Add("\uE790", "Activity.AccentChanged", "Activity.AccentChangedDetail", Loc.T("Accent." + _settings.Accent));
                    break;
                case nameof(AppSettings.RunInTray):
                    ReportSetting("Settings.Tray", _settings.RunInTray,
                        Loc.T(_settings.RunInTray ? "Activity.TrayOnEffect" : "Activity.TrayOffEffect"));
                    break;
                case nameof(AppSettings.AutoPerformance):
                    ReportSetting("Perf.AutoEnabled", _settings.AutoPerformance,
                        Loc.T(_settings.AutoPerformance ? "Activity.PerfAutoOnEffect" : "Activity.PerfAutoOffEffect"));
                    break;
                case nameof(AppSettings.GpuRendering):
                    _capture.GpuRendering = EffectiveGpuRendering();
                    ReportSetting("Perf.GpuRendering", _settings.GpuRendering, Loc.T("Activity.GpuRenderingEffect"));
                    break;
                case nameof(AppSettings.AllowFpsReduction):
                    ReportSetting("Perf.AllowReduce", _settings.AllowFpsReduction,
                        Loc.T(_settings.AllowFpsReduction ? "Activity.PerfReduceOnEffect" : "Activity.PerfReduceOffEffect"));
                    break;
                case nameof(AppSettings.AutoRefreshSources):
                    ReportSetting("Settings.AutoRefresh", _settings.AutoRefreshSources,
                        Loc.T(_settings.AutoRefreshSources ? "Activity.AutoRefreshOnEffect" : "Activity.AutoRefreshOffEffect"));
                    break;
                case nameof(AppSettings.ExcludeOwnWindows):
                    ReportSetting("Settings.HideOwn", _settings.ExcludeOwnWindows, Loc.T("Activity.RescanEffect"));
                    break;
                case nameof(AppSettings.IncludeMinimizedWindows):
                    ReportSetting("Settings.IncludeMinimized", _settings.IncludeMinimizedWindows, Loc.T("Activity.RescanEffect"));
                    break;
            }
        };

        Sources.ConnectBrowserRequested = () => NavigateTo(Browsers);
        Sources.CreateOverlayRequested = source =>
        {
            if (Overlays.CreateOverlay(source))
            {
                NavigateTo(Overlays);
            }
        };

        _regions = new RegionLibrary(out var regionsError);
        if (regionsError is not null)
        {
            Activity.Add("", "Activity.RegionsLoadFailed", ActivityLog.RawDetail, regionsError);
        }

        Sources.EditRegionsRequested = OpenRegionEditor;
        Sources.CreateOutputRequested = source =>
        {
            if (Outputs.CreateOutput(source))
            {
                NavigateTo(Studio);
            }
        };
        Calls.EditRegionsRequested = OpenRegionEditor;
        Studio.EditRegionsRequested = OpenRegionEditor;

        NavigationItems =
        [
            new NavigationItem("Nav.Dashboard", "", Dashboard, "Nav.SectionWorkspace"),
            new NavigationItem("Nav.Sources", "", Sources, "Nav.SectionWorkspace"),
            new NavigationItem("Nav.Browser", "\uE774", Browsers, "Nav.SectionWorkspace"),
            new NavigationItem("Nav.Gaming", "", Overlays, "Nav.SectionModes"),
            new NavigationItem("Nav.Calls", "", Calls, "Nav.SectionModes"),
            new NavigationItem("Nav.Studio", "", Studio, "Nav.SectionModes"),
            new NavigationItem("Nav.Media", "\uE8B9", Media, "Nav.SectionLibrary"),
            new NavigationItem("Nav.Notes", "\uE70B", Notes, "Nav.SectionLibrary"),
            new NavigationItem("Nav.SmartDraw", "\uE70F", SmartDraw, "Nav.SectionLibrary"),
            new NavigationItem("Nav.Mixer", "\uE767", Mixer, "Nav.SectionLibrary"),
            new NavigationItem("Nav.Schedules", "\uE823", Schedules, "Nav.SectionLibrary"),
            new NavigationItem("Nav.Profiles", "", Profiles, "Nav.SectionLibrary"),
            new NavigationItem("Nav.Performance", "", Performance, "Nav.SectionLibrary"),
            new NavigationItem("Nav.Settings", "", Settings, "Nav.SectionLibrary"),
        ];
        var navigationView = new ListCollectionView(NavigationItems.ToList());
        navigationView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(NavigationItem.SectionKey)));
        NavigationView = navigationView;

        ToggleSidebarCommand = new RelayCommand(() => IsSidebarCompact = !IsSidebarCompact);
        CloseFullscreenTipCommand = new RelayCommand(() => IsFullscreenTipVisible = false);
        NeverShowFullscreenTipCommand = new RelayCommand(() =>
        {
            _settings.ShowFullscreenTip = false;
            IsFullscreenTipVisible = false;
        });
        SelectedNavigationItem = NavigationItems[0];

        // Notes that were on screen when VIZEN closed come back once the main window is up.
        System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(Notes.RestoreShown, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    public ActivityLog Activity { get; }
    public ToastHostViewModel Toasts { get; }
    public DashboardViewModel Dashboard { get; }
    public WindowSourcesViewModel Sources { get; }
    public OverlaysViewModel Overlays { get; }

    public UpdatesViewModel Updates { get; }
    public OutputsViewModel Outputs { get; }
    public CallsViewModel Calls { get; }
    public StudioViewModel Studio { get; }
    public ProfilesViewModel Profiles { get; }
    public MediaLibraryViewModel Media { get; }
    public CompositionsViewModel Compositions { get; }
    public BrowserSourcesViewModel Browsers { get; }
    public NotesViewModel Notes { get; }
    public SmartDrawViewModel SmartDraw { get; }
    public AudioMixerViewModel Mixer { get; }
    public BrowserExtensionViewModel BrowserExtension { get; }
    public SchedulesViewModel Schedules { get; }
    public SettingsViewModel Settings { get; }
    public PerformanceViewModel Performance { get; }

    public IReadOnlyList<NavigationItem> NavigationItems { get; }

    /// <summary>Navigation items grouped by sidebar section.</summary>
    public ICollectionView NavigationView { get; }

    public ICommand ToggleSidebarCommand { get; }

    /// <summary>Banner: a game runs in exclusive fullscreen, so overlays cannot appear over it.</summary>
    public bool IsFullscreenTipVisible
    {
        get => _isFullscreenTipVisible;
        private set => SetProperty(ref _isFullscreenTipVisible, value);
    }

    public ICommand CloseFullscreenTipCommand { get; }

    public ICommand NeverShowFullscreenTipCommand { get; }

    /// <summary>
    /// Called when VIZEN held its overlays back because a game runs in exclusive fullscreen (raising them would minimize
    /// the game). Logged every time; the banner appears unless the user turned it off.
    /// </summary>
    /// <returns>True when the banner was newly shown.</returns>
    public bool ReportExclusiveFullscreen()
    {
        Activity.Record("", "Activity.ExclusiveFullscreen", "Activity.ExclusiveFullscreenDetail");
        if (!_settings.ShowFullscreenTip || IsFullscreenTipVisible)
        {
            return false;
        }

        IsFullscreenTipVisible = true;
        return true;
    }

    public bool IsCaptureSupported => _capture.IsSupported;

    public string CaptureStatusText => _capture.IsSupported ? Loc.T("Shell.CaptureReady") : Loc.T("Capture.Unsupported");

    public string VersionText => Loc.F("Shell.Version", SettingsViewModel.AppVersion);

    public NavigationItem? SelectedNavigationItem
    {
        get => _selectedNavigationItem;
        set
        {
            if (value is null)
            {
                // Keep a page selected at all times.
                OnPropertyChanged();
                return;
            }

            if (SetProperty(ref _selectedNavigationItem, value))
            {
                CurrentPage = value.Page;
                Sources.IsActive = ReferenceEquals(value.Page, Sources);
                // With GPU rendering the thumbnails present natively and need no CPU copies.
                _capture.ThumbnailsVisible = ReferenceEquals(value.Page, Overlays) && !_capture.GpuRendering;
                _monitor.Detailed = ReferenceEquals(value.Page, Performance);
                Mixer.IsPageVisible = ReferenceEquals(value.Page, Mixer);
                OnPropertyChanged(nameof(CurrentTitle));
            }
        }
    }

    public ObservableObject? CurrentPage
    {
        get => _currentPage;
        private set
        {
            var previous = _currentPage;
            if (SetProperty(ref _currentPage, value) && previous is RegionEditorViewModel editor)
            {
                // Leaving the editor releases its capture lease.
                editor.Dispose();
            }
        }
    }

    public string CurrentTitle => CurrentPage is RegionEditorViewModel
        ? Loc.T("Editor.Title")
        : SelectedNavigationItem?.Title ?? string.Empty;

    /// <summary>Opens the region editor for <paramref name="source"/>; Back returns to the current page.</summary>
    private void OpenRegionEditor(WindowSource source)
    {
        if (!_capture.CanCapture(source))
        {
            return;
        }

        _editorReturnItem = _selectedNavigationItem ?? NavigationItems[1];
        var editor = new RegionEditorViewModel(source, _capture, _regions, Overlays, Outputs, Activity, CloseRegionEditor);
        Activity.Add("\uE7A8", "Activity.EditorOpened", "Activity.EditorOpenedDetail", source.Title, editor.Regions.Count);

        // Deselect the sidebar so clicking any item (even the one we came from) leaves the editor.
        _selectedNavigationItem = null;
        OnPropertyChanged(nameof(SelectedNavigationItem));
        Sources.IsActive = false;
        CurrentPage = editor;
        OnPropertyChanged(nameof(CurrentTitle));
    }

    private void CloseRegionEditor() => SelectedNavigationItem = _editorReturnItem ?? NavigationItems[1];

    public bool IsSidebarCompact
    {
        get => _isSidebarCompact;
        set => SetProperty(ref _isSidebarCompact, value);
    }

    public async Task InitializeAsync()
    {
        await _capture.InitializeAsync();
        Browsers.OpenAutomatic();
        await Sources.RefreshAsync();
        Compositions.OpenAutomatic();
    }

    /// <summary>The setting, unless the VIZEN_RENDERER environment variable ("gpu" or "cpu") overrides it for benchmarks.</summary>
    private bool EffectiveGpuRendering() => Environment.GetEnvironmentVariable("VIZEN_RENDERER")?.ToLowerInvariant() switch
    {
        "cpu" => false,
        "gpu" => true,
        _ => _settings.GpuRendering,
    };

    /// <summary>Notification-area menu: show or hide every overlay and media layer.</summary>
    /// <summary>Sound controls for a source's app (shared per app; null for cameras).</summary>
    private AppAudioViewModel? AudioFor(WindowSource source) =>
        source.IsCamera || source.IsSynthetic || _audio.For(source.ExecutablePath, source.ProcessId, source.ProcessName) is not { } app
            ? null
            : new AppAudioViewModel(app, _audio.OutputDevices(), Activity);

    public void ToggleAllLayers()
    {
        Overlays.ToggleVisibilityAll();
        Media.ToggleVisibilityAll();
    }

    private void OnHotkeyPressed(object? sender, HotkeyAction action)
    {
        switch (action)
        {
            case HotkeyAction.ToggleClickThrough:
                Overlays.ToggleClickThroughAll();
                break;
            case HotkeyAction.ToggleVisibility:
                Overlays.ToggleVisibilityAll();
                Media.ToggleVisibilityAll();
                break;
            case HotkeyAction.ToggleNotes:
                Notes.ToggleVisibilityAll();
                break;
            case HotkeyAction.NotesEditMode:
                Notes.ToggleEditMode();
                break;
            case HotkeyAction.ToggleSmartDraw:
                SmartDraw.ToggleEnabled();
                break;
            case HotkeyAction.DrawMode:
                SmartDraw.ToggleMode();
                break;
            case HotkeyAction.ToggleAnnotations:
                SmartDraw.ToggleVisibility();
                break;
            case HotkeyAction.ToggleMixer:
                Mixer.ToggleFloating();
                break;
            case HotkeyAction.ClearAnnotations:
                SmartDraw.RequestClear();
                break;
        }
    }

    /// <summary>Confirms a settings toggle: name, new state, what it does now. Saved automatically.</summary>
    private void ReportSetting(string nameKey, bool enabled, string effect) =>
        Activity.Add("\uE713", "Activity.SettingChanged", "Activity.SettingChangedDetail",
            Loc.T(nameKey), Loc.T(enabled ? "Common.On" : "Common.Off"), effect);

    private void ReportHotkeyConflicts()
    {
        foreach (var binding in _hotkeys.Bindings.Where(b => b.IsConflict))
        {
            Activity.Add("", "Activity.HotkeyUnavailable", "Activity.HotkeyUnavailableDetail", binding.Gesture);
        }
    }

    private void NavigateTo(ObservableObject page)
    {
        var target = NavigationItems.FirstOrDefault(n => ReferenceEquals(n.Page, page));
        if (target is not null)
        {
            SelectedNavigationItem = target;
        }
    }

    public void Dispose()
    {
        Updates.Dispose();
        Sources.IsActive = false;
        (CurrentPage as RegionEditorViewModel)?.Dispose();
        Overlays.CloseAll();
        Outputs.CloseAll();
        Schedules.Shutdown();
        Compositions.SaveNow();
        Compositions.CloseAll();
        Browsers.SaveNow();
        Browsers.CloseAll();
        Media.SaveNow();
        Media.CloseAll();
        Notes.SaveNow();
        Notes.CloseAll();
        SmartDraw.Dispose();
        Mixer.Shutdown();
        BrowserExtension.Dispose();
        _audio.Dispose();
        _autoPerformance.Dispose();
        _monitor.Dispose();
        _hotkeys.Dispose();
        _capture.Dispose();
    }
}
