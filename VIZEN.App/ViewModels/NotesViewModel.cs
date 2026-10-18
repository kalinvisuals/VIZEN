using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Input;
using System.Windows.Threading;
using VIZEN.App.Infrastructure;
using VIZEN.App.Localization;
using VIZEN.App.Models;
using VIZEN.App.Services;

namespace VIZEN.App.ViewModels;

/// <summary>
/// VIZEN Smart Notes: text and checklist notes, each with an optional floating always-on-top window. Persisted to
/// notes.json in the data folder, debounced (one write shortly after the last change, and on exit). Static content:
/// no capture, no GPU device, no timer while nothing changes.
/// </summary>
public sealed class NotesViewModel : ObservableObject
{
    public const string FileName = "notes.json";
    private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(800);

    private readonly INotePresenter _presenter;
    private readonly ActivityLog _activity;
    private readonly DispatcherTimer _saveTimer;
    private readonly List<NoteViewModel> _hiddenByHotkey = [];
    private readonly List<NoteViewModel> _clickThroughBeforeEdit = [];
    private string? _errorMessage;
    private int _quiet;

    public NotesViewModel(INotePresenter presenter, ActivityLog activity)
    {
        _presenter = presenter;
        _activity = activity;
        _saveTimer = new DispatcherTimer(SaveDelay, DispatcherPriority.Background, (_, _) => SaveNow(), Dispatcher.CurrentDispatcher);
        _saveTimer.Stop();

        NewTextCommand = new RelayCommand(() => Create(NoteKind.Text));
        NewChecklistCommand = new RelayCommand(() => Create(NoteKind.Checklist));
        ShowAllCommand = new RelayCommand(() => SetAllShown(true), () => Notes.Any(n => !n.IsShown));
        HideAllCommand = new RelayCommand(() => SetAllShown(false), () => Notes.Any(n => n.IsShown));
        ToggleEditModeCommand = new RelayCommand(ToggleEditMode);

        var data = JsonStore.Load<NotesFile>(FileName, out var loadError);
        if (loadError is not null)
        {
            // The unreadable file was copied aside as notes.json.corrupt by JsonStore; start with what could be read.
            LoadError = loadError;
            _activity.Add("", "Notes.LoadFailed", ActivityLog.RawDetail, loadError);
        }

        foreach (var note in data?.Notes ?? [])
        {
            if (note is not null)
            {
                Notes.Add(CreateViewModel(Sanitize(note)));
            }
        }

        Notes.CollectionChanged += (_, _) => RefreshSummary();
    }

    public ObservableCollection<NoteViewModel> Notes { get; } = [];

    public bool HasNotes => Notes.Count > 0;

    public int ShownCount => Notes.Count(n => n.IsShown);

    public string SummaryText => Loc.F("Notes.Summary", Notes.Count, ShownCount);

    /// <summary>True while some notes had click-through turned off by "edit mode" (the hotkey or the page button).</summary>
    public bool IsEditMode => _clickThroughBeforeEdit.Count > 0;

