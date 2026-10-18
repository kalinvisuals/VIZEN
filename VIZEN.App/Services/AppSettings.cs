using System.IO;
using VIZEN.App.Infrastructure;
using VIZEN.App.Localization;

namespace VIZEN.App.Services;

/// <summary>
/// User preferences that are wired up. Persisted to settings.json in the data folder.
/// </summary>
public sealed class AppSettings : ObservableObject
{
    private const string FileName = "settings.json";

    private bool _excludeOwnWindows = true;
    private bool _includeMinimizedWindows = true;
    private bool _hotkeysEnabled = true;
    private bool _autoRefreshSources = true;
    private string _language = Loc.DefaultLanguage;
    private string _theme = ThemeManager.Dark;
    private string _accent = "red";
    private bool _runInTray;
    private bool _autoPerformance = true;
    private bool _allowFpsReduction;
    private bool _gpuRendering = true;
    private bool _showFullscreenTip = true;
    private bool _autoCheckUpdates = true;
    private bool _autoDownloadUpdates;
    private string? _saveErrorDetail;
    private readonly HashSet<string> _keepActiveApps = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _hotkeys = new(StringComparer.OrdinalIgnoreCase);

    public bool ExcludeOwnWindows
    {
        get => _excludeOwnWindows;
        set => SetProperty(ref _excludeOwnWindows, value);
    }

    public bool IncludeMinimizedWindows
    {
        get => _includeMinimizedWindows;
        set => SetProperty(ref _includeMinimizedWindows, value);
    }

    public bool HotkeysEnabled
    {
        get => _hotkeysEnabled;
        set => SetProperty(ref _hotkeysEnabled, value);
    }

    /// <summary>Re-scan windows every few seconds while VIZEN is running.</summary>
    public bool AutoRefreshSources
    {
        get => _autoRefreshSources;
        set => SetProperty(ref _autoRefreshSources, value);
    }

    /// <summary>UI language code ("en" or "ro").</summary>
    public string Language
    {
        get => _language;
        set => SetProperty(ref _language, Loc.Normalize(value));
    }

    /// <summary>"dark", "light" or "system" (follow Windows).</summary>
    public string Theme
    {
        get => _theme;
        set => SetProperty(ref _theme, ThemeManager.NormalizeTheme(value));
    }

    /// <summary>Accent colour name ("red", "blue", "teal", "rose", "amber").</summary>
    public string Accent
    {
        get => _accent;
        set => SetProperty(ref _accent, ThemeManager.NormalizeAccent(value));
    }

    /// <summary>Closing the window keeps VIZEN running in the notification area (overlays, schedules continue).</summary>
    public bool RunInTray
    {
        get => _runInTray;
        set => SetProperty(ref _runInTray, value);
    }

    /// <summary>Watch every capture and report (and, if allowed, relieve) bottlenecks.</summary>
    public bool AutoPerformance
    {
        get => _autoPerformance;
        set => SetProperty(ref _autoPerformance, value);
    }

    /// <summary>Auto Performance may lower a struggling capture to 30 fps. Off by default: never reduce quality silently.</summary>
    public bool AllowFpsReduction
    {
        get => _allowFpsReduction;
        set => SetProperty(ref _allowFpsReduction, value);
    }

    /// <summary>Show window captures through the GPU (no copy through system memory). Applies to captures started afterwards.</summary>
    public bool GpuRendering
    {
        get => _gpuRendering;
        set => SetProperty(ref _gpuRendering, value);
    }

    /// <summary>Show the hint to switch a game from exclusive fullscreen to borderless when overlays cannot appear over it.</summary>
    public bool ShowFullscreenTip
    {
        get => _showFullscreenTip;
        set => SetProperty(ref _showFullscreenTip, value);
    }

    /// <summary>Apps (executable path) whose minimized windows VIZEN restores behind the active window.</summary>
    /// <summary>Check for a new VIZEN at startup and every few hours (a few kilobytes; never installs by itself).</summary>
    public bool AutoCheckUpdates
    {
        get => _autoCheckUpdates;
        set => SetProperty(ref _autoCheckUpdates, value);
    }

    /// <summary>Also download a found update in the background, so installing it is immediate. Off by default.</summary>
    public bool AutoDownloadUpdates
    {
        get => _autoDownloadUpdates;
        set => SetProperty(ref _autoDownloadUpdates, value);
    }

    public IReadOnlyCollection<string> KeepActiveApps => _keepActiveApps;

    public bool IsKeepActive(string appKey) => _keepActiveApps.Contains(appKey);

