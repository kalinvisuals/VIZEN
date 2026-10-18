using VIZEN.App.ViewModels;

namespace VIZEN.App.Services;

/// <summary>Opens the on-screen window for an OBS output. Keeps view types out of the view models.</summary>
public interface IOutputPresenter
{
    /// <param name="output">The output to show. Its window closes when <see cref="OutputViewModel.Closed"/> fires.</param>
    /// <param name="index">Position among open outputs, used to stagger initial placement.</param>
    void Show(OutputViewModel output, int index);
}
