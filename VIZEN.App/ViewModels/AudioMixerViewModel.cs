using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using VIZEN.App.Infrastructure;
using VIZEN.App.Localization;
using VIZEN.App.Models;
using VIZEN.App.Services;

namespace VIZEN.App.ViewModels;

/// <summary>One app in the mixer (page and floating window share these rows).</summary>
public sealed class MixerAppViewModel : ObservableObject
{
    private ImageSource? _icon;
    private bool _iconLoaded;
    private readonly Func<IMixerApp, ImageSource?> _iconFor;
    private readonly Action? _userChanged;
    private readonly AudioFocusViewModel? _focus;

    /// <param name="userChanged">Called when the user moves this volume or mutes it here (not for outside changes).</param>
    /// <param name="focus">Audio Focus, so a lowered app shows (and keeps) the user's own volume.</param>
    public MixerAppViewModel(IMixerApp app, Func<IMixerApp, ImageSource?> iconFor, Action? userChanged = null, AudioFocusViewModel? focus = null)
    {
        App = app;
        _iconFor = iconFor;
        _userChanged = userChanged;
        _focus = focus;
        ToggleMuteCommand = new RelayCommand(() => Muted = !Muted);
        app.Changed += OnAppChanged;
        app.PeakChanged += OnPeakChanged;
    }

    public IMixerApp App { get; }

    public string Name => App.IsSystemSounds ? Loc.T("Mixer.SystemSounds") : App.AppName;

    /// <summary>The executable's icon (read once, when first shown).</summary>
    public ImageSource? Icon
    {
        get
        {
            if (!_iconLoaded)
            {
                _iconLoaded = true;
                _icon = _iconFor(App);
            }

            return _icon;
        }
    }

    public bool HasIcon => Icon is not null;

    public double Peak => App.Peak;

    /// <summary>The user's own volume for this app (while Audio Focus lowers it, the level it comes back to).</summary>
    public double VolumePercent
    {
        get => Math.Round((_focus?.BaseFor(App) ?? App.Volume) * 100);
        set
        {
            var volume = (float)(Math.Clamp(value, 0, 100) / 100);
            if (_focus?.UserSetVolume(App, volume) != true)
            {
                App.Volume = volume;
            }

            OnPropertyChanged();
            OnPropertyChanged(nameof(VolumeText));
            _userChanged?.Invoke();
        }
    }

    /// <summary>Audio Focus holds this app lowered right now.</summary>
    public bool IsFocusLowered => _focus?.BaseFor(App) is not null;

    /// <summary>For a browser: its tabs that play sound or are muted (from the VIZEN Connector extension).</summary>
    public ObservableCollection<BrowserTabViewModel> Tabs { get; } = [];

    public bool HasTabs => Tabs.Count > 0;

    internal void SetTabs(IEnumerable<BrowserTabViewModel> tabs)
    {
        var list = tabs.ToList();
        if (Tabs.SequenceEqual(list))
        {
            return;
        }

        Tabs.Clear();
        foreach (var tab in list)
        {
            Tabs.Add(tab);
        }

        OnPropertyChanged(nameof(HasTabs));
    }

    internal void RefreshFocus()
    {
        OnPropertyChanged(nameof(IsFocusLowered));
        OnPropertyChanged(nameof(VolumePercent));
        OnPropertyChanged(nameof(VolumeText));
    }

    public string VolumeText => $"{VolumePercent:0}%";

