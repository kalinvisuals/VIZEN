using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Input;
using VIZEN.App.Infrastructure;
using VIZEN.App.Localization;
using VIZEN.App.Services;

namespace VIZEN.App.ViewModels;

public sealed class DashboardViewModel : ObservableObject
{
    public DashboardViewModel(
        WindowSourcesViewModel sources,
        OverlaysViewModel overlays,
        ProfilesViewModel profiles,
        CallsViewModel calls,
        StudioViewModel studio,
        AppSettings settings,
        ActivityLog activity,
        Action<ObservableObject> navigate)
    {
        Sources = sources;
        Overlays = overlays;
        Profiles = profiles;
        Calls = calls;
        Studio = studio;
        Settings = settings;
        Activity = activity;

        // Overlay creation starts by picking a source in Window Sources.
        CreateOverlayCommand = new RelayCommand(() => navigate(sources));
        ViewSourcesCommand = new RelayCommand(() => navigate(sources));
        ViewOverlaysCommand = new RelayCommand(() => navigate(overlays));
        ViewProfilesCommand = new RelayCommand(() => navigate(profiles));
        ViewCallsCommand = new RelayCommand(() => navigate(calls));
        ViewStudioCommand = new RelayCommand(() => navigate(studio));
    }

    public WindowSourcesViewModel Sources { get; }
    public OverlaysViewModel Overlays { get; }
    public ProfilesViewModel Profiles { get; }
    public CallsViewModel Calls { get; }
    public StudioViewModel Studio { get; }
    public AppSettings Settings { get; }
    public ActivityLog Activity { get; }

    public ICommand CreateOverlayCommand { get; }
    public ICommand ViewSourcesCommand { get; }
    public ICommand ViewOverlaysCommand { get; }
    public ICommand ViewProfilesCommand { get; }
    public ICommand ViewCallsCommand { get; }
    public ICommand ViewStudioCommand { get; }

    public ICommand ScanCommand => Sources.RefreshCommand;

    public string Greeting => DateTime.Now.Hour switch
    {
        < 5 => Loc.T("Dashboard.GreetingEvening"),
        < 12 => Loc.T("Dashboard.GreetingMorning"),
        < 18 => Loc.T("Dashboard.GreetingAfternoon"),
        _ => Loc.T("Dashboard.GreetingEvening"),
    };

    public bool IsCaptureSupported => Overlays.IsCaptureSupported;
}

public sealed class SettingsViewModel : ObservableObject
{
    private readonly ActivityLog _activity;

    public SettingsViewModel(AppSettings settings, HotkeyService hotkeys, ActivityLog activity)
    {
        Settings = settings;
        _activity = activity;
        Hotkeys = hotkeys.Bindings.Select(b => new HotkeyRowViewModel(b, hotkeys, settings, activity)).ToList();
        Accents = ThemeManager.Accents.Keys.Select(name => new AccentOption(name, settings)).ToList();
        settings.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(AppSettings.Language):
                    OnPropertyChanged(nameof(IsEnglish));
                    OnPropertyChanged(nameof(IsRomanian));
                    break;
                case nameof(AppSettings.Theme):
                    OnPropertyChanged(nameof(IsThemeDark));
                    OnPropertyChanged(nameof(IsThemeLight));
                    OnPropertyChanged(nameof(IsThemeSystem));
                    break;
            }
        };
    }

    public bool IsThemeDark
    {
        get => Settings.Theme == ThemeManager.Dark;
        set { if (value) Settings.Theme = ThemeManager.Dark; }
    }

    public bool IsThemeLight
    {
        get => Settings.Theme == ThemeManager.Light;
        set { if (value) Settings.Theme = ThemeManager.Light; }
    }

    public bool IsThemeSystem
    {
        get => Settings.Theme == ThemeManager.System;
        set { if (value) Settings.Theme = ThemeManager.System; }
    }

    public IReadOnlyList<AccentOption> Accents { get; }

    /// <summary>Windows starts VIZEN at sign-in (registry Run entry for the current user).</summary>
    public bool LaunchAtSignIn
    {
        get => StartupRegistration.IsEnabled;
        set
        {
            var error = StartupRegistration.Set(value);
            if (error is null)
            {
                _activity.Add("\uE7E8", "Activity.SettingChanged", "Activity.SettingChangedDetail",
                    Loc.T("Settings.LaunchAtSignIn"), Loc.T(value ? "Common.On" : "Common.Off"),
                    Loc.T(value ? "Activity.LaunchOnEffect" : "Activity.LaunchOffEffect"));
            }
            else
            {
                _activity.Add("\uE7BA", "Activity.LaunchFailed", ActivityLog.RawDetail, error);
            }

            OnPropertyChanged();
        }
    }

    public AppSettings Settings { get; }

    public IReadOnlyList<HotkeyRowViewModel> Hotkeys { get; }

    // Bound to the language radio buttons. Only "checked" is acted on; unchecking happens implicitly.
    public bool IsEnglish
    {
        get => Settings.Language == "en";
        set
        {
            if (value)
            {
                Settings.Language = "en";
            }
        }
    }

    public bool IsRomanian
    {
        get => Settings.Language == "ro";
        set
        {
            if (value)
            {
                Settings.Language = "ro";
            }
        }
    }

    public static string AppVersion { get; } =
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            .Split('+')[0] ?? "0.0.0";

    /// <summary>"Version 0.3.0".</summary>
    public string VersionText => Loc.F("Shell.Version", AppVersion);
}

