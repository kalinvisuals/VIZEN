using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using VIZEN.App.Infrastructure;
using VIZEN.App.Localization;
using VIZEN.App.Media;
using VIZEN.App.Models;

namespace VIZEN.App.ViewModels;

/// <summary>One layer in a schedule's list, with an optional own duration.</summary>
public sealed class ScheduleEntryViewModel : ObservableObject
{
    private readonly ScheduleViewModel _owner;

    public ScheduleEntryViewModel(ScheduleEntry entry, ScheduleViewModel owner)
    {
        Entry = entry;
        _owner = owner;
        MoveUpCommand = new RelayCommand(() => owner.MoveEntry(this, -1));
        MoveDownCommand = new RelayCommand(() => owner.MoveEntry(this, 1));
        RemoveCommand = new RelayCommand(() => owner.RemoveEntry(this));
    }

    public ScheduleEntry Entry { get; }

    public MediaLayerViewModel? Layer => _owner.Library.Find(Entry.LayerId);

    public string Name => Layer?.Name ?? Loc.T("Schedule.MissingLayer");

    public string KindGlyph => Layer?.KindGlyph ?? "";

    /// <summary>Own duration in seconds; empty = the schedule's duration.</summary>
    public string SecondsText
    {
        get => Entry.Seconds is { } s ? s.ToString("0.#", CultureInfo.CurrentCulture) : string.Empty;
        set
        {
            Entry.Seconds = ScheduleViewModel.ParseNumber(value) is { } s && s > 0 ? Math.Min(s, 86400) : null;
            OnPropertyChanged();
            _owner.Changed();
        }
    }

    public ICommand MoveUpCommand { get; }
    public ICommand MoveDownCommand { get; }
    public ICommand RemoveCommand { get; }
}

/// <summary>
/// One schedule / sponsor playlist on the Schedules page: its settings, its layers, a live status line and
/// a preview of the next appearances. The <see cref="Runner"/> does the timing; the page applies its changes.
/// </summary>
public sealed class ScheduleViewModel : ObservableObject
{
    private readonly Action _save;
    private readonly Action<ScheduleViewModel> _hotkeyChanged;
    private string _statusText = string.Empty;
    private bool _isConfirmingDelete;
    private bool _isExpanded = true;

    public ScheduleViewModel(MediaSchedule schedule, MediaLibraryViewModel library, Action save, Action<ScheduleViewModel> hotkeyChanged)
    {
        Schedule = schedule;
        Library = library;
        _save = save;
        _hotkeyChanged = hotkeyChanged;
        Runner = new ScheduleRunner(schedule, MediaLength);
        foreach (var entry in schedule.Entries)
        {
            Entries.Add(new ScheduleEntryViewModel(entry, this));
        }

        AddLayerCommand = new RelayCommand<MediaLayerViewModel>(AddLayer);
        DeleteCommand = new RelayCommand(() => IsConfirmingDelete = true);
        CancelDeleteCommand = new RelayCommand(() => IsConfirmingDelete = false);
        ToggleExpandedCommand = new RelayCommand(() => IsExpanded = !IsExpanded);
        library.Items.CollectionChanged += OnLibraryChanged;
    }

    public MediaSchedule Schedule { get; }
    public MediaLibraryViewModel Library { get; }
    public ScheduleRunner Runner { get; }
    public Guid Id => Schedule.Id;

    public ObservableCollection<ScheduleEntryViewModel> Entries { get; } = [];

    public bool HasEntries => Entries.Count > 0;

    /// <summary>Library layers not yet in this schedule.</summary>
    public IReadOnlyList<MediaLayerViewModel> AvailableLayers =>
        Library.Items.Where(l => Schedule.Entries.All(e => e.LayerId != l.Id)).ToList();

    // ----- Settings -----

    public string Name
    {
        get => Schedule.Name;
        set
        {
            Schedule.Name = string.IsNullOrWhiteSpace(value) ? Schedule.Name : value.Trim();
            OnPropertyChanged();
            Changed();
        }
    }

    public bool IsPermanent { get => Schedule.Mode == ScheduleMode.Permanent; set => SetMode(value, ScheduleMode.Permanent); }
    public bool IsOnce { get => Schedule.Mode == ScheduleMode.Once; set => SetMode(value, ScheduleMode.Once); }
    public bool IsRepeat { get => Schedule.Mode == ScheduleMode.Repeat; set => SetMode(value, ScheduleMode.Repeat); }

