using VIZEN.App.ViewModels;

namespace VIZEN.App.Services;

/// <summary>Opens and hides the floating Smart Audio Mixer window (implemented by the view layer).</summary>
public interface IMixerPresenter
{
    /// <summary>Shows the floating mixer without taking the focus (a game or app keeps it).</summary>
    void Show(AudioMixerViewModel mixer);

    /// <summary>Hides it (the window is kept for the next show).</summary>
    void Hide();

    /// <summary>Closes the window for good (VIZEN exiting).</summary>
    void Close();
}
