using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Windows.Input;
using System.Windows.Threading;
using VIZEN.App.Infrastructure;
using VIZEN.App.Localization;
using VIZEN.App.Media;
using VIZEN.App.Models;
using VIZEN.App.Services;

namespace VIZEN.App.ViewModels;

/// <summary>
/// Schedules &amp; sponsor playlists: runs every schedule independently on one 4 Hz timer and shows/hides
/// media layers through the library (so they work for overlays and OBS outputs alike). Persisted in schedules.json.
/// </summary>
public sealed class SchedulesViewModel : ObservableObject
{
    private const string FileName = "schedules.json";

    private readonly HotkeyService _hotkeys;
    private readonly ActivityLog _activity;
    private readonly Func<DateTime> _clock;
    private readonly DispatcherTimer _tick;
    private readonly DispatcherTimer _saveTimer;
    private string? _errorMessage;

    public SchedulesViewModel(MediaLibraryViewModel library, HotkeyService hotkeys, ActivityLog activity, Func<DateTime>? clock = null)
    {
        Library = library;
        _hotkeys = hotkeys;
        _activity = activity;
        _clock = clock ?? (() => DateTime.Now);
        _saveTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(600), DispatcherPriority.Background, (_, _) => SaveNow(), Dispatcher.CurrentDispatcher);
        _saveTimer.Stop();
        _tick = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Normal, (_, _) => Tick(), Dispatcher.CurrentDispatcher);

        NewScheduleCommand = new RelayCommand(() => Add(NewSchedule(Loc.F("Schedule.DefaultName", Items.Count + 1)), log: true));
        NewFromGroupCommand = new RelayCommand<string>(g => NewFromGroup(g));
        StopAllCommand = new RelayCommand(StopAll, () => Items.Any(i => i.IsRunning || i.IsPaused));

        var data = JsonStore.Load<SchedulesFile>(FileName, out var loadError);
        if (loadError is not null)
        {
            _activity.Add("", "Schedule.LoadFailed", ActivityLog.RawDetail, loadError);
        }

        foreach (var schedule in data?.Schedules ?? [])
        {
            Add(schedule, log: false);
        }

        foreach (var layer in library.Items)
        {
            layer.PlaybackEnded += OnPlaybackEnded;
        }

        library.Items.CollectionChanged += OnLibraryChanged;
        Items.CollectionChanged += (_, _) => RefreshSummary();

        var now = _clock();
        foreach (var item in Items.Where(i => i.Schedule.AutoStart && i.HasEntries))
        {
            Apply(item, item.Runner.Start(now));
        }

        if (Items.Any(i => i.IsRunning))
        {
            _activity.Add("", "Schedule.AutoStarted", "Schedule.AutoStartedDetail",
                string.Join(", ", Items.Where(i => i.IsRunning).Select(i => i.Name)));
        }

        RefreshSummary();
    }

    public MediaLibraryViewModel Library { get; }

    public ObservableCollection<ScheduleViewModel> Items { get; } = [];

    public bool HasItems => Items.Count > 0;

    public string SummaryText => Loc.F("Schedule.Summary", Items.Count, Items.Count(i => i.IsRunning));

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set => SetProperty(ref _errorMessage, value);
    }

    public string StorageLocation => Path.Combine(JsonStore.DataDirectory, FileName);

    public ICommand NewScheduleCommand { get; }
    public ICommand NewFromGroupCommand { get; }
    public ICommand StopAllCommand { get; }

    /// <summary>Creates a sponsor playlist from all layers of a media group: 10 s each, every 5 minutes, in order.</summary>
    public ScheduleViewModel? NewFromGroup(string? group)
    {
        var members = Library.Items.Where(l => string.Equals(l.Group, group, StringComparison.CurrentCultureIgnoreCase)).ToList();
        if (string.IsNullOrWhiteSpace(group) || members.Count == 0)
        {
            return null;
        }

        var schedule = NewSchedule(Loc.F("Schedule.PlaylistName", group));
        schedule.Entries = members.Select(m => new ScheduleEntry { LayerId = m.Id }).ToList();
        return Add(schedule, log: true);
    }

    public void Start(ScheduleViewModel item) => Run(item, "Schedule.Started", item.Runner.Start(_clock()));

    public void Trigger(ScheduleViewModel item) => Run(item, "Schedule.Triggered", item.Runner.Trigger(_clock()));

    public void StopAll()
    {
        foreach (var item in Items)
        {
            Apply(item, item.Runner.Stop());
        }

        _activity.Add("", "Schedule.AllStopped", "Schedule.AllStoppedDetail", Items.Count);
        RefreshSummary();
    }

    /// <summary>Advances every schedule to the current time (the 4 Hz timer calls this).</summary>
    public void Tick()
    {
        var now = _clock();
        foreach (var item in Items)
        {
            Apply(item, item.Runner.Tick(now));
            item.RefreshStatus(now);
        }
    }

    private ScheduleViewModel Add(MediaSchedule schedule, bool log)
    {
        var item = new ScheduleViewModel(schedule, Library, RequestSave, RegisterHotkey);
        item.StartCommand = new RelayCommand(() => Start(item), () => item.CanStart || item.IsPaused);
        item.PauseCommand = new RelayCommand(() => Run(item, "Schedule.Paused", item.Runner.Pause()), () => item.IsRunning);
        item.StopCommand = new RelayCommand(() => Run(item, "Schedule.Stopped", item.Runner.Stop()), () => item.IsRunning || item.IsPaused);
        item.TriggerCommand = new RelayCommand(() => Trigger(item), () => item.HasEntries);
        item.ConfirmDeleteCommand = new RelayCommand(() => Delete(item));
        Items.Add(item);
        RegisterHotkey(item);
        item.RefreshStatus(_clock());
        item.RefreshPreview(_clock());
        if (log)
        {
            RequestSave();
            _activity.Add("", "Schedule.Created", "Schedule.CreatedDetail", item.Name, item.Entries.Count);
        }

        return item;
    }

    private void Run(ScheduleViewModel item, string titleKey, IReadOnlyList<LayerChange> changes)
    {
        if (titleKey == "Schedule.Started" && item.IsPaused)
        {
            changes = item.Runner.Resume(_clock());
        }

        Apply(item, changes);
        item.RefreshStatus(_clock());
        item.RefreshPreview(_clock());
        RefreshSummary();
        _activity.Add("", titleKey, "Schedule.ActionDetail", item.Name, item.StatusText);
        CommandManager.InvalidateRequerySuggested();
    }

    private void Delete(ScheduleViewModel item)
    {
        Apply(item, item.Runner.Stop());
        _hotkeys.UnregisterExtra(HotkeyKey(item));
        Items.Remove(item);
        item.Detach();
        RequestSave();
        _activity.Add("", "Schedule.Deleted", "Schedule.DeletedDetail", item.Name);
    }

    private void Apply(ScheduleViewModel item, IReadOnlyList<LayerChange> changes)
    {
        foreach (var change in changes)
        {
            if (Library.Find(change.LayerId) is { } layer)
            {
                Library.SetShownQuietly(layer, change.Show);
            }
        }
    }

    private void RegisterHotkey(ScheduleViewModel item)
    {
        var key = HotkeyKey(item);
        if (!HotkeyGesture.TryParse(item.Schedule.Hotkey, out var gesture))
        {
            _hotkeys.UnregisterExtra(key);
            item.HotkeyStatusText = string.Empty;
            return;
        }

        var status = _hotkeys.RegisterExtra(key, gesture, () => Trigger(item));
        item.HotkeyStatusText = Loc.T($"Hotkey.Status.{status}");
    }

    private static string HotkeyKey(ScheduleViewModel item) => "schedule:" + item.Id;

    private void OnPlaybackEnded(object? sender, EventArgs e)
    {
        if (sender is not MediaLayerViewModel layer)
        {
            return;
        }

        var now = _clock();
        foreach (var item in Items)
        {
            Apply(item, item.Runner.OnMediaEnded(layer.Id, now));
        }
    }

    private void OnLibraryChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (var layer in e.NewItems?.OfType<MediaLayerViewModel>() ?? [])
        {
            layer.PlaybackEnded += OnPlaybackEnded;
        }

        foreach (var layer in e.OldItems?.OfType<MediaLayerViewModel>() ?? [])
        {
            layer.PlaybackEnded -= OnPlaybackEnded;
        }
    }

    private static MediaSchedule NewSchedule(string name) => new() { Name = name };

    private void RefreshSummary()
    {
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(SummaryText));
    }

    private void RequestSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    public void SaveNow()
    {
        _saveTimer.Stop();
        try
        {
            JsonStore.Save(FileName, new SchedulesFile { Schedules = Items.Select(i => i.Schedule).ToList() });
            ErrorMessage = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ErrorMessage = Loc.F("Schedule.SaveFailed", ex.Message);
        }
    }

    /// <summary>Stops the timer and all schedules (on exit).</summary>
    public void Shutdown()
    {
        _tick.Stop();
        SaveNow();
        foreach (var item in Items)
        {
            _hotkeys.UnregisterExtra(HotkeyKey(item));
        }
    }

    private sealed class SchedulesFile
    {
        public int Version { get; set; } = 1;
        public List<MediaSchedule> Schedules { get; set; } = [];
    }
}
