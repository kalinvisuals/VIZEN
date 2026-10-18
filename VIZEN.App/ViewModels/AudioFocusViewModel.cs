using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using VIZEN.App.Infrastructure;
using VIZEN.App.Localization;
using VIZEN.App.Models;
using VIZEN.App.Services;

namespace VIZEN.App.ViewModels;

/// <summary>An app offered as the priority app or as an app to lower (a pill that can be selected).</summary>
public sealed class FocusChoiceViewModel : ObservableObject
{
    private readonly Action<FocusChoiceViewModel> _toggle;
    private bool _isSelected;

    public FocusChoiceViewModel(string app, string name, bool isRunning, Action<FocusChoiceViewModel> toggle)
    {
        App = app;
        Name = name;
        IsRunning = isRunning;
        _toggle = toggle;
        ToggleCommand = new RelayCommand(() => _toggle(this));
    }

    public string App { get; }

    public string Name { get; }

    /// <summary>The app has sound in Windows right now (a saved choice that is not running is shown dimmed).</summary>
    public bool IsRunning { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public ICommand ToggleCommand { get; }
}

/// <summary>An app in the Audio Focus diagram (icon and name).</summary>
public sealed record FocusAppPreview(string Name, System.Windows.Media.ImageSource? Icon)
{
    public bool HasIcon => Icon is not null;
}

/// <summary>
/// Smart Audio Focus: when the priority app (for example Discord) produces sound above the sensitivity threshold, the
/// affected apps (for example Chrome, Spotify) are lowered smoothly and come back after a quiet hold. Works on the
/// sound level only, not on speech. Runs on the audio service's meter ticks while it is on, also when the mixer is
/// not on screen. Each lowered app keeps its own volume ("base"); a volume set in VIZEN while lowered becomes the new
/// base; everything is put back on Restore, when Audio Focus is turned off and when VIZEN exits. While apps are
/// lowered their own volumes are also kept in a small restore file, so they come back after an unexpected exit.
/// </summary>
public sealed class AudioFocusViewModel : ObservableObject
{
    public const string RestoreFileName = "audio-focus-restore.json";

    private readonly IMixerSource _source;
    private readonly ActivityLog _activity;
    private readonly Action _requestSave;
    private readonly Action _refreshRows;
    private string _statusText = string.Empty;
    private bool _isDucking;
    private double _priorityLevel;

    public AudioFocusViewModel(IMixerSource source, ActivityLog activity, AudioFocusSettings? settings, Action requestSave, Action refreshRows)
    {
        _source = source;
        _activity = activity;
        _requestSave = requestSave;
        _refreshRows = refreshRows;
        Settings = settings ?? new AudioFocusSettings();
        Settings.Sanitize();
        Engine = new AudioFocusEngine();
        Engine.LoweredChanged += (_, _) =>
        {
            WriteRestoreFile();
            _refreshRows();
        };
        RestoreCommand = new RelayCommand(Restore);
        AllOthersCommand = new RelayCommand(() => AllOthers = !AllOthers);

        RestoreAfterUnexpectedExit();
        _source.MixerAppsChanged += (_, _) =>
        {
            RebuildChoices();
            UpdateStatus();
        };
        _source.MeterTick += OnMeterTick;
        RebuildChoices();
        UpdateStatus();
        if (Settings.Enabled)
        {
            _source.SetFocusActive(true);
        }
    }

    public AudioFocusSettings Settings { get; }

    public AudioFocusEngine Engine { get; }

    public ObservableCollection<FocusChoiceViewModel> PriorityChoices { get; } = [];

    public ObservableCollection<FocusChoiceViewModel> DuckChoices { get; } = [];

    public bool HasChoices => PriorityChoices.Count > 0;

    public bool IsEnabled
    {
        get => Settings.Enabled;
        set
        {
            if (Settings.Enabled == value)
            {
                return;
            }

            Settings.Enabled = value;
            _source.SetFocusActive(value);
            if (!value)
            {
                Engine.RestoreAll();
            }

            OnPropertyChanged();
            UpdateStatus();
            _requestSave();
            _activity.Add("", value ? "Focus.On" : "Focus.Off", value ? "Focus.OnDetail" : "Focus.OffDetail", PriorityName);
        }
    }

    public string PriorityName => Settings.Priority?.Name ?? Loc.T("Focus.NoPriority");

