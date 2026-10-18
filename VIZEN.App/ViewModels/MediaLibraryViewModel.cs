using System.Collections.ObjectModel;
using System.ComponentModel;
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
/// Media &amp; graphics manager: imported logos, GIFs and videos, each a layer that can be shown as an
/// overlay or an OBS output. The list is ordered front-to-back (first item is drawn on top).
/// Persisted to media.json; files are referenced in place, not copied.
/// </summary>
public sealed class MediaLibraryViewModel : ObservableObject
{
    private const string FileName = "media.json";

    private readonly IMediaPresenter _presenter;
    private readonly IMediaFilePicker _picker;
    private readonly ActivityLog _activity;
    private readonly DispatcherTimer _saveTimer;
    private readonly List<MediaLayerViewModel> _hiddenByHotkey = [];
    private int _quiet;
    private string? _errorMessage;

    public MediaLibraryViewModel(IMediaPresenter presenter, IMediaFilePicker picker, ActivityLog activity)
    {
        _presenter = presenter;
        _picker = picker;
        _activity = activity;
        _saveTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(600), DispatcherPriority.Background, (_, _) => SaveNow(), Dispatcher.CurrentDispatcher);
        _saveTimer.Stop();

        ImportCommand = new RelayCommand(Import);
        HideAllCommand = new RelayCommand(HideAll, () => Items.Any(i => i.IsShown));
        ShowGroupCommand = new RelayCommand<string>(g => SetGroupShown(g, true));
        HideGroupCommand = new RelayCommand<string>(g => SetGroupShown(g, false));
        LockGroupCommand = new RelayCommand<string>(g => SetGroupLocked(g, true));
        UnlockGroupCommand = new RelayCommand<string>(g => SetGroupLocked(g, false));

        var data = JsonStore.Load<MediaFile>(FileName, out var loadError);
        if (loadError is not null)
        {
            _activity.Add("", "Media.LoadFailed", ActivityLog.RawDetail, loadError);
        }

        foreach (var item in data?.Items ?? [])
        {
            Items.Add(CreateLayer(item));
        }

