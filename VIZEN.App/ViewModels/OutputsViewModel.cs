using System.Collections.ObjectModel;
using System.Windows;
using VIZEN.App.Capture;
using VIZEN.App.Infrastructure;
using VIZEN.App.Localization;
using VIZEN.App.Models;
using VIZEN.App.Services;

namespace VIZEN.App.ViewModels;

/// <summary>Manages OBS output windows (Studio mode). Shares captures with overlays and previews.</summary>
public sealed class OutputsViewModel : ObservableObject
{
    private readonly CaptureService _capture;
    private readonly IOutputPresenter _presenter;
    private readonly ActivityLog _activity;
    private string? _errorMessage;

    public OutputsViewModel(CaptureService capture, IOutputPresenter presenter, ActivityLog activity)
    {
        _capture = capture;
        _presenter = presenter;
        _activity = activity;
        Outputs.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(Count));
            OnPropertyChanged(nameof(HasOutputs));
        };
    }

    public ObservableCollection<OutputViewModel> Outputs { get; } = [];

    public int Count => Outputs.Count;

    public bool HasOutputs => Outputs.Count > 0;

    public bool IsCaptureSupported => _capture.IsSupported;

    /// <summary>Set by the shell: sound controls for a source's app.</summary>
    public Func<WindowSource, AppAudioViewModel?>? AudioFor { get; set; }

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set => SetProperty(ref _errorMessage, value);
    }

    /// <summary>
    /// Opens a clean output window for <paramref name="source"/> (or a normalized <paramref name="region"/> of it),
    /// optionally restoring a saved <paramref name="layout"/>. Returns false if capture could not start.
    /// </summary>
    public bool CreateOutput(WindowSource source, Rect? region = null, string? regionName = null, OverlayLayout? layout = null)
    {
        ErrorMessage = null;
        if (!_capture.CanCapture(source))
        {
            ErrorMessage = Loc.T("Capture.Unsupported");
            return false;
        }

        CaptureLease lease;
        try
        {
            lease = _capture.Acquire(source);
        }
        catch (Exception ex)
        {
            ErrorMessage = Loc.F("Capture.StartFailed", ex.Message);
            _activity.Add("", "Activity.OutputFailed", ActivityLog.RawDetail, ErrorMessage);
            return false;
        }

        if (lease.Capture.Status == CaptureStatus.Failed)
        {
            ErrorMessage = $"{source.Title}: {lease.Capture.StatusText}";
            _activity.Add("", "Activity.OutputFailed", ActivityLog.RawDetail, ErrorMessage);
            lease.Dispose();
            return false;
        }

        var effectiveRegion = region ?? layout?.Region ?? RegionMath.Full;
        var effectiveName = RegionMath.IsFull(effectiveRegion) ? null : regionName ?? layout?.RegionName;
        var output = new OutputViewModel(source, lease, effectiveRegion, effectiveName, UniqueTitle(source, effectiveName))
        {
            InitialBounds = layout is { Width: > 0, Height: > 0 }
                ? new Rect(layout.Left, layout.Top, layout.Width, layout.Height)
                : null,
        };
        if (layout is not null)
        {
            output.Background = Enum.TryParse<OutputBackground>(layout.OutputBackground, out var bg) ? bg : OutputBackground.Black;
            output.Fit = Enum.TryParse<OutputFit>(layout.OutputFit, out var fit) ? fit : OutputFit.Fit;
        }

        output.Audio = AudioFor?.Invoke(source);
        output.Closed += (_, _) =>
        {
            Outputs.Remove(output);
            _activity.Add("", "Activity.OutputClosed", "Activity.OutputClosedDetail", output.WindowTitle);
        };

        output.Resized += (_, request) =>
        {
            // Report what the window really became, not just what was asked for.
            var applied = request.Applied ?? request.Requested;
            var appliedText = Loc.F("Editor.PixelSize", (int)applied.Width, (int)applied.Height);
            if (applied == request.Requested)
            {
                _activity.Add("\uE740", "Activity.OutputResized", "Activity.OutputResizedDetail", output.WindowTitle, appliedText);
            }
            else
            {
                _activity.Add("\uE740", "Activity.OutputResized", "Activity.OutputResizedLimitedDetail", output.WindowTitle, appliedText,
                    Loc.F("Editor.PixelSize", (int)request.Requested.Width, (int)request.Requested.Height));
            }
        };
        output.BringToFrontRequested += (_, _) => _activity.Add("\uE8A7", "Activity.OutputShown", "Activity.OutputShownDetail", output.WindowTitle);
        output.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(OutputViewModel.Fit):
                    _activity.Add("\uE9A6", "Activity.OutputFitChanged", "Activity.OutputSettingDetail",
                        output.WindowTitle, Loc.T($"Studio.{output.Fit}"));
                    break;
                case nameof(OutputViewModel.Background):
                    _activity.Add("\uE790", "Activity.OutputBackgroundChanged", "Activity.OutputSettingDetail",
                        output.WindowTitle, Loc.T(output.Background == OutputBackground.Green ? "Studio.BgGreen" : "Studio.BgBlack"));
                    break;
            }
        };

        Outputs.Add(output);
        _presenter.Show(output, Outputs.Count - 1);
        _activity.Add("", "Activity.OutputCreated", "Activity.OutputCreatedDetail", output.WindowTitle,
            output.ContentPixelSize is { } px
                ? Loc.F("Editor.PixelSize", (int)px.Width, (int)px.Height)
                : Loc.T("Activity.RegionSizePending"));
        return true;
    }

    public void CloseAll()
    {
        foreach (var output in Outputs.ToList())
        {
            output.Close();
        }
    }

    /// <summary>
    /// "VIZEN Output · app · region". Deterministic, so the same setup gets the same title next session
    /// (OBS re-finds windows by title). A number is appended only if that exact title is already open.
    /// </summary>
    private string UniqueTitle(WindowSource source, string? regionName)
    {
        var baseTitle = regionName is null
            ? $"VIZEN Output · {source.ProcessName}"
            : $"VIZEN Output · {source.ProcessName} · {regionName}";

        var title = baseTitle;
        for (var n = 2; Outputs.Any(o => o.WindowTitle == title); n++)
        {
            title = $"{baseTitle} ({n})";
        }

        return title;
    }
}