    public bool IsSequential { get => Schedule.Order == ScheduleOrder.Sequential; set => SetOrder(value, ScheduleOrder.Sequential); }
    public bool IsRandom { get => Schedule.Order == ScheduleOrder.Random; set => SetOrder(value, ScheduleOrder.Random); }

    public string StartDelayText
    {
        get => Format(Schedule.StartDelaySeconds);
        set => SetNumber(value, 0, 86400, v => Schedule.StartDelaySeconds = v);
    }

    /// <summary>Seconds each appearance lasts; 0 = the media's own length.</summary>
    public string VisibleSecondsText
    {
        get => Format(Schedule.VisibleSeconds);
        set => SetNumber(value, 0, 86400, v => Schedule.VisibleSeconds = v);
    }

    /// <summary>Repeat interval in minutes (decimals allowed: 0.5 = 30 s).</summary>
    public string IntervalMinutesText
    {
        get => Format(Schedule.IntervalSeconds / 60);
        set => SetNumber(value, 1.0 / 60, 24 * 60, v => Schedule.IntervalSeconds = Math.Round(v * 60));
    }

    public string ActiveFrom
    {
        get => Schedule.ActiveFrom ?? string.Empty;
        set => SetTime(value, v => Schedule.ActiveFrom = v);
    }

    public string ActiveUntil
    {
        get => Schedule.ActiveUntil ?? string.Empty;
        set => SetTime(value, v => Schedule.ActiveUntil = v);
    }

    public string Hotkey
    {
        get => Schedule.Hotkey ?? string.Empty;
        set
        {
            Schedule.Hotkey = HotkeyGesture.TryParse(value, out var g) ? g.ToString() : null;
            OnPropertyChanged();
            _hotkeyChanged(this);
            Changed();
        }
    }

    private string _hotkeyStatus = string.Empty;

    public string HotkeyStatusText
    {
        get => _hotkeyStatus;
        internal set => SetProperty(ref _hotkeyStatus, value);
    }

    public bool AutoStart
    {
        get => Schedule.AutoStart;
        set
        {
            Schedule.AutoStart = value;
            OnPropertyChanged();
            Changed();
        }
    }

    // ----- Live state -----

    public bool IsRunning => Runner.IsRunning;
    public bool IsPaused => Runner.State == RunnerState.Paused;
    public bool CanStart => !Runner.IsRunning && HasEntries;

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public IReadOnlyList<string> PreviewLines { get; private set; } = [];

    public bool IsConfirmingDelete
    {
        get => _isConfirmingDelete;
        set => SetProperty(ref _isConfirmingDelete, value);
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    // Set by the Schedules page (they apply layer changes and log).
    public ICommand? StartCommand { get; internal set; }
    public ICommand? PauseCommand { get; internal set; }
    public ICommand? StopCommand { get; internal set; }
    public ICommand? TriggerCommand { get; internal set; }
    public ICommand? ConfirmDeleteCommand { get; internal set; }

    public ICommand AddLayerCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand CancelDeleteCommand { get; }
    public ICommand ToggleExpandedCommand { get; }

    /// <summary>Refreshes the status line (called a few times per second by the page).</summary>
    public void RefreshStatus(DateTime now)
    {
        StatusText = Runner.State switch
        {
            RunnerState.Showing when Runner.NextChangeAt is { } end =>
                Loc.F("Schedule.StatusShowing", LayerName(Runner.Current), Remaining(end - now)),
            RunnerState.Showing => Loc.F("Schedule.StatusShowingPermanent", LayerName(Runner.Current)),
            RunnerState.Waiting when !Runner.IsInWindow(now) => Loc.F("Schedule.StatusOutsideWindow", ActiveFrom, ActiveUntil),
            RunnerState.Waiting when Runner.NextChangeAt is { } next => Loc.F("Schedule.StatusWaiting", Remaining(next - now)),
            RunnerState.Paused => Loc.T("Schedule.StatusPaused"),
            RunnerState.Finished => Loc.T("Schedule.StatusFinished"),
            _ => HasEntries ? Loc.T("Schedule.StatusStopped") : Loc.T("Schedule.StatusEmpty"),
        };
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(IsPaused));
        OnPropertyChanged(nameof(CanStart));
    }