/// <summary>One built-in shortcut in Settings: shows its status and lets the user change or reset it.</summary>
public sealed class HotkeyRowViewModel : ObservableObject
{
    private readonly HotkeyService _service;
    private readonly AppSettings _settings;
    private readonly ActivityLog _activity;

    public HotkeyRowViewModel(HotkeyBinding binding, HotkeyService service, AppSettings settings, ActivityLog activity)
    {
        Binding = binding;
        _service = service;
        _settings = settings;
        _activity = activity;
        ResetCommand = new RelayCommand(() => GestureText = binding.DefaultGesture.ToString(), () => !binding.IsDefault);
        binding.PropertyChanged += (_, _) => OnPropertyChanged(string.Empty);
    }

    public HotkeyBinding Binding { get; }

    public string Name => Binding.Name;

    public string StatusText => Binding.StatusText;

    public bool IsRegistered => Binding.IsRegistered;

    public bool IsConflict => Binding.IsConflict;

    /// <summary>Bound to the shortcut picker; an empty value restores the default.</summary>
    public string GestureText
    {
        get => Binding.Gesture;
        set
        {
            var text = string.IsNullOrWhiteSpace(value) ? Binding.DefaultGesture.ToString() : value;
            if (!Models.HotkeyGesture.TryParse(text, out var gesture) || gesture == Binding.Current)
            {
                OnPropertyChanged();
                return;
            }

            var status = _service.ChangeGesture(Binding, gesture);
            _settings.SetHotkey(Binding.Action.ToString(), Binding.IsDefault ? null : Binding.Gesture);
            _activity.Add("\uE765", "Activity.HotkeyChanged",
                status == HotkeyStatus.Conflict ? "Activity.HotkeyChangedConflict" : "Activity.HotkeyChangedDetail",
                Binding.Name, Binding.Gesture, Binding.StatusText);
            OnPropertyChanged();
        }
    }

    public ICommand ResetCommand { get; }
}
/// <summary>One accent colour swatch in Settings.</summary>
public sealed class AccentOption : ObservableObject
{
    private readonly AppSettings _settings;

    public AccentOption(string name, AppSettings settings)
    {
        Name = name;
        _settings = settings;
        var (accent, second) = ThemeManager.Accents[name];
        Swatch = new System.Windows.Media.LinearGradientBrush(accent, second, 45);
        Swatch.Freeze();
        SelectCommand = new RelayCommand(() => _settings.Accent = name);
        settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AppSettings.Accent))
            {
                OnPropertyChanged(nameof(IsSelected));
            }
        };
    }

    public string Name { get; }

    public string DisplayName => Loc.T("Accent." + Name);

    public System.Windows.Media.Brush Swatch { get; }

    public bool IsSelected => _settings.Accent == Name;

    public ICommand SelectCommand { get; }
}