    public bool Muted
    {
        get => App.Muted;
        set
        {
            if (value == App.Muted)
            {
                return;
            }

            App.Muted = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(MuteGlyph));
            _userChanged?.Invoke();
        }
    }

    public bool IsPlaying => App.IsPlayingSound;

    public string MuteGlyph => Muted ? "" : "";

    public string StatusText => App.IsPlayingSound ? Loc.T("Audio.Playing") : Loc.T("Audio.Silent");

    /// <summary>One word for the narrow channel strips ("Playing" / "Silent").</summary>
    public string ShortStatusText => App.IsPlayingSound ? Loc.T("Mixer.StatusPlaying") : Loc.T("Mixer.StatusSilent");

    /// <summary>"2 audio sessions" when one row controls several sessions (empty for one).</summary>
    public string SessionsText => App.SessionCount > 1 ? Loc.F("Mixer.Sessions", App.SessionCount) : string.Empty;

    public bool HasSeveralSessions => App.SessionCount > 1;

    public string VolumeName => Loc.F("Mixer.VolumeOf", Name);

    public string MuteName => Loc.F("Mixer.MuteOf", Name);

    public ICommand ToggleMuteCommand { get; }

    public void Detach()
    {
        App.Changed -= OnAppChanged;
        App.PeakChanged -= OnPeakChanged;
    }

    private void OnPeakChanged(object? sender, EventArgs e) => OnPropertyChanged(nameof(Peak));

    private void OnAppChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(VolumePercent));
        OnPropertyChanged(nameof(VolumeText));
        OnPropertyChanged(nameof(Muted));
        OnPropertyChanged(nameof(MuteGlyph));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(ShortStatusText));
        OnPropertyChanged(nameof(IsPlaying));
        OnPropertyChanged(nameof(SessionsText));
        OnPropertyChanged(nameof(HasSeveralSessions));
    }
}

/// <summary>A saved mix: the volume and mute of each app.</summary>
public sealed class AudioProfileViewModel : ObservableObject
{
    private readonly AudioMixerViewModel _owner;
    private bool _isConfirmingDelete;
    private bool _isActive;
    private bool _isSelected;

    public AudioProfileViewModel(AudioProfile profile, AudioMixerViewModel owner)
    {
        Profile = profile;
        _owner = owner;
        ApplyCommand = new RelayCommand(() => _owner.Apply(this));
        SaveCommand = new RelayCommand(() => _owner.SaveInto(this));
        DeleteCommand = new RelayCommand(() => IsConfirmingDelete = true, () => IsCustom);
        ConfirmDeleteCommand = new RelayCommand(() => _owner.Delete(this));
        CancelDeleteCommand = new RelayCommand(() => IsConfirmingDelete = false);
    }

    public AudioProfile Profile { get; }

    public bool IsCustom => Profile.Kind == AudioProfileKind.Custom;

    public string Name => IsCustom ? Profile.Name : Loc.T("Mixer.Profile." + Profile.Kind);

    public string Glyph => Profile.Kind switch
    {
        AudioProfileKind.Gaming => "",
        AudioProfileKind.Streaming => "",
        AudioProfileKind.Meeting => "",
        AudioProfileKind.Studio => "",
        _ => "",
    };

    public bool IsEmpty => Profile.Entries.Count == 0;

    /// <summary>"Discord 80% · Google Chrome 30% · Spotify muted", or how to fill an empty profile.</summary>
    public string Summary => IsEmpty
        ? Loc.T("Mixer.ProfileEmpty")
        : string.Join(" · ", Profile.Entries.Select(e => e.Muted
            ? Loc.F("Mixer.EntryMuted", EntryName(e))
            : $"{EntryName(e)} {Math.Round(e.Volume * 100):0}%"));

    public bool IsConfirmingDelete
    {
        get => _isConfirmingDelete;
        set => SetProperty(ref _isConfirmingDelete, value);
    }

    /// <summary>Applied (or saved) last, and no volume has been changed by hand since.</summary>
    public bool IsActive
    {
        get => _isActive;
        internal set => SetProperty(ref _isActive, value);
    }

    /// <summary>The profile chosen in the segmented control: "Save current mix" saves into it.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        internal set => SetProperty(ref _isSelected, value);
    }

    public ICommand ApplyCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand ConfirmDeleteCommand { get; }
    public ICommand CancelDeleteCommand { get; }

    internal void Refresh()
    {
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(Name));
    }

    private static string EntryName(AudioProfileEntry e) => e.App == AudioProfiles.SystemKey ? Loc.T("Mixer.SystemSounds") : e.Name;
}

