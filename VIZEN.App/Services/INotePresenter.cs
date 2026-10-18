using VIZEN.App.ViewModels;

namespace VIZEN.App.Services;

/// <summary>Opens and closes the floating windows of Smart Notes (implemented by the view layer).</summary>
public interface INotePresenter
{
    /// <summary>Opens (or reuses) the note's window and shows it without taking the focus.</summary>
    void Show(NoteViewModel note);

    /// <summary>Hides the note's window (kept for the next show).</summary>
    void Hide(NoteViewModel note);

    /// <summary>Closes and forgets the note's window.</summary>
    void Close(NoteViewModel note);
}
