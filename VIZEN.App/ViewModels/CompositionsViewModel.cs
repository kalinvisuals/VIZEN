using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using System.Windows.Threading;
using VIZEN.App.Capture;
using VIZEN.App.Infrastructure;
using VIZEN.App.Localization;
using VIZEN.App.Models;
using VIZEN.App.Services;

namespace VIZEN.App.ViewModels;

/// <summary>
/// Compositions (Studio): scenes that stack several captures and media layers in one OBS window. Captures are
/// shared with every other consumer of the same source; sources are found again by app and title like profiles.
/// Persisted in compositions.json.
/// </summary>
public sealed class CompositionsViewModel : ObservableObject
{
    private const string FileName = "compositions.json";

    private readonly CaptureService _capture;
    private readonly Func<IReadOnlyList<WindowSource>> _currentSources;
    private readonly ICompositionPresenter _presenter;
    private readonly ActivityLog _activity;
    private readonly DispatcherTimer _saveTimer;
    private string? _errorMessage;

    public CompositionsViewModel(CaptureService capture, OutputsViewModel outputs, MediaLibraryViewModel library,
        Func<IReadOnlyList<WindowSource>> currentSources, ICompositionPresenter presenter, ActivityLog activity)
    {
        _capture = capture;
        Outputs = outputs;
        Library = library;
        _currentSources = currentSources;
        _presenter = presenter;
        _activity = activity;
        _saveTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(600), DispatcherPriority.Background, (_, _) => SaveNow(), Dispatcher.CurrentDispatcher);
        _saveTimer.Stop();

        NewCommand = new RelayCommand(() => Add(new Composition { Name = Loc.F("Composition.DefaultName", Items.Count + 1) }, log: true));

        var data = JsonStore.Load<CompositionsFile>(FileName, out var loadError);
        if (loadError is not null)
        {
            _activity.Add("", "Composition.LoadFailed", ActivityLog.RawDetail, loadError);
        }

        foreach (var composition in data?.Compositions ?? [])
        {
            Add(composition, log: false);
        }

