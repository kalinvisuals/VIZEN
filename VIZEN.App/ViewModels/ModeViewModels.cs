using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Input;
using VIZEN.App.Infrastructure;
using VIZEN.App.Models;

namespace VIZEN.App.ViewModels;

/// <summary>
/// Studio / OBS mode: discovers browser, media and camera sources and manages clean output windows that
/// OBS Window Capture can grab (whole windows or cropped regions). Camera capture is a later milestone.
/// </summary>
public sealed class StudioViewModel : ObservableObject
{
    private readonly WindowSourcesViewModel _sources;

    public StudioViewModel(WindowSourcesViewModel sources, OutputsViewModel outputs, CompositionsViewModel compositions, Action<ObservableObject> navigate)
    {
        _sources = sources;
        Outputs = outputs;
        Compositions = compositions;
        CreateOutputCommand = new RelayCommand<WindowSource>(source => outputs.CreateOutput(source));
        CloseAllOutputsCommand = new RelayCommand(outputs.CloseAll, () => outputs.HasOutputs);
        StudioWindows = new ListCollectionView(sources.Sources)
        {
            Filter = o => o is WindowSource { Category: SourceCategory.Browsers or SourceCategory.Media or SourceCategory.Cameras },
        };
        sources.Refreshed += (_, _) =>
        {
            StudioWindows.Refresh();
            OnPropertyChanged(nameof(WindowCount));
            OnPropertyChanged(nameof(HasStudioWindows));
            OnPropertyChanged(nameof(CameraCount));
        };

        ShowInSourcesCommand = new RelayCommand<string>(category =>
        {
            sources.ShowCategory(Enum.TryParse<SourceCategory>(category, out var c) ? c : null);
            navigate(sources);
        });
        CropCommand = new RelayCommand<WindowSource>(source => EditRegionsRequested?.Invoke(source));
    }

    /// <summary>Set by the shell: opens the region editor for a source.</summary>
    public Action<WindowSource>? EditRegionsRequested { get; set; }

    public ICommand CropCommand { get; }

    public OutputsViewModel Outputs { get; }

    /// <summary>Scenes combining several captures and media layers in one OBS window.</summary>
    public CompositionsViewModel Compositions { get; }

    /// <summary>Opens a clean output window for a whole source window.</summary>
    public ICommand CreateOutputCommand { get; }

    public ICommand CloseAllOutputsCommand { get; }

    public WindowSourcesViewModel Sources => _sources;

    public ICollectionView StudioWindows { get; }

    public int WindowCount => _sources.Sources.Count(s => s.Category is SourceCategory.Browsers or SourceCategory.Media or SourceCategory.Cameras);

    public bool HasStudioWindows => WindowCount > 0;

    public int CameraCount => _sources.Cameras.Count;

    public ICommand ShowInSourcesCommand { get; }
}
