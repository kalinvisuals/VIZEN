using System.Windows;
using VIZEN.App.Models;
using VIZEN.App.Services;
using VIZEN.App.ViewModels;

namespace VIZEN.Tests;

/// <summary>Smart Notes logic with fake windows and a temporary data folder.</summary>
[Collection(DataFolderCollection.Name)]
public class SmartNotesTests
{
    /// <summary>Records which note windows exist and are visible, like the real presenter's dictionary.</summary>
    private sealed class FakePresenter : INotePresenter
    {
        public HashSet<Guid> Open { get; } = [];
        public HashSet<Guid> Visible { get; } = [];
        public List<string> Calls { get; } = [];

        public void Show(NoteViewModel note) { Open.Add(note.Id); Visible.Add(note.Id); Calls.Add("show " + note.Title); }
        public void Hide(NoteViewModel note) { Visible.Remove(note.Id); Calls.Add("hide " + note.Title); }
        public void Close(NoteViewModel note) { Open.Remove(note.Id); Visible.Remove(note.Id); Calls.Add("close " + note.Title); }
    }

    private static NotesViewModel NewNotes(FakePresenter presenter, ActivityLog? log = null, bool freshFolder = true)
    {
        if (freshFolder)
        {
            DataFolderCollection.UseTemporaryFolder();
        }

        return new NotesViewModel(presenter, log ?? new ActivityLog());
    }

    private static string NotesPath => Path.Combine(JsonStore.DataDirectory, NotesViewModel.FileName);

    [Fact]
    public void CreateShowsTheNoteAndDeleteClosesItsWindow()
    {
        var presenter = new FakePresenter();
        var notes = NewNotes(presenter);

        var text = notes.Create(NoteKind.Text);
        var list = notes.Create(NoteKind.Checklist);

        Assert.Equal([list.Id, text.Id], notes.Notes.Select(n => n.Id)); // newest first
        Assert.True(text.IsShown);
        Assert.Contains(text.Id, presenter.Visible);
        Assert.NotEqual(text.Id, list.Id);

        text.DeleteCommand.Execute(null);
        Assert.True(text.IsConfirmingDelete);    // asks first
        Assert.Equal(2, notes.Notes.Count);
        text.ConfirmDeleteCommand!.Execute(null);

        Assert.Equal([list.Id], notes.Notes.Select(n => n.Id));
        Assert.DoesNotContain(text.Id, presenter.Open);
    }

    [Fact]
    public void EditingRequestsADebouncedSaveAndRoundTrips()
    {
        var notes = NewNotes(new FakePresenter());
        var note = notes.Create(NoteKind.Text);
        notes.SaveNow();
        Assert.False(notes.HasPendingSave);

        note.Text = "Linia 1\nȘtiri, țară, ăîâ — diacritice";
        note.Title = "Plan de joc";

        Assert.True(notes.HasPendingSave); // written once, shortly after the last change, not on every keystroke
        notes.SaveNow();
        Assert.False(notes.HasPendingSave);

        var reloaded = NewNotes(new FakePresenter(), freshFolder: false).Notes.Single();
        Assert.Equal(note.Id, reloaded.Id);
        Assert.Equal("Plan de joc", reloaded.Title);
        Assert.Equal("Linia 1\nȘtiri, țară, ăîâ — diacritice", reloaded.Text);
    }

    [Fact]
    public void RenamingToBlankFallsBackToAName()
    {
        var note = NewNotes(new FakePresenter()).Create(NoteKind.Text);
        note.Title = "   ";
        Assert.Equal("Notes.Untitled", note.Title); // localized at run time; tests see the key
    }

    [Fact]
    public void ChecklistItemsStateAndOrderPersist()
    {
        var notes = NewNotes(new FakePresenter());
        var list = notes.Create(NoteKind.Checklist);
        var a = list.AddItem("Cumpără bilete")!;
        var b = list.AddItem("Verifică setările")!;
        list.AddItem("   "); // ignored
        var c = list.AddItem("Pornește stream-ul")!;
        b.Done = true;
        list.MoveItem(c, -1); // a, c, b
        list.RemoveItem(a);   // c, b

        Assert.Equal("Notes.Progress", list.ProgressText);
        Assert.Equal(1, list.DoneCount);
        Assert.Equal(0.5, list.Progress);
        notes.SaveNow();

        var reloaded = NewNotes(new FakePresenter(), freshFolder: false).Notes.Single();
        Assert.Equal(NoteKind.Checklist, reloaded.Kind);
        Assert.Equal(["Pornește stream-ul", "Verifică setările"], reloaded.Items.Select(i => i.Text));
        Assert.Equal([false, true], reloaded.Items.Select(i => i.Done));
    }

