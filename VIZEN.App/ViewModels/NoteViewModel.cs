using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using VIZEN.App.Infrastructure;
using VIZEN.App.Localization;
using VIZEN.App.Models;

namespace VIZEN.App.ViewModels;

/// <summary>One line of a checklist note.</summary>
public sealed class ChecklistItemViewModel : ObservableObject
{
    private readonly Action _changed;

    public ChecklistItemViewModel(ChecklistItem item, Action changed)
    {
        Item = item;
        _changed = changed;
    }

    public ChecklistItem Item { get; }

    public Guid Id => Item.Id;

    public string Text
    {
        get => Item.Text;
        set
        {
            if (Item.Text != value)
            {
                Item.Text = value;
                OnPropertyChanged();
                _changed();
            }
        }
    }

    public bool Done
    {
        get => Item.Done;
        set
        {
            if (Item.Done != value)
            {
                Item.Done = value;
                OnPropertyChanged();
                _changed();
            }
        }
    }

    public ICommand? RemoveCommand { get; internal set; }
    public ICommand? MoveUpCommand { get; internal set; }
    public ICommand? MoveDownCommand { get; internal set; }
}

/// <summary>
/// One Smart Note: its saved data (<see cref="SmartNote"/>) and commands. Every change asks the owner to save
/// (debounced there); the floating window is opened and closed by the owner through <see cref="IsShown"/>.
/// </summary>
public sealed class NoteViewModel : ObservableObject
{
    public const double MinOpacity = 0.2;

    private readonly Action _saveRequested;
    private bool _isConfirmingDelete;
    private bool _isExpanded = true;
    private string _newItemText = string.Empty;

    public NoteViewModel(SmartNote note, Action saveRequested)
    {
        Note = note;
        _saveRequested = saveRequested;
        foreach (var item in note.Items)
        {
            Items.Add(Wrap(item));
        }

        AddItemCommand = new RelayCommand(() => AddItem(NewItemText), () => !string.IsNullOrWhiteSpace(NewItemText));
        ConvertCommand = new RelayCommand(Convert);
        ToggleShownCommand = new RelayCommand(() => IsShown = !IsShown);
        HideCommand = new RelayCommand(() => IsShown = false);
        ToggleExpandedCommand = new RelayCommand(() => IsExpanded = !IsExpanded);
        DeleteCommand = new RelayCommand(() => IsConfirmingDelete = true);
        CancelDeleteCommand = new RelayCommand(() => IsConfirmingDelete = false);
        ClearDoneCommand = new RelayCommand(ClearDone, () => Items.Any(i => i.Done));
    }

    /// <summary>The persisted data; edit through this view model so changes are saved.</summary>
    public SmartNote Note { get; }

    public Guid Id => Note.Id;

    public ObservableCollection<ChecklistItemViewModel> Items { get; } = [];

    // ----- Content -----

    public string Title
    {
        get => Note.Title;
        set => Set(Note.Title, string.IsNullOrWhiteSpace(value) ? Loc.T("Notes.Untitled") : value.Trim(), v => Note.Title = v);
    }

    public NoteKind Kind
    {
        get => Note.Kind;
        private set
        {
            if (Set(Note.Kind, value, v => Note.Kind = v))
            {
                OnPropertyChanged(nameof(IsChecklist));
                OnPropertyChanged(nameof(IsText));
                OnPropertyChanged(nameof(KindText));
                RefreshProgress();
            }
        }
    }

    public bool IsChecklist => Kind == NoteKind.Checklist;

    public bool IsText => Kind == NoteKind.Text;

    public string KindText => Loc.T(IsChecklist ? "Notes.KindChecklist" : "Notes.KindText");

    public string Text
    {
        get => Note.Text;
        set => Set(Note.Text, value ?? string.Empty, v => Note.Text = v);
    }

    public string NewItemText
    {
        get => _newItemText;
        set => SetProperty(ref _newItemText, value ?? string.Empty);
    }

    public int DoneCount => Items.Count(i => i.Done);

    public string ProgressText => Loc.F("Notes.Progress", DoneCount, Items.Count);

    /// <summary>Kind, plus the progress for checklists (page card).</summary>
    public string DetailText => IsChecklist ? $"{KindText}  ·  {ProgressText}" : KindText;

    /// <summary>0..1 for the progress bar.</summary>
    public double Progress => Items.Count == 0 ? 0 : (double)DoneCount / Items.Count;

    public string WindowTitle => Loc.F("Notes.WindowTitle", Title);

    // ----- Window settings -----

    public bool IsShown
    {
        get => Note.IsShown;
        set => Set(Note.IsShown, value, v => Note.IsShown = v);
    }

    public double Opacity
    {
        get => Note.Opacity;
        set
        {
            if (Set(Note.Opacity, Math.Clamp(value, MinOpacity, 1), v => Note.Opacity = v))
            {
                OnPropertyChanged(nameof(OpacityText));
            }
        }
    }

    public string OpacityText => $"{Math.Round(Opacity * 100)}%";

    public bool AlwaysOnTop
    {
        get => Note.AlwaysOnTop;
        set => Set(Note.AlwaysOnTop, value, v => Note.AlwaysOnTop = v);
    }