/// <summary>
/// VIZEN Smart Audio Mixer: every app that has a Windows audio session, with its own volume, mute and level meter
/// (the per-app setting of the Windows volume mixer; the master volume is never changed), audio profiles, and a
/// compact floating window for use over games. Sessions are watched only while the page or the floating mixer is on
/// screen. Profiles and the floating window's position are saved to audio.json.
/// </summary>
public sealed class AudioMixerViewModel : ObservableObject
{
    public const string FileName = "audio.json";
    private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(800);

    private readonly IMixerSource _source;
    private readonly IMixerPresenter _presenter;
    private readonly ActivityLog _activity;
    private readonly DispatcherTimer _saveTimer;
    private bool _isPageVisible;
    private bool _isFloatingShown;
    private string _newProfileName = string.Empty;
    private string? _errorMessage;
    private double _floatingOpacity = 1;

    public AudioMixerViewModel(IMixerSource source, IMixerPresenter presenter, ActivityLog activity)
    {
        _source = source;
        _presenter = presenter;
        _activity = activity;
        _saveTimer = new DispatcherTimer(SaveDelay, DispatcherPriority.Background, (_, _) => SaveNow(), Dispatcher.CurrentDispatcher);
        _saveTimer.Stop();

        var data = JsonStore.Load<AudioMixerFile>(FileName, out var loadError);
        if (loadError is not null)
        {
            _activity.Add("", "Mixer.LoadFailed", ActivityLog.RawDetail, loadError);
        }

        foreach (var profile in AudioProfiles.Sanitize(data?.Profiles))
        {
            Profiles.Add(new AudioProfileViewModel(profile, this));
        }

        if (data is { MixerLeft: { } left, MixerTop: { } top } && double.IsFinite(left) && double.IsFinite(top))
        {
            FloatingPosition = new Point(left, top);
        }

        if (data is { MixerWidth: { } width, MixerHeight: { } height } && double.IsFinite(width) && double.IsFinite(height) && width > 0 && height > 0)
        {
            FloatingSize = new Size(width, height);
        }

        if (data?.MixerOpacity is { } opacity && double.IsFinite(opacity))
        {
            _floatingOpacity = Math.Clamp(opacity, MinFloatingOpacity, 1);
        }

        ToggleMasterMuteCommand = new RelayCommand(() => MasterMuted = !MasterMuted, () => _source.HasMaster);
        _source.MasterChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasMaster));
            OnPropertyChanged(nameof(MasterName));
            OnPropertyChanged(nameof(MasterVolumePercent));
            OnPropertyChanged(nameof(MasterMuted));
            OnPropertyChanged(nameof(MasterMuteGlyph));
        };
        _source.MasterPeakChanged += (_, _) => OnPropertyChanged(nameof(MasterPeak));

        ToggleFloatingCommand = new RelayCommand(ToggleFloating);
        HideFloatingCommand = new RelayCommand(() => IsFloatingShown = false);
        RefreshCommand = new RelayCommand(_source.RescanNow);
        CreateProfileCommand = new RelayCommand(() => CreateProfile(), () => !string.IsNullOrWhiteSpace(NewProfileName));

        _source.MixerAppsChanged += (_, _) => SyncApps();
        Focus = new AudioFocusViewModel(_source, _activity, data?.Focus, RequestSave, () =>
        {
            foreach (var row in Apps)
            {
                row.RefreshFocus();
            }
        });
        SyncApps();
        Select(Profiles.FirstOrDefault()); // something to save into from the start
    }

    public ObservableCollection<MixerAppViewModel> Apps { get; } = [];

    /// <summary>Smart Audio Focus (phase 2): lowers other apps while the priority app is heard.</summary>
    public AudioFocusViewModel Focus { get; }

    private BrowserExtensionViewModel? _browserTabs;

    /// <summary>Browser tabs with sound are listed under their browser's row (mute per tab; Chrome has no per-tab volume).</summary>
    public BrowserExtensionViewModel? BrowserTabs
    {
        get => _browserTabs;
        set
        {
            if (_browserTabs is not null)
            {
                _browserTabs.TabsChanged -= OnBrowserTabsChanged;
            }

            _browserTabs = value;
            if (value is not null)
            {
                value.TabsChanged += OnBrowserTabsChanged;
            }

            OnBrowserTabsChanged(this, EventArgs.Empty);
        }
    }

    private void OnBrowserTabsChanged(object? sender, EventArgs e)
    {
        foreach (var row in Apps)
        {
            row.SetTabs(_browserTabs?.SoundTabsOf(row.App.Key) ?? []);
        }
    }

    public ObservableCollection<AudioProfileViewModel> Profiles { get; } = [];

    public bool HasApps => Apps.Count > 0;

    public string SummaryText => Loc.F("Mixer.Summary", Apps.Count);

    public int PlayingCount => Apps.Count(a => a.IsPlaying);

    public string PlayingText => Loc.F("Mixer.PlayingNow", PlayingCount);

    /// <summary>The profile applied or saved last, until a volume is changed by hand.</summary>
    public AudioProfileViewModel? ActiveProfile { get; private set; }

    /// <summary>The profile chosen last (kept when volumes change), the target of "Save current mix".</summary>
    public AudioProfileViewModel? SelectedProfile { get; private set; }

    /// <summary>How the current mix relates to the selected profile, in plain words.</summary>
    public string SelectedStateText => SelectedProfile switch
    {
        null => Loc.T("Mixer.ChooseProfile"),
        { IsEmpty: true } => Loc.T("Mixer.ProfileEmpty"),
        { IsActive: true } => Loc.T("Mixer.StateApplied"),
        _ => Loc.T("Mixer.StateChanged"),
    };

    /// <summary>Where the floating mixer was left (device-independent units), null = default corner.</summary>
    public Point? FloatingPosition { get; private set; }

    /// <summary>Size the user gave the floating mixer by dragging its edges; null = automatic (fits its content).</summary>
    public Size? FloatingSize { get; private set; }

    /// <summary>The floating window was resized by the user (null = back to the automatic size); saved shortly after.</summary>
    public void ReportFloatingSize(Size? size)
    {
        if (FloatingSize == size)
        {
            return;
        }

        FloatingSize = size;
        RequestSave();
    }

    public const double MinFloatingOpacity = 0.3;

    /// <summary>Opacity of the floating mixer (0.3..1), so a game stays visible through it.</summary>
    public double FloatingOpacity
    {
        get => _floatingOpacity;
        set
        {
            if (SetProperty(ref _floatingOpacity, Math.Clamp(value, MinFloatingOpacity, 1)))
            {
                OnPropertyChanged(nameof(FloatingOpacityPercent));
                RequestSave();
            }
        }
    }

    /// <summary>The same as a percentage, for the page slider (30..100).</summary>
    public double FloatingOpacityPercent
    {
        get => Math.Round(_floatingOpacity * 100);
        set => FloatingOpacity = value / 100;
    }

    // ----- Windows volume (master volume of the default output device) -----

    public bool HasMaster => _source.HasMaster;

    public string MasterName => _source.MasterDeviceName ?? string.Empty;

    /// <summary>The Windows volume, live (changes made in Windows show up here at once).</summary>
    public double MasterVolumePercent
    {
        get => Math.Round(Math.Max(0, _source.MasterVolume) * 100);
        set
        {
            _source.MasterVolume = (float)(Math.Clamp(value, 0, 100) / 100);
            OnPropertyChanged();
        }
    }

    public bool MasterMuted
    {
        get => _source.MasterMuted;
        set
        {
            if (value == _source.MasterMuted)
            {
                return;
            }

            _source.MasterMuted = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(MasterMuteGlyph));
        }
    }

    public string MasterMuteGlyph => MasterMuted ? "" : "";

    public double MasterPeak => _source.MasterPeak;

    public ICommand ToggleMasterMuteCommand { get; }

    /// <summary>The Smart Audio Mixer page is the current page.</summary>
    public bool IsPageVisible
    {
        get => _isPageVisible;
        set
        {
            if (SetProperty(ref _isPageVisible, value))
            {
                UpdateActive();
            }
        }
    }

    public bool IsFloatingShown
    {
        get => _isFloatingShown;
        set
        {
            if (!SetProperty(ref _isFloatingShown, value))
            {
                return;
            }

            if (value)
            {
                _presenter.Show(this);
            }
            else
            {
                _presenter.Hide();
            }

            OnPropertyChanged(nameof(FloatingButtonText));
            UpdateActive();
        }
    }

    public string FloatingButtonText => Loc.T(IsFloatingShown ? "Mixer.FloatingHide" : "Mixer.FloatingShow");

    public string NewProfileName
    {
        get => _newProfileName;
        set => SetProperty(ref _newProfileName, value ?? string.Empty);
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set => SetProperty(ref _errorMessage, value);
    }

    public ICommand ToggleFloatingCommand { get; }
    public ICommand HideFloatingCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand CreateProfileCommand { get; }

    /// <summary>Hotkey and page button: show or hide the floating mixer.</summary>
    public void ToggleFloating() => IsFloatingShown = !IsFloatingShown;

    /// <summary>The floating window moved (device-independent units); saved shortly after.</summary>
    public void ReportFloatingPosition(Point topLeft)
    {
        if (FloatingPosition == topLeft)
        {
            return;
        }

        FloatingPosition = topLeft;
        RequestSave();
    }

    internal void Apply(AudioProfileViewModel profile)
    {
        Select(profile);
        if (profile.IsEmpty)
        {
            // Nothing to apply yet: the profile is selected, so "Save current mix" fills it.
            _activity.Add("", "Mixer.AppliedEmptyTitle", "Mixer.AppliedEmpty", profile.Name);
            return;
        }

        var result = AudioProfiles.Apply(profile.Profile, Apps.Select(a => a.App));
        SetActive(profile);
        if (result.NotRunning.Count == 0)
        {
            _activity.Add("", "Mixer.Applied", "Mixer.AppliedDetail", profile.Name, result.Applied);
        }
        else
        {
            _activity.Add("", "Mixer.Applied", "Mixer.AppliedSkipped", profile.Name, result.Applied, string.Join(", ", result.NotRunning));
        }
    }

    internal void SaveInto(AudioProfileViewModel profile)
    {
        var entries = AudioProfiles.Snapshot(Apps.Select(a => a.App));
        if (entries.Count == 0)
        {
            _activity.Add("", "Mixer.SavedNothingTitle", "Mixer.SavedNothing", profile.Name);
            return;
        }

        profile.Profile.Entries = entries;
        profile.Refresh();
        Select(profile);
        SetActive(profile);
        _activity.Add("", "Mixer.Saved", "Mixer.SavedDetail", profile.Name, entries.Count);
        SaveNow();
    }

    internal void Delete(AudioProfileViewModel profile)
    {
        if (!profile.IsCustom || !Profiles.Remove(profile))
        {
            return;
        }

        if (ActiveProfile == profile)
        {
            SetActive(null);
        }

        if (SelectedProfile == profile)
        {
            Select(Profiles.FirstOrDefault());
        }

        _activity.Add("", "Mixer.Deleted", "Mixer.NameDetail", profile.Name);
        SaveNow();
    }

    /// <summary>A new custom profile holding the current mix.</summary>
    public AudioProfileViewModel? CreateProfile()
    {
        var name = NewProfileName.Trim();
        if (name.Length == 0)
        {
            return null;
        }

        var profile = new AudioProfileViewModel(new AudioProfile
        {
            Kind = AudioProfileKind.Custom,
            Name = name,
            Entries = AudioProfiles.Snapshot(Apps.Select(a => a.App)),
        }, this);
        Profiles.Add(profile);
        Select(profile);
        SetActive(profile);
        NewProfileName = string.Empty;
        _activity.Add("", "Mixer.Created", "Mixer.CreatedDetail", name, profile.Profile.Entries.Count);
        SaveNow();
        return profile;
    }

    public void SaveNow()
    {
        _saveTimer.Stop();
        try
        {
            JsonStore.Save(FileName, new AudioMixerFile
            {
                Profiles = Profiles.Select(p => p.Profile).ToList(),
                MixerLeft = FloatingPosition?.X,
                MixerTop = FloatingPosition?.Y,
                MixerWidth = FloatingSize?.Width,
                MixerHeight = FloatingSize?.Height,
                MixerOpacity = _floatingOpacity < 1 ? _floatingOpacity : null,
                Focus = Focus.Settings,
            });
            ErrorMessage = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ErrorMessage = Loc.F("Mixer.SaveFailed", ex.Message);
        }
    }

    /// <summary>VIZEN exits: save, close the floating window, stop watching sessions.</summary>
    public void Shutdown()
    {
        if (_saveTimer.IsEnabled)
        {
            SaveNow();
        }

        Focus.Shutdown(); // lowered apps get their own volume back
        _presenter.Close();
        _source.SetMixerActive(false);
        foreach (var app in Apps)
        {
            app.Detach();
        }
    }

    private void RequestSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void UpdateActive() => _source.SetMixerActive(_isPageVisible || _isFloatingShown);

    private void SetActive(AudioProfileViewModel? profile)
    {
        if (ActiveProfile is not null)
        {
            ActiveProfile.IsActive = false;
        }

        ActiveProfile = profile;
        if (profile is not null)
        {
            profile.IsActive = true;
        }

        OnPropertyChanged(nameof(ActiveProfile));
        OnPropertyChanged(nameof(SelectedStateText));
    }

    private void Select(AudioProfileViewModel? profile)
    {
        if (SelectedProfile is not null)
        {
            SelectedProfile.IsSelected = false;
            SelectedProfile.IsConfirmingDelete = false;
        }

        SelectedProfile = profile;
        if (profile is not null)
        {
            profile.IsSelected = true;
        }

        OnPropertyChanged(nameof(SelectedProfile));
        OnPropertyChanged(nameof(SelectedStateText));
    }

    private void OnRowChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MixerAppViewModel.IsPlaying))
        {
            OnPropertyChanged(nameof(PlayingCount));
            OnPropertyChanged(nameof(PlayingText));
        }
    }

    /// <summary>Keeps one row per app, in the source's order, reusing rows that stay.</summary>
    private void SyncApps()
    {
        var current = _source.MixerApps;
        foreach (var row in Apps.Where(r => !current.Contains(r.App)).ToList())
        {
            row.Detach();
            row.PropertyChanged -= OnRowChanged;
            Apps.Remove(row);
        }

        for (var i = 0; i < current.Count; i++)
        {
            var existing = Apps.FirstOrDefault(r => ReferenceEquals(r.App, current[i]));
            if (existing is null)
            {
                var row = new MixerAppViewModel(current[i], _source.IconFor, () => SetActive(null), Focus);
                row.PropertyChanged += OnRowChanged;
                row.SetTabs(_browserTabs?.SoundTabsOf(row.App.Key) ?? []);
                Apps.Insert(i, row);
            }
            else if (Apps.IndexOf(existing) != i)
            {
                Apps.Move(Apps.IndexOf(existing), i);
            }
        }

        OnPropertyChanged(nameof(HasApps));
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(PlayingCount));
        OnPropertyChanged(nameof(PlayingText));
    }
}