    [Fact]
    public void ConvertingKeepsContentBothWays()
    {
        var note = NewNotes(new FakePresenter()).Create(NoteKind.Text);
        note.Text = "unu\r\n\r\n[x] doi\ntrei";

        note.ConvertCommand.Execute(null);
        Assert.True(note.IsChecklist);
        Assert.Equal(["unu", "doi", "trei"], note.Items.Select(i => i.Text));
        Assert.Equal([false, true, false], note.Items.Select(i => i.Done));

        note.ConvertCommand.Execute(null);
        Assert.True(note.IsText);
        Assert.Empty(note.Items);
        Assert.Equal(string.Join(Environment.NewLine, "[ ] unu", "[x] doi", "[ ] trei"), note.Text);
    }

    [Fact]
    public void SeveralNotesAreShownAndHiddenIndependently()
    {
        var presenter = new FakePresenter();
        var notes = NewNotes(presenter);
        var one = notes.Create(NoteKind.Text);
        var two = notes.Create(NoteKind.Text);
        var three = notes.Create(NoteKind.Checklist);

        two.HideCommand.Execute(null);

        Assert.Equal(new[] { one.Id, three.Id }.Order(), presenter.Visible.Order());
        Assert.Equal(2, notes.ShownCount);

        // Hotkey: hide all, then bring back exactly the ones it hid.
        notes.ToggleVisibilityAll();
        Assert.Empty(presenter.Visible);
        notes.ToggleVisibilityAll();
        Assert.Equal(new[] { one.Id, three.Id }.Order(), presenter.Visible.Order());
        Assert.False(two.IsShown);
    }

    [Fact]
    public void WindowSettingsPersistAndShownNotesReopen()
    {
        var notes = NewNotes(new FakePresenter());
        var note = notes.Create(NoteKind.Text);
        note.ReportBounds(new Rect(120, 80, 340, 280));
        note.Opacity = 0.05;      // clamped to the minimum: a note never becomes invisible
        note.AlwaysOnTop = false;
        note.Locked = true;
        note.ClickThrough = true;
        var hidden = notes.Create(NoteKind.Text);
        hidden.IsShown = false;
        notes.SaveNow();

        var presenter = new FakePresenter();
        var reloaded = NewNotes(presenter, freshFolder: false);
        var again = reloaded.Notes.Single(n => n.Id == note.Id);
        Assert.Equal(new Rect(120, 80, 340, 280), again.SavedBounds);
        Assert.Equal(NoteViewModel.MinOpacity, again.Opacity);
        Assert.False(again.AlwaysOnTop);
        Assert.True(again.Locked);
        Assert.True(again.ClickThrough);

        Assert.Empty(presenter.Open); // nothing opens before the shell asks
        reloaded.RestoreShown();
        Assert.Equal([note.Id], presenter.Visible);
    }

    [Fact]
    public void MissingFileStartsEmptyWithoutError()
    {
        var notes = NewNotes(new FakePresenter());
        Assert.Empty(notes.Notes);
        Assert.Null(notes.LoadError);
    }

    [Fact]
    public void CorruptFileIsKeptAsideAndTheAppStartsEmpty()
    {
        DataFolderCollection.UseTemporaryFolder();
        File.WriteAllText(NotesPath, "{ \"notes\": [ { \"title\": \"half");
        var log = new ActivityLog();

        var notes = NewNotes(new FakePresenter(), log, freshFolder: false);

        Assert.Empty(notes.Notes);
        Assert.NotNull(notes.LoadError);
        Assert.Contains(log.Entries, e => e.Title == "Notes.LoadFailed");
        Assert.True(File.Exists(NotesPath + ".corrupt")); // the user's data is not lost

        notes.Create(NoteKind.Text);
        notes.SaveNow();
        Assert.True(File.Exists(NotesPath + ".corrupt"));
        Assert.Single(NewNotes(new FakePresenter(), freshFolder: false).Notes);
    }

