using System.Windows;
using VIZEN.App.Services;
using VIZEN.App.ViewModels;

namespace VIZEN.App.Views;

/// <summary>Opens one <see cref="CompositionWindow"/> per open composition, at its last position.</summary>
public sealed class CompositionPresenter : ICompositionPresenter
{
    private readonly Dictionary<Guid, CompositionWindow> _windows = [];

    public void Show(CompositionViewModel composition)
    {
        if (_windows.ContainsKey(composition.Id))
        {
            return;
        }

        var window = new CompositionWindow(composition);
        var model = composition.Composition;
        var saved = new Rect(model.Left, model.Top, model.WindowWidth, model.WindowHeight);
        var desktop = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        if (saved.Width > 0 && saved.Height > 0 && Rect.Intersect(saved, desktop) is { IsEmpty: false } visible && visible.Width >= 80)
        {
            (window.Left, window.Top, window.Width, window.Height) = (saved.Left, saved.Top, saved.Width, saved.Height);
        }
        else
        {
            // Half the canvas size (in DIPs), capped to the work area.
            var work = SystemParameters.WorkArea;
            var width = Math.Min(composition.CanvasWidth / 2.0, work.Width * 0.6);
            window.Width = width;
            window.Height = width * composition.CanvasHeight / composition.CanvasWidth;
            window.Left = work.Left + 80;
            window.Top = work.Top + 80;
        }

        // Closing the window (e.g. Alt+F4) closes the composition too.
        window.Closed += (_, _) =>
        {
            if (_windows.Remove(composition.Id))
            {
                composition.CloseCommand?.Execute(null);
            }
        };
        _windows[composition.Id] = window;
        window.Show();
    }

    public void Close(CompositionViewModel composition)
    {
        if (_windows.Remove(composition.Id, out var window))
        {
            window.Close();
        }
    }
}
