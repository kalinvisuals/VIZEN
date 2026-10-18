using System.Windows;
using VIZEN.App.Services;
using VIZEN.App.ViewModels;

namespace VIZEN.App.Views;

/// <summary>Owns one <see cref="NoteWindow"/> per note that has been shown; windows are kept while hidden.</summary>
public sealed class NotePresenter : INotePresenter
{
    private const double DefaultWidth = 300;
    private const double DefaultHeight = 260;
    private const double EdgeMargin = 24;
    private const double Stagger = 28;

    private readonly Dictionary<Guid, NoteWindow> _windows = [];

    /// <summary>
    /// Set when VIZEN starts to exit (main window closed, Exit from the tray, Windows sign-out). WPF then closes every
    /// window; note windows must not take that for the user hiding them, so their "on screen" state is kept and the
    /// notes come back at the next start.
    /// </summary>
    public static bool IsExiting { get; private set; }

    public static void BeginExit() => IsExiting = true;

    public void Show(NoteViewModel note)
    {
        if (!_windows.TryGetValue(note.Id, out var window))
        {
            window = new NoteWindow(note);
            _windows[note.Id] = window;
            window.Place(ScreenPlacement.Restorable(note.SavedBounds) ?? DefaultBounds(_windows.Count - 1));
        }

        window.ShowNote();
    }

    public void Hide(NoteViewModel note)
    {
        if (_windows.TryGetValue(note.Id, out var window))
        {
            window.Hide();
        }
    }

    public void Close(NoteViewModel note)
    {
        if (_windows.Remove(note.Id, out var window))
        {
            window.AllowClose = true;
            window.Close();
        }
    }

    /// <summary>New notes open near the top-right corner of the primary work area, each a little lower and to the left.</summary>
    private static Rect DefaultBounds(int index)
    {
        var work = SystemParameters.WorkArea;
        var offset = (index % 8) * Stagger;
        return new Rect(work.Right - DefaultWidth - EdgeMargin - offset, work.Top + EdgeMargin + 40 + offset, DefaultWidth, DefaultHeight);
    }
}
