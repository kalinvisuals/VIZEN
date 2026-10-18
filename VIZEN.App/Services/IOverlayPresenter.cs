using VIZEN.App.ViewModels;

namespace VIZEN.App.Services;

/// <summary>
/// Opens the on-screen window for an overlay. Keeps view types out of the view models.
/// </summary>
public interface IOverlayPresenter
{
    /// <param name="overlay">The overlay to show. Its window closes when <see cref="OverlayViewModel.Closed"/> fires.</param>
    /// <param name="index">Position among open overlays, used to stagger initial placement.</param>
    void Show(OverlayViewModel overlay, int index);
}