    public bool AllOthers
    {
        get => Settings.AllOthers;
        set
        {
            if (Settings.AllOthers == value)
            {
                return;
            }

            Settings.AllOthers = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsChoosingApps));
            _requestSave();
            UpdateStatus();
        }
    }

    /// <summary>The individual app pills are used (not "all other apps").</summary>
    public bool IsChoosingApps => !Settings.AllOthers;

    public double DuckPercent
    {
        get => Settings.DuckPercent;
        set => SetSetting(() => Settings.DuckPercent = (int)Math.Round(value), nameof(DuckPercent), nameof(DuckText));
    }

    public string DuckText => Loc.F("Focus.DuckValue", Settings.DuckPercent, 100 - Settings.DuckPercent);

    public double Sensitivity
    {
        get => Settings.Sensitivity;
        set => SetSetting(() => Settings.Sensitivity = (int)Math.Round(value), nameof(Sensitivity), nameof(ThresholdFraction), nameof(ThresholdLeft), nameof(ThresholdRight), nameof(SensitivityText));
    }

    /// <summary>The sensitivity in one plain word.</summary>
    public string SensitivityText => Loc.T(Settings.Sensitivity < 34 ? "Focus.SensitivityLoud" : Settings.Sensitivity < 67 ? "Focus.SensitivityNormal" : "Focus.SensitivityQuiet");

    /// <summary>Where the activation threshold sits on the priority level meter (0..1).</summary>
    public double ThresholdFraction => AudioFocusSettings.ThresholdFor(Settings.Sensitivity);

    /// <summary>The meter's two parts left and right of the threshold marker (star widths).</summary>
    public System.Windows.GridLength ThresholdLeft => new(Math.Max(0.001, ThresholdFraction), System.Windows.GridUnitType.Star);

    public System.Windows.GridLength ThresholdRight => new(Math.Max(0.001, 1 - ThresholdFraction), System.Windows.GridUnitType.Star);

    public double AttackMs
    {
        get => Settings.AttackMs;
        set => SetSetting(() => Settings.AttackMs = (int)Math.Round(value / 10) * 10, nameof(AttackMs));
    }

    public double HoldMs
    {
        get => Settings.HoldMs;
        set => SetSetting(() => Settings.HoldMs = (int)Math.Round(value / 50) * 50, nameof(HoldMs));
    }

    public double ReleaseMs
    {
        get => Settings.ReleaseMs;
        set => SetSetting(() => Settings.ReleaseMs = (int)Math.Round(value / 50) * 50, nameof(ReleaseMs));
    }

    /// <summary>Live level of the priority app (0..1), shown with the threshold marker.</summary>
    public double PriorityLevel
    {
        get => _priorityLevel;
        private set => SetProperty(ref _priorityLevel, value);
    }

    /// <summary>The affected apps are lowered (or coming back) right now.</summary>
    public bool IsDucking
    {
        get => _isDucking;
        private set => SetProperty(ref _isDucking, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public ICommand RestoreCommand { get; }

    public ICommand AllOthersCommand { get; }

    // ----- Simple controls: how fast it reacts (presets), advanced timings on request -----

    private static readonly (int Attack, int Hold, int Release)[] Presets = [(100, 400, 600), (250, 800, 1200), (600, 1500, 2500)];
    private bool _showAdvanced;

    /// <summary>0 = quick, 1 = normal, 2 = smooth, -1 = custom timings (set under Advanced).</summary>
    public int Reaction
    {
        get
        {
            for (var i = 0; i < Presets.Length; i++)
            {
                if (Presets[i] == (Settings.AttackMs, Settings.HoldMs, Settings.ReleaseMs))
                {
                    return i;
                }
            }

            return -1;
        }
    }

    public bool IsQuick => Reaction == 0;

    public bool IsNormal => Reaction == 1;

    public bool IsSmooth => Reaction == 2;

    public bool IsCustomReaction => Reaction == -1;

    public ICommand SetReactionCommand => new RelayCommand<string>(index =>
    {
        var (attack, hold, release) = Presets[int.Parse(index, System.Globalization.CultureInfo.InvariantCulture)];
        SetSetting(() => (Settings.AttackMs, Settings.HoldMs, Settings.ReleaseMs) = (attack, hold, release),
            nameof(AttackMs), nameof(HoldMs), nameof(ReleaseMs));
    });

    /// <summary>The exact timings (in milliseconds) are shown.</summary>
    public bool ShowAdvanced
    {
        get => _showAdvanced;
        set => SetProperty(ref _showAdvanced, value);
    }

    public ICommand ToggleAdvancedCommand => new RelayCommand(() => ShowAdvanced = !ShowAdvanced);

    private void RaiseReaction()
    {
        OnPropertyChanged(nameof(Reaction));
        OnPropertyChanged(nameof(IsQuick));
        OnPropertyChanged(nameof(IsNormal));
        OnPropertyChanged(nameof(IsSmooth));
        OnPropertyChanged(nameof(IsCustomReaction));
    }

    // ----- Diagram: priority app → apps that get quieter -----

    /// <summary>Icon of the priority app (null when it is not running or has none).</summary>
    public System.Windows.Media.ImageSource? PriorityIcon { get; private set; }

    public bool HasPriorityIcon => PriorityIcon is not null;

    /// <summary>The apps that would be lowered right now (icons for the diagram, at most six).</summary>
    public ObservableCollection<FocusAppPreview> AffectedPreview { get; } = [];

    /// <summary>"2 apps · to 40 %" / "All other apps · to 40 %".</summary>
    public string AffectedText => Settings.AllOthers
        ? Loc.F("Focus.AffectedAll", 100 - Settings.DuckPercent)
        : Loc.F("Focus.AffectedSome", AudioFocusEngine.AffectedApps(Settings, _source.MixerApps).Count(), 100 - Settings.DuckPercent);

    /// <summary>"−60 %" on the arrow between the two sides.</summary>
    public string DuckShortText => $"−{Settings.DuckPercent} %";

    private void RefreshDiagram()
    {
        var priority = AudioFocusEngine.PriorityApp(Settings, _source.MixerApps);
        PriorityIcon = priority is null ? null : _source.IconFor(priority);
        AffectedPreview.Clear();
        foreach (var app in AudioFocusEngine.AffectedApps(Settings, _source.MixerApps).Take(6))
        {
            AffectedPreview.Add(new FocusAppPreview(app.AppName, _source.IconFor(app)));
        }

        OnPropertyChanged(nameof(PriorityIcon));
        OnPropertyChanged(nameof(HasPriorityIcon));
        OnPropertyChanged(nameof(AffectedText));
        OnPropertyChanged(nameof(DuckShortText));
    }

    /// <summary>The app's own volume while Audio Focus holds it lowered (null when it is not lowered).</summary>
    public float? BaseFor(IMixerApp app) => Engine.BaseFor(app);

    /// <summary>A volume set in VIZEN for a lowered app becomes its own volume; false when the app is not lowered.</summary>
    public bool UserSetVolume(IMixerApp app, float volume) => Engine.UserSetVolume(app, volume);

    public void Restore()
    {
        var count = Engine.Lowered.Count;
        Engine.RestoreAll();
        UpdateStatus();
        _activity.Add("", "Focus.Restored", "Focus.RestoredDetail", count);
    }

    /// <summary>VIZEN exits: every lowered app gets its own volume back, the restore file goes.</summary>
    public void Shutdown()
    {
        _source.MeterTick -= OnMeterTick;
        Engine.RestoreAll();
        DeleteRestoreFile();
        _source.SetFocusActive(false);
    }

    private void OnMeterTick(object? sender, double elapsedMs)
    {
        if (!Settings.Enabled)
        {
            return;
        }

        Engine.Step(elapsedMs, Settings, _source.MixerApps);
        var priority = AudioFocusEngine.PriorityApp(Settings, _source.MixerApps);
        var level = priority?.LevelNow ?? 0;
        if (Math.Abs(level - _priorityLevel) > 0.004 || (level == 0 && _priorityLevel != 0))
        {
            PriorityLevel = level;
        }

        if (IsDucking != Engine.Ducker.IsDucking)
        {
            IsDucking = Engine.Ducker.IsDucking;
            UpdateStatus();
        }
    }

    private void Select(FocusChoiceViewModel choice)
    {
        Engine.RestoreAll(); // a different rule from now on: start from the apps' own volumes
        Settings.Priority = new AudioFocusApp { App = choice.App, Name = choice.Name };
        Settings.Ducked.RemoveAll(d => d.App == choice.App);
        _requestSave();
        RebuildChoices();
        OnPropertyChanged(nameof(PriorityName));
        UpdateStatus();
    }

    private void ToggleDucked(FocusChoiceViewModel choice)
    {
        var existing = Settings.Ducked.FirstOrDefault(d => d.App == choice.App);
        if (existing is null)
        {
            Settings.Ducked.Add(new AudioFocusApp { App = choice.App, Name = choice.Name });
        }
        else
        {
            Settings.Ducked.Remove(existing);
        }

        choice.IsSelected = existing is null;
        _requestSave();
        UpdateStatus();
    }

    /// <summary>The pills: every app with sound now, plus saved choices that are not running (dimmed).</summary>
    private void RebuildChoices()
    {
        var apps = _source.MixerApps.Where(a => !string.IsNullOrEmpty(a.Key) && !a.IsSystemSounds)
            .GroupBy(a => a.Key).Select(g => g.First()).ToList();
        // Two different apps with the same product name (e.g. two copies of one program) get their file name added.
        var duplicateNames = apps.GroupBy(a => a.AppName).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
        string NameOf(IMixerApp a) => duplicateNames.Contains(a.AppName) ? $"{a.AppName} ({a.Key})" : a.AppName;

        var priorityKeys = apps.Select(a => (a.Key, NameOf(a), true)).ToList();
        if (Settings.Priority is { } p && priorityKeys.All(k => k.Key != p.App))
        {
            priorityKeys.Add((p.App, p.Name, false));
        }

        Sync(PriorityChoices, priorityKeys, Select, c => c.App == Settings.Priority?.App);

        var duckKeys = apps.Where(a => a.Key != Settings.Priority?.App).Select(a => (a.Key, NameOf(a), true)).ToList();
        foreach (var d in Settings.Ducked.Where(d => duckKeys.All(k => k.Key != d.App)))
        {
            duckKeys.Add((d.App, d.Name, false));
        }

        Sync(DuckChoices, duckKeys, ToggleDucked, c => Settings.Ducked.Any(d => d.App == c.App));
        OnPropertyChanged(nameof(HasChoices));
    }

    private static void Sync(ObservableCollection<FocusChoiceViewModel> target, List<(string Key, string Name, bool Running)> wanted,
        Action<FocusChoiceViewModel> toggle, Func<FocusChoiceViewModel, bool> selected)
    {
        var same = target.Count == wanted.Count && target.Zip(wanted).All(p => p.First.App == p.Second.Key && p.First.IsRunning == p.Second.Running);
        if (!same)
        {
            target.Clear();
            foreach (var (key, name, running) in wanted)
            {
                target.Add(new FocusChoiceViewModel(key, name, running, toggle));
            }
        }

        foreach (var choice in target)
        {
            choice.IsSelected = selected(choice);
        }
    }

    private void SetSetting(Action set, params string[] properties)
    {
        set();
        Settings.Sanitize();
        foreach (var property in properties)
        {
            OnPropertyChanged(property);
        }

        RefreshDiagram();
        RaiseReaction();

        _requestSave();
    }

    private void UpdateStatus()
    {
        RefreshDiagram();
        var running = AudioFocusEngine.PriorityApp(Settings, _source.MixerApps) is not null;
        var affected = AudioFocusEngine.AffectedApps(Settings, _source.MixerApps).Count();
        StatusText = !Settings.Enabled ? Loc.T("Focus.StatusOff")
            : Settings.Priority is null ? Loc.T("Focus.StatusChoose")
            : !running ? Loc.F("Focus.StatusNotRunning", PriorityName)
            : Engine.Ducker.IsDucking ? Loc.F("Focus.StatusDucking", PriorityName, Engine.Lowered.Count, 100 - Settings.DuckPercent)
            : Loc.F("Focus.StatusWaiting", PriorityName, affected);
    }

    // ----- Restore file (volumes come back after an unexpected exit) -----

    private void WriteRestoreFile()
    {
        try
        {
            var entries = Engine.RestoreEntries();
            if (entries.Count == 0)
            {
                DeleteRestoreFile();
            }
            else
            {
                JsonStore.Save(RestoreFileName, entries);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: the volumes are still restored normally when the duck ends.
        }
    }

    private static void DeleteRestoreFile()
    {
        try
        {
            File.Delete(Path.Combine(JsonStore.DataDirectory, RestoreFileName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // nothing to clean up
        }
    }

    private void RestoreAfterUnexpectedExit()
    {
        if (!File.Exists(Path.Combine(JsonStore.DataDirectory, RestoreFileName)))
        {
            return;
        }

        var entries = JsonStore.Load<List<AudioFocusRestoreEntry>>(RestoreFileName, out _) ?? [];
        _source.RescanNow();
        var restored = AudioFocusEngine.RestoreFromFile(entries, _source.MixerApps);
        DeleteRestoreFile();
        if (entries.Count > 0)
        {
            _activity.Add("", "Focus.RestoredAfterExit", "Focus.RestoredAfterExitDetail", restored);
        }
    }
}
