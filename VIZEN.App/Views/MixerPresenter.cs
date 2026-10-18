using System.Windows;
using VIZEN.App.Services;
using VIZEN.App.ViewModels;

namespace VIZEN.App.Views;

/// <summary>Owns the floating Smart Audio Mixer window (created on first show, kept while hidden).</summary>
public sealed class MixerPresenter : IMixerPresenter
{
    private const double DefaultWidth = 320;
    private const double EdgeMargin = 24;

    private MixerWindow? _window;

    public void Show(AudioMixerViewModel mixer)
    {
        if (_window is null)
        {
            _window = new MixerWindow(mixer);
            var saved = mixer.FloatingPosition is { } p ? new Rect(p.X, p.Y, DefaultWidth, 120) : (Rect?)null;
            _window.Place(ScreenPlacement.Restorable(saved)?.TopLeft ?? DefaultPosition());
        }

        _window.ShowMixer();
    }

    public void Hide() => _window?.Hide();

    public void Close()
    {
        if (_window is not null)
        {
            _window.AllowClose = true;
            _window.Close();
            _window = null;
        }
    }

    /// <summary>Right edge of the primary work area, a third of the way down (clear of notes and Smart Draw's toolbar).</summary>
    private static Point DefaultPosition()
    {
        var work = SystemParameters.WorkArea;
        return new Point(work.Right - DefaultWidth - EdgeMargin, work.Top + work.Height / 3);
    }
}