        Items.CollectionChanged += (_, _) => RefreshSummary();
        RefreshSummary();
    }

    public ObservableCollection<MediaLayerViewModel> Items { get; } = [];

    public bool HasItems => Items.Count > 0;

    public int ShownCount => Items.Count(i => i.IsShown);

    public string SummaryText => Loc.F("Media.Summary", Items.Count, ShownCount);

    /// <summary>Distinct group names, for the group actions.</summary>
    public IReadOnlyList<string> Groups => Items.Select(i => i.Group).Where(g => g.Length > 0)
        .Distinct(StringComparer.CurrentCultureIgnoreCase).Order(StringComparer.CurrentCultureIgnoreCase).ToList();

    public bool HasGroups => Groups.Count > 0;

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set => SetProperty(ref _errorMessage, value);
    }

    public string StorageLocation => Path.Combine(JsonStore.DataDirectory, FileName);

    public ICommand ImportCommand { get; }
    public ICommand HideAllCommand { get; }
    public ICommand ShowGroupCommand { get; }
    public ICommand HideGroupCommand { get; }
    public ICommand LockGroupCommand { get; }
    public ICommand UnlockGroupCommand { get; }

    public MediaLayerViewModel? Find(Guid id) => Items.FirstOrDefault(i => i.Id == id);

    /// <summary>Imports files picked by the user.</summary>
    public void Import() => ImportFiles(_picker.PickFiles());

    /// <summary>Adds the given files as new layers in front. Unsupported files are reported, not added.</summary>
    public IReadOnlyList<MediaLayerViewModel> ImportFiles(IEnumerable<string> paths)
    {
        var added = new List<MediaLayerViewModel>();
        var rejected = new List<string>();
        foreach (var path in paths)
        {
            if (MediaFormats.KindFor(path) is not { } kind)
            {
                rejected.Add(Path.GetFileName(path));
                continue;
            }

            var layer = CreateLayer(new MediaItem
            {
                Name = Path.GetFileNameWithoutExtension(path),
                FilePath = Path.GetFullPath(path),
                Kind = kind,
            });
            Items.Insert(0, layer);
            added.Add(layer);
        }

        if (added.Count > 0)
        {
            Renumber();
            RequestSave();
            _activity.Add("", "Media.Imported", "Media.ImportedDetail", added.Count,
                string.Join(", ", added.Select(a => $"{a.Name} ({a.KindText})")));
        }

        if (rejected.Count > 0)
        {
            _activity.Add("", "Media.ImportRejected", "Media.ImportRejectedDetail", string.Join(", ", rejected));
        }

        return added;
    }

    /// <summary>Shows or hides a layer without a confirmation toast (used by schedules and playlists).</summary>
    public void SetShownQuietly(MediaLayerViewModel layer, bool shown)
    {
        _quiet++;
        try
        {
            layer.IsShown = shown;
        }
        finally
        {
            _quiet--;
        }
    }

    /// <summary>Hotkey: hides every shown layer, or brings back the ones it hid.</summary>
    public void ToggleVisibilityAll()
    {
        var shown = Items.Where(i => i.IsShown).ToList();
        if (shown.Count > 0)
        {
            _hiddenByHotkey.Clear();
            _hiddenByHotkey.AddRange(shown);
            shown.ForEach(l => SetShownQuietly(l, false));
        }
        else
        {
            _hiddenByHotkey.Where(Items.Contains).ToList().ForEach(l => SetShownQuietly(l, true));
            _hiddenByHotkey.Clear();
        }
    }

    public void HideAll()
    {
        var shown = Items.Where(i => i.IsShown).ToList();
        shown.ForEach(l => SetShownQuietly(l, false));
        if (shown.Count > 0)
        {
            _activity.Add("", "Media.AllHidden", "Media.AllHiddenDetail", shown.Count);
        }
    }

    private MediaLayerViewModel CreateLayer(MediaItem item)
    {
        var layer = new MediaLayerViewModel(item, RequestSave);
        layer.ConfirmRemoveCommand = new RelayCommand(() => Remove(layer));
        layer.DuplicateCommand = new RelayCommand(() => Duplicate(layer));
        layer.BringForwardCommand = new RelayCommand(() => Move(layer, -1), () => Items.IndexOf(layer) > 0);
        layer.SendBackwardCommand = new RelayCommand(() => Move(layer, 1), () => Items.IndexOf(layer) < Items.Count - 1);
        layer.PropertyChanged += (_, e) => OnLayerPropertyChanged(layer, e);
        return layer;
    }

    private async void OnLayerPropertyChanged(MediaLayerViewModel layer, PropertyChangedEventArgs e)
    {
        var quiet = _quiet > 0;
        switch (e.PropertyName)
        {
            case nameof(MediaLayerViewModel.IsShown):
                RefreshSummary();
                if (layer.IsShown)
                {
                    var shown = await _presenter.ShowAsync(layer, Items.IndexOf(layer));
                    if (!shown)
                    {
                        SetShownQuietly(layer, false);
                        _activity.Add("", "Media.ShowFailed", ActivityLog.RawDetail, layer.Error ?? layer.FileName);
                        return;
                    }

                    _presenter.ApplyZOrder(Items.Reverse());
                    if (!quiet)
                    {
                        _activity.Add("", "Media.LayerShown", "Media.LayerShownDetail", layer.Name, DescribeTarget(layer), layer.NaturalSizeText);
                    }
                }
                else
                {
                    _presenter.Hide(layer);
                    if (!quiet)
                    {
                        _activity.Add("", "Media.LayerHidden", "Media.LayerHiddenDetail", layer.Name);
                    }
                }

                break;

            case nameof(MediaLayerViewModel.Target):
                _activity.Add("", "Media.TargetChanged", "Media.TargetChangedDetail", layer.Name, DescribeTarget(layer));
                if (layer.IsShown)
                {
                    // Overlay and output windows are different kinds of window: reopen.
                    _presenter.Close(layer);
                    await _presenter.ShowAsync(layer, Items.IndexOf(layer));
                    _presenter.ApplyZOrder(Items.Reverse());
                }

                break;

            case nameof(MediaLayerViewModel.Locked) when !quiet:
                _activity.Add("", layer.Locked ? "Media.LockedOn" : "Media.LockedOff",
                    layer.Locked ? "Media.LockedOnDetail" : "Media.LockedOffDetail", layer.Name);
                break;

            case nameof(MediaLayerViewModel.ClickThrough) when !quiet:
                _activity.Add("", layer.ClickThrough ? "Activity.ClickThroughOn" : "Activity.ClickThroughOff",
                    layer.ClickThrough ? "Activity.ClickThroughOnDetail" : "Activity.ClickThroughOffDetail", layer.Name);
                break;

            case nameof(MediaLayerViewModel.Group):
                OnPropertyChanged(nameof(Groups));
                OnPropertyChanged(nameof(HasGroups));
                break;
        }
    }

    private void Remove(MediaLayerViewModel layer)
    {
        _presenter.Close(layer);
        Items.Remove(layer);
        Renumber();
        RequestSave();
        _activity.Add("", "Media.Removed", "Media.RemovedDetail", layer.Name);
    }

    private void Duplicate(MediaLayerViewModel layer)
    {
        var copy = layer.Item.Clone();
        copy.Id = Guid.NewGuid();
        copy.Name = Loc.F("Media.CopyName", layer.Name);
        if (copy.Width > 0)
        {
            copy.Left += 24;
            copy.Top += 24;
        }

        var duplicate = CreateLayer(copy);
        Items.Insert(Items.IndexOf(layer), duplicate);
        Renumber();
        RequestSave();
        _activity.Add("", "Media.Duplicated", "Media.DuplicatedDetail", layer.Name, copy.Name);
    }

    /// <summary>Moves a layer one step toward the front (-1) or back (+1).</summary>
    private void Move(MediaLayerViewModel layer, int direction)
    {
        var index = Items.IndexOf(layer);
        var target = index + direction;
        if (index < 0 || target < 0 || target >= Items.Count)
        {
            return;
        }

        Items.Move(index, target);
        Renumber();
        RequestSave();
        _presenter.ApplyZOrder(Items.Reverse());
        _activity.Add("", "Media.LayerMoved", "Media.LayerMovedDetail", layer.Name, layer.Layer, Items.Count);
        CommandManager.InvalidateRequerySuggested();
    }

    private void SetGroupShown(string group, bool shown)
    {
        var members = Members(group);
        members.ForEach(l => SetShownQuietly(l, shown));
        _activity.Add(shown ? "" : "", shown ? "Media.GroupShown" : "Media.GroupHidden", "Media.GroupDetail", group, members.Count);
    }

    private void SetGroupLocked(string group, bool locked)
    {
        var members = Members(group);
        _quiet++;
        try
        {
            members.ForEach(l => l.Locked = locked);
        }
        finally
        {
            _quiet--;
        }

        _activity.Add("", locked ? "Media.GroupLocked" : "Media.GroupUnlocked", "Media.GroupDetail", group, members.Count);
    }

    private List<MediaLayerViewModel> Members(string group) =>
        Items.Where(i => string.Equals(i.Group, group, StringComparison.CurrentCultureIgnoreCase)).ToList();

    /// <summary>Front item gets the highest layer number.</summary>
    private void Renumber()
    {
        for (var i = 0; i < Items.Count; i++)
        {
            Items[i].Layer = Items.Count - i;
        }
    }

    private static string DescribeTarget(MediaLayerViewModel layer) => layer.Target switch
    {
        MediaTarget.Output => Loc.F("Media.TargetOutputDetail", layer.WindowTitle),
        MediaTarget.Composition => Loc.T("Media.TargetCompositionDetail"),
        _ => Loc.T("Media.TargetOverlayDetail"),
    };

    private void RefreshSummary()
    {
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(ShownCount));
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(Groups));
        OnPropertyChanged(nameof(HasGroups));
    }

    private void RequestSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    /// <summary>Writes media.json now (also called on exit).</summary>
    public void SaveNow()
    {
        _saveTimer.Stop();
        try
        {
            JsonStore.Save(FileName, new MediaFile { Items = Items.Select(i => i.Item).ToList() });
            ErrorMessage = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ErrorMessage = Loc.F("Media.SaveFailed", ex.Message);
        }
    }

    public void CloseAll()
    {
        foreach (var layer in Items)
        {
            _presenter.Close(layer);
        }
    }

    private sealed class MediaFile
    {
        public int Version { get; set; } = 1;
        public List<MediaItem> Items { get; set; } = [];
    }
}
