using VIZEN.App.Models;
using VIZEN.App.ViewModels;

namespace VIZEN.App.Services;

/// <summary>Opens and closes Smart Draw's windows: one drawing surface per monitor plus the toolbar (view layer).</summary>
public interface ISmartDrawPresenter
{
    bool IsOpen { get; }

    /// <summary>Creates the windows; they follow the view model's mode, visibility and document from then on.</summary>
    void Open(SmartDrawViewModel drawing);

    /// <summary>Closes and releases every Smart Draw window.</summary>
    void Close();
}

/// <summary>The monitors connected now (view layer; a fixed list in tests).</summary>
public interface IMonitorProvider
{
    IReadOnlyList<MonitorInfo> Current();
}