    /// <summary>Recomputes the "next appearances" list from the current settings.</summary>
    public void RefreshPreview(DateTime now)
    {
        PreviewLines = !HasEntries
            ? []
            : ScheduleRunner.Preview(Schedule, MediaLength, now, 5)
                .Select(p => Loc.F("Schedule.PreviewLine", p.At.ToString("HH:mm:ss"), LayerName(p.LayerId),
                    p.Duration is { } d ? Loc.F("Schedule.PreviewFor", Remaining(d)) : Loc.T("Schedule.PreviewPermanent")))
                .ToList();
        OnPropertyChanged(nameof(PreviewLines));
    }

    internal void Changed()
    {
        _save();
        RefreshPreview(DateTime.Now);
        OnPropertyChanged(nameof(HasEntries));
        OnPropertyChanged(nameof(CanStart));
    }

    internal void MoveEntry(ScheduleEntryViewModel entry, int direction)
    {
        var index = Entries.IndexOf(entry);
        var target = index + direction;
        if (index < 0 || target < 0 || target >= Entries.Count)
        {
            return;
        }

        Entries.Move(index, target);
        Schedule.Entries.RemoveAt(index);
        Schedule.Entries.Insert(target, entry.Entry);
        Changed();
    }

    internal void RemoveEntry(ScheduleEntryViewModel entry)
    {
        Entries.Remove(entry);
        Schedule.Entries.Remove(entry.Entry);
        OnPropertyChanged(nameof(AvailableLayers));
        Changed();
    }

    private void AddLayer(MediaLayerViewModel layer)
    {
        if (Schedule.Entries.Any(e => e.LayerId == layer.Id))
        {
            return;
        }

        var entry = new ScheduleEntry { LayerId = layer.Id };
        Schedule.Entries.Add(entry);
        Entries.Add(new ScheduleEntryViewModel(entry, this));
        OnPropertyChanged(nameof(AvailableLayers));
        Changed();
    }

    /// <summary>Stops listening to the library (when the schedule is deleted).</summary>
    internal void Detach() => Library.Items.CollectionChanged -= OnLibraryChanged;

    private void OnLibraryChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => RefreshLayers();

    private void RefreshLayers()
    {
        OnPropertyChanged(nameof(AvailableLayers));
        foreach (var entry in Entries)
        {
            entry.RaiseAllPropertiesChanged();
        }
    }

    private TimeSpan? MediaLength(Guid layerId) => Library.Find(layerId)?.Duration;

    private string LayerName(Guid? id) => id is { } layerId ? Library.Find(layerId)?.Name ?? Loc.T("Schedule.MissingLayer") : "—";

    private static string Remaining(TimeSpan span)
    {
        if (span < TimeSpan.Zero)
        {
            span = TimeSpan.Zero;
        }

        return span.TotalHours >= 1 ? span.ToString(@"h\:mm\:ss") : span.TotalMinutes >= 1 ? span.ToString(@"m\:ss") : Loc.F("Media.Seconds", Math.Ceiling(span.TotalSeconds));
    }

    private void SetMode(bool value, ScheduleMode mode)
    {
        if (!value || Schedule.Mode == mode)
        {
            return;
        }

        Schedule.Mode = mode;
        OnPropertyChanged(nameof(IsPermanent));
        OnPropertyChanged(nameof(IsOnce));
        OnPropertyChanged(nameof(IsRepeat));
        Changed();
    }

    private void SetOrder(bool value, ScheduleOrder order)
    {
        if (!value || Schedule.Order == order)
        {
            return;
        }

        Schedule.Order = order;
        OnPropertyChanged(nameof(IsSequential));
        OnPropertyChanged(nameof(IsRandom));
        Changed();
    }

    private void SetNumber(string text, double min, double max, Action<double> assign, [System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
    {
        if (ParseNumber(text) is { } value)
        {
            assign(Math.Clamp(value, min, max));
            Changed();
        }

        OnPropertyChanged(propertyName);
    }

    private void SetTime(string text, Action<string?> assign, [System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            assign(null);
            Changed();
        }
        else if (ScheduleRunner.ParseTime(text) is { } time)
        {
            assign(time.ToString(@"hh\:mm"));
            Changed();
        }

        OnPropertyChanged(propertyName);
    }

    /// <summary>Accepts "2.5" and "2,5".</summary>
    internal static double? ParseNumber(string? text) =>
        double.TryParse(text?.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) ? value : null;

    private static string Format(double value) => value.ToString("0.##", CultureInfo.CurrentCulture);
}