    [Fact]
    public void ImpossibleValuesInTheFileAreRepaired()
    {
        DataFolderCollection.UseTemporaryFolder();
        File.WriteAllText(NotesPath, """
            { "version": 1, "notes": [
              { "id": "00000000-0000-0000-0000-000000000000", "title": null, "text": null, "kind": 7,
                "opacity": 9, "width": -5, "height": 100, "items": [ null, { "text": null, "done": true } ] },
              null
            ] }
            """);

        var note = NewNotes(new FakePresenter(), freshFolder: false).Notes.Single();

        Assert.NotEqual(Guid.Empty, note.Id);
        Assert.Equal(NoteKind.Text, note.Kind);
        Assert.Equal(string.Empty, note.Text);
        Assert.Equal(1, note.Opacity);
        Assert.Null(note.SavedBounds);
        Assert.Single(note.Items);
    }

    [Fact]
    public void EditModeTurnsClickThroughOffAndRestoresOnlyThoseNotes()
    {
        var notes = NewNotes(new FakePresenter());
        var a = notes.Create(NoteKind.Text);
        var b = notes.Create(NoteKind.Text);
        var c = notes.Create(NoteKind.Text);
        a.ClickThrough = true;
        b.ClickThrough = true;
        b.IsShown = false;

        notes.ToggleEditMode();
        Assert.True(notes.IsEditMode);
        Assert.False(a.ClickThrough);
        Assert.False(b.ClickThrough);
        Assert.True(b.IsShown); // brought back so it can be edited
        Assert.False(c.ClickThrough);

        a.ClickThrough = false; // already off; no change
        b.ClickThrough = true;  // set by hand: no longer restored by edit mode
        notes.ToggleEditMode();
        Assert.False(notes.IsEditMode);
        Assert.True(a.ClickThrough);
        Assert.True(b.ClickThrough);
        Assert.False(c.ClickThrough);
    }

    [Fact]
    public void EditModeWithNothingToUnlockSaysSo()
    {
        var log = new ActivityLog();
        var notes = NewNotes(new FakePresenter(), log);
        notes.Create(NoteKind.Text);

        notes.ToggleEditMode();

        Assert.False(notes.IsEditMode);
        Assert.Equal("Notes.EditModeNothing", log.Entries[0].Title);
    }

    [Fact]
    public void ClosingReleasesEveryWindow()
    {
        var presenter = new FakePresenter();
        var notes = NewNotes(presenter);
        notes.Create(NoteKind.Text);
        notes.Create(NoteKind.Checklist).IsShown = false;
        notes.Create(NoteKind.Text);

        notes.SaveNow();
        notes.CloseAll();

        Assert.Empty(presenter.Open);
        Assert.Equal(2, notes.Notes.Count(n => n.IsShown)); // visibility is kept for the next start
    }

    [Fact]
    public void NotesHotkeysHaveDistinctDefaultsAndConflictsAreDetected() => OnSta(() =>
    {
        using var hotkeys = new HotkeyService();
        var notesToggle = hotkeys.Bindings.Single(b => b.Action == HotkeyAction.ToggleNotes);
        var editMode = hotkeys.Bindings.Single(b => b.Action == HotkeyAction.NotesEditMode);

        Assert.Equal(hotkeys.Bindings.Count, hotkeys.Bindings.Select(b => b.Current).Distinct().Count());
        Assert.Equal("Ctrl + Shift + F11", notesToggle.Gesture);
        Assert.Equal("Ctrl + Shift + F8", editMode.Gesture);

        hotkeys.SetEnabled(true);
        var clickThrough = hotkeys.Bindings.Single(b => b.Action == HotkeyAction.ToggleClickThrough);
        Assert.Equal(HotkeyStatus.Conflict, hotkeys.ChangeGesture(editMode, clickThrough.Current)); // VIZEN already uses it
        Assert.True(HotkeyGesture.TryParse("Ctrl+Alt+F7", out var free));
        Assert.NotEqual(HotkeyStatus.Off, hotkeys.ChangeGesture(editMode, free));
        Assert.Equal("Ctrl + Alt + F7", editMode.Gesture);
    });

    private static void OnSta(Action test)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { test(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            throw new Xunit.Sdk.XunitException(failure.ToString());
        }
    }
}
