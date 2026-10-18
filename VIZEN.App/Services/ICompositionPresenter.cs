using VIZEN.App.ViewModels;

namespace VIZEN.App.Services;

/// <summary>Opens and closes composition windows (implemented by the view layer).</summary>
public interface ICompositionPresenter
{
    void Show(CompositionViewModel composition);

    void Close(CompositionViewModel composition);
}