        Items.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasItems));
    }

    public OutputsViewModel Outputs { get; }
    public MediaLibraryViewModel Library { get; }

    public ObservableCollection<CompositionViewModel> Items { get; } = [];

    public bool HasItems => Items.Count > 0;

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set => SetProperty(ref _errorMessage, value);
    }

    public ICommand NewCommand { get; }

    /// <summary>Opens compositions marked "open when VIZEN starts" (call after the first source scan).</summary>
    public void OpenAutomatic()
    {
        foreach (var item in Items.Where(i => i.AutoOpen && !i.IsOpen))
        {
            Open(item);
        }
    }

    public void Open(CompositionViewModel composition)
    {
        if (composition.IsOpen)
        {
            return;
        }

        foreach (var item in composition.Items)
        {
            AttachSource(item);
        }

        _presenter.Show(composition);
        composition.IsOpen = true;
        var missing = composition.Items.Count(i => i.MissingText is not null);
        _activity.Add("", "Composition.Opened", missing > 0 ? "Composition.OpenedMissingDetail" : "Composition.OpenedDetail",
            composition.WindowTitle, composition.CanvasText, composition.Items.Count, missing);
    }

    public void Close(CompositionViewModel composition)
    {
        if (!composition.IsOpen)
        {
            return;
        }

        foreach (var item in composition.Items)
        {
            item.Detach();
        }

        composition.IsOpen = false;
        _presenter.Close(composition);
        _activity.Add("", "Composition.Closed", "Composition.ClosedDetail", composition.WindowTitle);
    }

    public void CloseAll()
    {
        foreach (var composition in Items)
        {
            Close(composition);
        }
    }

    /// <summary>
    /// Open compositions: re-finds capture layers whose source is gone or was never found (same app and the same
    /// or a similar title only). Returns the names of the layers that are live again.
    /// </summary>
    public IReadOnlyList<string> ReconnectMissing(IReadOnlyList<WindowSource> windows)
    {
        var reconnected = new List<string>();
        foreach (var composition in Items.Where(c => c.IsOpen))
        {
            foreach (var item in composition.Items.Where(i => i.IsCapture && (i.Capture is null || !i.Capture.IsUsable)).ToList())
            {
                var match = ProfileMatcher.Match([item.Item.ToLayout()], windows).Single();
                if (match is not { Window: { } window, Quality: MatchQuality.Exact or MatchQuality.Similar } || !_capture.CanCapture(window))
                {
                    continue;
                }

                try
                {
                    var lease = _capture.Acquire(window);
                    item.Detach();
                    item.Attach(lease, null);
                    reconnected.Add($"{composition.Name} · {item.Name}");
                }
                catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException or ArgumentException
                    or System.Runtime.InteropServices.COMException)
                {
                    // Try again after the next scan.
                }
            }
        }

        return reconnected;
    }

    private CompositionViewModel Add(Composition model, bool log)
    {
        var composition = new CompositionViewModel(model, Library, RequestSave);
        composition.OpenCommand = new RelayCommand(() => Open(composition), () => !composition.IsOpen);
        composition.CloseCommand = new RelayCommand(() => Close(composition), () => composition.IsOpen);
        composition.ConfirmDeleteCommand = new RelayCommand(() => Delete(composition));
        composition.AddOutputCommand = new RelayCommand<OutputViewModel>(output =>
            composition.AddCapture(output.Source, output.Viewbox, output.RegionName));
        composition.ItemAdded += (_, item) =>
        {
            if (composition.IsOpen)
            {
                AttachSource(item);
            }
        };
        composition.ItemsEdited += (_, detail) => _activity.Add("", "Composition.Edited", "Composition.EditedDetail", composition.Name, detail);
        composition.EditingChanged += (_, editing) => _activity.Add("", editing ? "Composition.EditOn" : "Composition.EditOff",
            editing ? "Composition.EditOnDetail" : "Composition.EditOffDetail", composition.Name);
        composition.Resized += (_, request) =>
        {
            var applied = request.Applied ?? request.Requested;
            _activity.Add("", "Activity.OutputResized", "Activity.OutputResizedDetail", composition.WindowTitle,
                Loc.F("Editor.PixelSize", (int)applied.Width, (int)applied.Height));
        };
        Items.Add(composition);
        if (log)
        {
            RequestSave();
            _activity.Add("", "Composition.Created", "Composition.CreatedDetail", composition.Name, composition.CanvasText);
        }

        return composition;
    }

    private void Delete(CompositionViewModel composition)
    {
        Close(composition);
        Items.Remove(composition);
        composition.Detach();
        RequestSave();
        _activity.Add("", "Composition.Deleted", "Composition.DeletedDetail", composition.Name);
    }

    /// <summary>Finds a capture item's source among the current windows/cameras and starts (or shares) its capture.</summary>
    private void AttachSource(CompositionItemViewModel item)
    {
        if (!item.IsCapture)
        {
            item.Attach(null, item.MediaLayer is null ? Loc.T("Schedule.MissingLayer") : null);
            return;
        }

        var match = ProfileMatcher.Match([item.Item.ToLayout()], _currentSources()).Single();
        if (match.Window is not { } source || !_capture.CanCapture(source))
        {
            item.Attach(null, Loc.F("Composition.SourceMissing", item.Item.ProcessName, item.Item.WindowTitle));
            return;
        }

        try
        {
            item.Attach(_capture.Acquire(source), null);
        }
        catch (Exception ex)
        {
            item.Attach(null, Loc.F("Capture.StartFailed", ex.Message));
        }
    }

    private void RequestSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    public void SaveNow()
    {
        _saveTimer.Stop();
        try
        {
            JsonStore.Save(FileName, new CompositionsFile { Compositions = Items.Select(i => i.Composition).ToList() });
            ErrorMessage = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ErrorMessage = Loc.F("Composition.SaveFailed", ex.Message);
        }
    }

    private sealed class CompositionsFile
    {
        public int Version { get; set; } = 1;
        public List<Composition> Compositions { get; set; } = [];
    }
}