    public void SetKeepActive(string appKey, bool enabled)
    {
        if (enabled ? _keepActiveApps.Add(appKey) : _keepActiveApps.Remove(appKey))
        {
            OnPropertyChanged(nameof(KeepActiveApps));
        }
    }

    /// <summary>Custom gesture text for a built-in hotkey action, or null for the default.</summary>
    public string? GetHotkey(string action) => _hotkeys.TryGetValue(action, out var gesture) ? gesture : null;

    public void SetHotkey(string action, string? gesture)
    {
        var changed = gesture is null ? _hotkeys.Remove(action) : !_hotkeys.TryGetValue(action, out var old) || old != gesture;
        if (gesture is not null)
        {
            _hotkeys[action] = gesture;
        }

        if (changed)
        {
            OnPropertyChanged(nameof(GetHotkey));
        }
    }

    /// <summary>Last save failure (localized), or null.</summary>
    public string? SaveError => _saveErrorDetail is null ? null : Loc.F("Settings.SaveError", _saveErrorDetail);

    /// <summary>Loads saved settings (defaults if none) and saves automatically on every change.</summary>
    public static AppSettings LoadAndAutoSave(out string? loadError)
    {
        var settings = new AppSettings();
        var data = JsonStore.Load<SettingsFile>(FileName, out loadError);
        if (data is not null)
        {
            settings._excludeOwnWindows = data.ExcludeOwnWindows;
            settings._includeMinimizedWindows = data.IncludeMinimizedWindows;
            settings._hotkeysEnabled = data.HotkeysEnabled;
            settings._autoRefreshSources = data.AutoRefreshSources;
            settings._language = Loc.Normalize(data.Language);
            settings._theme = ThemeManager.NormalizeTheme(data.Theme);
            settings._accent = ThemeManager.NormalizeAccent(data.Accent);
            settings._runInTray = data.RunInTray;
            settings._autoPerformance = data.AutoPerformance;
            settings._allowFpsReduction = data.AllowFpsReduction;
            settings._gpuRendering = data.GpuRendering;
            settings._showFullscreenTip = data.ShowFullscreenTip;
            settings._autoCheckUpdates = data.AutoCheckUpdates;
            settings._autoDownloadUpdates = data.AutoDownloadUpdates;
            settings._keepActiveApps.UnionWith(data.KeepActiveApps ?? []);
            foreach (var (action, gesture) in data.Hotkeys ?? [])
            {
                settings._hotkeys[action] = gesture;
            }
        }

        settings.PropertyChanged += (_, e) =>
        {
            // Empty name = "refresh everything" (e.g. language switch); only save on real setting changes.
            if (!string.IsNullOrEmpty(e.PropertyName) && e.PropertyName != nameof(SaveError))
            {
                settings.Save();
            }
        };
        return settings;
    }

    private void Save()
    {
        try
        {
            JsonStore.Save(FileName, new SettingsFile
            {
                ExcludeOwnWindows = ExcludeOwnWindows,
                IncludeMinimizedWindows = IncludeMinimizedWindows,
                HotkeysEnabled = HotkeysEnabled,
                AutoRefreshSources = AutoRefreshSources,
                Language = Language,
                Theme = Theme,
                Accent = Accent,
                RunInTray = RunInTray,
                AutoPerformance = AutoPerformance,
                AllowFpsReduction = AllowFpsReduction,
                GpuRendering = GpuRendering,
                ShowFullscreenTip = ShowFullscreenTip,
                AutoCheckUpdates = AutoCheckUpdates,
                AutoDownloadUpdates = AutoDownloadUpdates,
                KeepActiveApps = [.. _keepActiveApps],
                Hotkeys = new Dictionary<string, string>(_hotkeys),
            });
            _saveErrorDetail = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _saveErrorDetail = ex.Message;
        }

        OnPropertyChanged(nameof(SaveError));
    }

    private sealed class SettingsFile
    {
        public int Version { get; set; } = 1;
        public bool ExcludeOwnWindows { get; set; } = true;
        public bool IncludeMinimizedWindows { get; set; } = true;
        public bool HotkeysEnabled { get; set; } = true;
        public bool AutoRefreshSources { get; set; } = true;
        public string? Language { get; set; }
        public string? Theme { get; set; }
        public string? Accent { get; set; }
        public bool RunInTray { get; set; }
        public bool AutoPerformance { get; set; } = true;
        public bool AllowFpsReduction { get; set; }
        public bool GpuRendering { get; set; } = true;
        public bool ShowFullscreenTip { get; set; } = true;
        public bool AutoCheckUpdates { get; set; } = true;
        public bool AutoDownloadUpdates { get; set; }
        public List<string>? KeepActiveApps { get; set; }
        public Dictionary<string, string>? Hotkeys { get; set; }
    }
}