    public bool Locked
    {
        get => Note.Locked;
        set => Set(Note.Locked, value, v => Note.Locked = v);
    }

    public bool ClickThrough
    {
        get => Note.ClickThrough;
        set => Set(Note.ClickThrough, value, v => Note.ClickThrough = v);
    }

    public Rect? SavedBounds => Note.Width > 0 && Note.Height > 0 ? new Rect(Note.Left, Note.Top, Note.Width, Note.Height) : null;

    /// <summary>Called by the window after the user moved or resized it.</summary>
    public void ReportBounds(Rect bounds)
    {
        if (Note.Left == bounds.Left && Note.Top == bounds.Top && Note.Width == bounds.Width && Note.Height == bounds.Height)
        {
            return;
        }

        (Note.Left, Note.Top, Note.Width, Note.Height) = (bounds.Left, bounds.Top, bounds.Width, bounds.Height);
        _saveRequested(); // position only: not a content change
    }

    // ----- Page state -----

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

    public ICommand AddItemCommand { get; }
    public ICommand ConvertCommand { get; }
    public ICommand ToggleShownCommand { get; }
    public ICommand HideCommand { get; }
    public ICommand ToggleExpandedCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand CancelDeleteCommand { get; }
    public ICommand ClearDoneCommand { get; }

    /// <summary>Set by the notes list: deletes after the confirmation.</summary>
    public ICommand? ConfirmDeleteCommand { get; internal set; }

    // ----- Checklist -----

    public ChecklistItemViewModel? AddItem(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var item = new ChecklistItem { Text = text.Trim() };
        Note.Items.Add(item);
        var vm = Wrap(item);
        Items.Add(vm);
        NewItemText = string.Empty;
        ContentChanged();
        return vm;
    }

    public void RemoveItem(ChecklistItemViewModel item)
    {
        if (Items.Remove(item))
        {
            Note.Items.Remove(item.Item);
            ContentChanged();
        }
    }

    /// <summary>Moves an item one place up (-1) or down (+1).</summary>
    public void MoveItem(ChecklistItemViewModel item, int direction)
    {
        var index = Items.IndexOf(item);
        var target = index + direction;
        if (index < 0 || target < 0 || target >= Items.Count)
        {
            return;
        }

        Items.Move(index, target);
        Note.Items.RemoveAt(index);
        Note.Items.Insert(target, item.Item);
        ContentChanged();
        CommandManager.InvalidateRequerySuggested();
    }

    private void ClearDone()
    {
        foreach (var item in Items.Where(i => i.Done).ToList())
        {
            Items.Remove(item);
            Note.Items.Remove(item.Item);
        }

        ContentChanged();
    }

    /// <summary>
    /// Text → checklist: one item per non-empty line. Checklist → text: one line per item, "[x] " marking done ones,
    /// so nothing is lost either way.
    /// </summary>
    private void Convert()
    {
        if (IsText)
        {
            Items.Clear();
            Note.Items.Clear();
            foreach (var line in Note.Text.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Trim().Length > 0))
            {
                var done = line.StartsWith("[x] ", StringComparison.OrdinalIgnoreCase);
                var item = new ChecklistItem { Text = (done ? line[4..] : line.StartsWith("[ ] ") ? line[4..] : line).Trim(), Done = done };
                Note.Items.Add(item);
                Items.Add(Wrap(item));
            }

            Note.Text = string.Empty;
            OnPropertyChanged(nameof(Text));
            Kind = NoteKind.Checklist;
        }
        else
        {
            Note.Text = string.Join(Environment.NewLine, Note.Items.Select(i => (i.Done ? "[x] " : "[ ] ") + i.Text));
            Items.Clear();
            Note.Items.Clear();
            OnPropertyChanged(nameof(Text));
            Kind = NoteKind.Text;
        }

        ContentChanged();
    }

    private ChecklistItemViewModel Wrap(ChecklistItem item)
    {
        var vm = new ChecklistItemViewModel(item, ContentChanged);
        vm.RemoveCommand = new RelayCommand(() => RemoveItem(vm));
        vm.MoveUpCommand = new RelayCommand(() => MoveItem(vm, -1), () => Items.IndexOf(vm) > 0);
        vm.MoveDownCommand = new RelayCommand(() => MoveItem(vm, 1), () => Items.IndexOf(vm) < Items.Count - 1);
        return vm;
    }

    private void ContentChanged()
    {
        Note.ModifiedUtc = DateTime.UtcNow;
        RefreshProgress();
        _saveRequested();
    }

    private void RefreshProgress()
    {
        OnPropertyChanged(nameof(DoneCount));
        OnPropertyChanged(nameof(ProgressText));
        OnPropertyChanged(nameof(DetailText));
        OnPropertyChanged(nameof(Progress));
    }

    private bool Set<T>(T current, T value, Action<T> assign, [System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(current, value))
        {
            return false;
        }

        assign(value);
        OnPropertyChanged(propertyName);
        if (propertyName is nameof(Title))
        {
            OnPropertyChanged(nameof(WindowTitle));
        }

        if (propertyName is nameof(Title) or nameof(Text))
        {
            Note.ModifiedUtc = DateTime.UtcNow;
        }

        _saveRequested();
        return true;
    }
}