    /// <summary>The load error of this session, if notes.json could not be read.</summary>
    public string? LoadError { get; }

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set => SetProperty(ref _errorMessage, value);
    }

    public bool HasPendingSave => _saveTimer.IsEnabled;

    public string StorageLocation => Path.Combine(JsonStore.DataDirectory, FileName);

    public ICommand NewTextCommand { get; }
    public ICommand NewChecklistCommand { get; }
    public ICommand ShowAllCommand { get; }
    public ICommand HideAllCommand { get; }
    public ICommand ToggleEditModeCommand { get; }

    /// <summary>Opens the windows of notes that were shown when VIZEN last closed.</summary>
    public void RestoreShown()
    {
        foreach (var note in Notes.Where(n => n.IsShown))
        {
            _presenter.Show(note);
        }
    }

    /// <summary>Creates a note at the top of the list and shows its window.</summary>
    public NoteViewModel Create(NoteKind kind)
    {
        var note = CreateViewModel(new SmartNote
        {
            Kind = kind,
            Title = Loc.F(kind == NoteKind.Checklist ? "Notes.NewChecklistTitle" : "Notes.NewTextTitle", Notes.Count + 1),
        });
        Notes.Insert(0, note);
        _quiet++;
        try
        {
            note.IsShown = true;
        }
        finally
        {
            _quiet--;
        }

        RequestSave();
        _activity.Add("", "Notes.Created", "Notes.CreatedDetail", note.Title, note.KindText);
        return note;
    }

    public void Delete(NoteViewModel note)
    {
        _presenter.Close(note);
        Notes.Remove(note);
        _hiddenByHotkey.Remove(note);
        _clickThroughBeforeEdit.Remove(note);
        RequestSave();
        _activity.Add("", "Notes.Deleted", "Notes.DeletedDetail", note.Title);
    }

    /// <summary>Hotkey: hides every shown note, or brings back the ones it hid.</summary>
    public void ToggleVisibilityAll()
    {
        var shown = Notes.Where(n => n.IsShown).ToList();
        _quiet++;
        try
        {
            if (shown.Count > 0)
            {
                _hiddenByHotkey.Clear();
                _hiddenByHotkey.AddRange(shown);
                shown.ForEach(n => n.IsShown = false);
            }
            else
            {
                var again = _hiddenByHotkey.Where(Notes.Contains).ToList();
                (again.Count > 0 ? again : Notes.ToList()).ForEach(n => n.IsShown = true);
                _hiddenByHotkey.Clear();
            }
        }
        finally
        {
            _quiet--;
        }

        _activity.Add("", shown.Count > 0 ? "Notes.AllHidden" : "Notes.AllShown", "Notes.CountDetail",
            shown.Count > 0 ? shown.Count : ShownCount);
    }

    /// <summary>
    /// Edit mode (hotkey or page button): turns click-through off on every note that has it, so the notes can be
    /// clicked and edited again; pressing again restores click-through on exactly those notes.
    /// </summary>
    public void ToggleEditMode()
    {
        _quiet++;
        try
        {
            if (IsEditMode)
            {
                var restored = _clickThroughBeforeEdit.Where(Notes.Contains).ToList();
                _clickThroughBeforeEdit.Clear();
                restored.ForEach(n => n.ClickThrough = true);
                _activity.Add("", "Notes.EditModeOff", "Notes.EditModeOffDetail", restored.Count);
            }
            else
            {
                var locked = Notes.Where(n => n.ClickThrough).ToList();
                if (locked.Count == 0)
                {
                    _activity.Add("", "Notes.EditModeNothing", "Notes.EditModeNothingDetail");
                    return;
                }

                _clickThroughBeforeEdit.AddRange(locked);
                locked.ForEach(n =>
                {
                    n.ClickThrough = false;
                    n.IsShown = true;
                });
                _activity.Add("", "Notes.EditModeOn", "Notes.EditModeOnDetail", locked.Count);
            }
        }
        finally
        {
            _quiet--;
            OnPropertyChanged(nameof(IsEditMode));
        }
    }

    private void SetAllShown(bool shown)
    {
        var changed = Notes.Where(n => n.IsShown != shown).ToList();
        _quiet++;
        try
        {
            changed.ForEach(n => n.IsShown = shown);
        }
        finally
        {
            _quiet--;
        }

        _activity.Add("", shown ? "Notes.AllShown" : "Notes.AllHidden", "Notes.CountDetail", changed.Count);
    }

    private NoteViewModel CreateViewModel(SmartNote data)
    {
        var note = new NoteViewModel(data, RequestSave);
        note.ConfirmDeleteCommand = new RelayCommand(() => Delete(note));
        note.PropertyChanged += (_, e) => OnNotePropertyChanged(note, e);
        return note;
    }

    private void OnNotePropertyChanged(NoteViewModel note, PropertyChangedEventArgs e)
    {
        var quiet = _quiet > 0;
        switch (e.PropertyName)
        {
            case nameof(NoteViewModel.IsShown):
                if (note.IsShown)
                {
                    _presenter.Show(note);
                }
                else
                {
                    _presenter.Hide(note);
                }

                RefreshSummary();
                if (!quiet)
                {
                    _activity.Add("", note.IsShown ? "Notes.Shown" : "Notes.Hidden", "Notes.NameDetail", note.Title);
                }

                break;

            case nameof(NoteViewModel.ClickThrough):
                if (!quiet)
                {
                    // Changed by hand: no longer part of an edit-mode restore.
                    _clickThroughBeforeEdit.Remove(note);
                    OnPropertyChanged(nameof(IsEditMode));
                    _activity.Add("", note.ClickThrough ? "Notes.ClickThroughOn" : "Notes.ClickThroughOff",
                        note.ClickThrough ? "Notes.ClickThroughOnDetail" : "Notes.ClickThroughOffDetail", note.Title);
                }

                break;

            case nameof(NoteViewModel.Locked) when !quiet:
                _activity.Add("", note.Locked ? "Notes.LockedOn" : "Notes.LockedOff", "Notes.NameDetail", note.Title);
                break;
        }
    }

    /// <summary>Repairs values a hand-edited or older file may contain.</summary>
    private static SmartNote Sanitize(SmartNote note)
    {
        if (note.Id == Guid.Empty)
        {
            note.Id = Guid.NewGuid();
        }

        note.Title ??= string.Empty;
        note.Text ??= string.Empty;
        note.Items = (note.Items ?? []).Where(i => i is not null).Select(i =>
        {
            i.Text ??= string.Empty;
            if (i.Id == Guid.Empty)
            {
                i.Id = Guid.NewGuid();
            }

            return i;
        }).ToList();
        note.Opacity = double.IsFinite(note.Opacity) ? Math.Clamp(note.Opacity, NoteViewModel.MinOpacity, 1) : 1;
        if (!double.IsFinite(note.Left) || !double.IsFinite(note.Top) || !double.IsFinite(note.Width) || !double.IsFinite(note.Height)
            || note.Width < 0 || note.Height < 0)
        {
            note.Left = note.Top = note.Width = note.Height = 0;
        }

        if (!Enum.IsDefined(note.Kind))
        {
            note.Kind = NoteKind.Text;
        }

        return note;
    }

    private void RefreshSummary()
    {
        OnPropertyChanged(nameof(HasNotes));
        OnPropertyChanged(nameof(ShownCount));
        OnPropertyChanged(nameof(SummaryText));
        CommandManager.InvalidateRequerySuggested();
    }

    private void RequestSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    /// <summary>Writes notes.json now (also called on exit).</summary>
    public void SaveNow()
    {
        _saveTimer.Stop();
        try
        {
            JsonStore.Save(FileName, new NotesFile { Notes = Notes.Select(n => n.Note).ToList() });
            ErrorMessage = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ErrorMessage = Loc.F("Notes.SaveFailed", ex.Message);
        }
    }

    public void CloseAll()
    {
        foreach (var note in Notes)
        {
            _presenter.Close(note);
        }
    }

    private sealed class NotesFile
    {
        public int Version { get; set; } = 1;
        public List<SmartNote> Notes { get; set; } = [];
    }
}
