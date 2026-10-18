using VIZEN.App.ViewModels;

namespace VIZEN.App.Services;

/// <summary>Opens and controls the on-screen windows of media layers (implemented by the view layer).</summary>
public interface IMediaPresenter
{
    /// <summary>Opens (or reuses) the layer's window and shows it with its in-transition. False if the file cannot be shown.</summary>
    Task<bool> ShowAsync(MediaLayerViewModel layer, int index);

    /// <summary>Hides the layer with its out-transition (the window is kept for the next show).</summary>
    void Hide(MediaLayerViewModel layer);

    /// <summary>Closes and forgets the layer's window.</summary>
    void Close(MediaLayerViewModel layer);

    /// <summary>Stacks overlay windows so later items are in front.</summary>
    void ApplyZOrder(IEnumerable<MediaLayerViewModel> backToFront);
}

/// <summary>Lets the user pick media files (implemented by the view layer).</summary>
public interface IMediaFilePicker
{
    IReadOnlyList<string> PickFiles();
}
